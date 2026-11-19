using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QualityAviationCodeRepositoryConsoleClient.Console.Forms;
using QualityAviationCodeRepositoryConsoleClient.Console.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    // STA is what the Windows folder and file dialogs require
    [STAThread]
    private static int Main(string[] args)
    {
        var windowsApp = false;
        // Taken before anything can update the installation: after an update this path holds the new client
        var exePath = Environment.ProcessPath;
        try
        {
            System.Console.OutputEncoding = Encoding.UTF8;
            windowsApp = AskForWindowsApp(args);

            // Read next to the exe, so shortcuts and "run from anywhere" find the same settings.
            // --no-update: run this copy as it is, e.g. to try a new build before it is published.
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddInMemoryCollection(args.Contains("--no-update", StringComparer.OrdinalIgnoreCase)
                    ? new Dictionary<string, string?> { ["Update:Disabled"] = "true" }
                    : new Dictionary<string, string?>())
                .Build();

            using var serviceProvider = new ServiceCollection()
                .AddSingleton<IConfiguration>(config)
                .AddSingleton<IApiService, ApiService>()
                .AddSingleton<IFileService, FileService>()
                .AddSingleton<IAuthService, AuthService>()
                .AddSingleton<IRepoOperations, RepoOperations>()
                .AddSingleton<IClientUpdater, ClientUpdater>()
                .AddSingleton<IDeployClient, DeployClient>()
                .AddSingleton<DeployConsole>()
                .AddSingleton<IHelpdeskClient, HelpdeskClient>()
                .AddSingleton<HelpdeskConsole>()
                .AddSingleton<IConsoleUI, ConsoleUI>()
                .AddSingleton<MainForm>()
                .BuildServiceProvider();

            var updater = serviceProvider.GetRequiredService<IClientUpdater>();
            updater.CleanUpInBackground();

            if (windowsApp)
            {
                FreeConsole();
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(serviceProvider.GetRequiredService<MainForm>());

                if (updater.RestartPending) Restart(exePath, "--gui", waitForIt: false);
            }
            else
            {
                serviceProvider.GetRequiredService<IConsoleUI>().Run().GetAwaiter().GetResult();

                // The new client runs in this same console window; this process only waits for it
                if (updater.RestartPending) return Restart(exePath, "--terminal", waitForIt: true);
            }

            return 0;
        }
        catch (Exception ex)
        {
            if (windowsApp)
                MessageBox.Show(ex.Message, "QA Repository Management", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                System.Console.WriteLine($"❌ {ex.Message}");
            return 1;
        }
    }

    // Starts the client that an update just installed. The saved login carries over, so it opens signed in.
    private static int Restart(string exePath, string mode, bool waitForIt)
    {
        var start = new ProcessStartInfo(exePath!) { UseShellExecute = false, CreateNoWindow = !waitForIt };
        start.ArgumentList.Add(mode);

        using var client = Process.Start(start);
        if (client == null || !waitForIt) return 0;

        client.WaitForExit();
        return client.ExitCode;
    }

    // Asked on every start. --terminal / --gui answer it up front, for shortcuts and scripts.
    private static bool AskForWindowsApp(string[] args)
    {
        if (args.Contains("--gui", StringComparer.OrdinalIgnoreCase)) return true;
        if (args.Contains("--terminal", StringComparer.OrdinalIgnoreCase)) return false;

        System.Console.WriteLine("\n╔════════════════════════════════════╗");
        System.Console.WriteLine("║      QA REPOSITORY MANAGEMENT      ║");
        System.Console.WriteLine("╠════════════════════════════════════╣");
        System.Console.WriteLine("║  1  Terminal                       ║");
        System.Console.WriteLine("║  2  Windows application            ║");
        System.Console.WriteLine("╚════════════════════════════════════╝");

        while (true)
        {
            System.Console.Write("➤ How do you want to work? [1/2]: ");
            var choice = System.Console.ReadLine()?.Trim();

            if (choice is null or "1") return false;
            if (choice == "2") return true;

            System.Console.WriteLine("❌ Invalid option");
        }
    }
}
