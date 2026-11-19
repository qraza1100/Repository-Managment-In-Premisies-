using QualityAviationCodeRepositoryConsoleClient.Console.Models;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    public interface IConsoleUI
    {
        Task Run();
    }

    public class ConsoleUI : IConsoleUI
    {
        private readonly IAuthService _authService;
        private readonly IApiService _apiService;
        private readonly IFileService _fileService;
        private readonly IRepoOperations _repoOperations;
        private readonly DeployConsole _deployConsole;
        private readonly HelpdeskConsole _helpdeskConsole;
        private readonly IClientUpdater _updater;
        private bool _exit;
        private bool _updateChecked;

        public ConsoleUI(IAuthService authService, IApiService apiService, IFileService fileService, IRepoOperations repoOperations, DeployConsole deployConsole,
            IClientUpdater updater, HelpdeskConsole helpdeskConsole)
        {
            _helpdeskConsole = helpdeskConsole;
            _authService = authService;
            _apiService = apiService;
            _fileService = fileService;
            _repoOperations = repoOperations;
            _deployConsole = deployConsole;
            _updater = updater;

            _apiService.StatusChanged += message => System.Console.WriteLine($"⏳ {message}");
        }

        public async Task Run()
        {
            try
            {
                if (!await Connect()) return;

                while (!_exit)
                {
                    if (!_authService.IsAuthenticated)
                    {
                        await HandleLogin();
                    }
                    else if (!_updateChecked)
                    {
                        // Once after every sign-in; when a newer client was installed, Program starts it in this window
                        _updateChecked = true;
                        if (await UpdateClient()) return;
                    }
                    else
                    {
                        await HandleMainMenu();
                    }
                }
            }
            catch (EndOfStreamException)
            {
                // Input was closed (window closed or end of a piped script)
            }
        }

        private async Task<bool> Connect()
        {
            while (true)
            {
                try
                {
                    System.Console.WriteLine("\n🔗 Finding the server ...");
                    await _authService.ConnectAsync();
                    System.Console.WriteLine($"✅ Secure connection to {_apiService.CurrentServer}");
                    return true;
                }
                catch (ApiException ex)
                {
                    System.Console.WriteLine($"❌ {ex.Message}");
                    if (Prompt("🔁 Retry? (y/n): ").ToLower() != "y") return false;
                }
            }
        }

        private async Task HandleLogin()
        {
            System.Console.WriteLine("\n╔════════════════════════════════════╗");
            System.Console.WriteLine("║        REPOSITORY MANAGEMENT       ║");
            System.Console.WriteLine("║               LOGIN                ║");
            System.Console.WriteLine("╚════════════════════════════════════╝\n");

            var username = Prompt("📝 Username (empty to exit): ");
            if (string.IsNullOrWhiteSpace(username)) { _exit = true; return; }

            var password = ReadPassword("🔐 Password: ");

            try
            {
                await _authService.LoginAsync(username, password);
                System.Console.WriteLine($"\n✅ Welcome, {_authService.CurrentUsername}!");
            }
            catch (ApiException ex)
            {
                System.Console.WriteLine($"\n❌ Login failed: {ex.Message}");
            }
        }

        private async Task HandleMainMenu()
        {
            ClearScreen();
            System.Console.WriteLine($"\n👤 Logged in as: {_authService.CurrentUsername}\n");

            PrintMainMenu();

            var choice = Prompt("➤ Select option: ").ToUpperInvariant();

            try
            {
                switch (choice)
                {
                    case "1": await ShowRepositories(); break;
                    case "2": await PushRepository(); break;
                    case "3": await PullRepository(); break;
                    case "4": await CloneRepository(); break;
                    case "5": await CreateRepository(); break;
                    case "6": await ShowRepositoryDetails(); break;
                    case "7": await HandleSwitchVersion(); break; // Added Switch/Downgrade Logic
                    case "8": await ChangePassword(); break;
                    case "O": await OpenLocalCopy(); break;
                    case "D": await _deployConsole.RunAsync(); return;
                    case "H": await _helpdeskConsole.RunAsync(); return;
                    case "9": _exit = true; return;
                    case "0":
                        _authService.Logout();
                        _updateChecked = false;
                        System.Console.WriteLine("✅ Logged out successfully!");
                        return;
                    default:
                        System.Console.WriteLine("❌ Invalid option");
                        break;
                }
            }
            catch (ApiException ex) when (ex.IsUnauthorized)
            {
                _authService.Logout();
                _updateChecked = false;
                System.Console.WriteLine("\n🔒 Your session has expired. Please log in again.");
                return;
            }
            catch (Exception ex) when (ex is not EndOfStreamException)
            {
                // Whatever went wrong (network, disk, locked file), report it and keep the menu alive
                System.Console.WriteLine($"\n❌ {ex.Message}");
            }

            Pause();
        }

        private async Task ShowRepositories()
        {
            System.Console.WriteLine("\n📚 Fetching repositories...");
            var repos = await _repoOperations.ListAsync();

            System.Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            System.Console.WriteLine("📦 YOUR REPOSITORIES");
            System.Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            foreach (var repo in repos.OwnedRepos)
            {
                System.Console.WriteLine($"\n📁 {repo.Name} (Owner)");
                if (repo.LatestVersion != null)
                {
                    System.Console.WriteLine($"   Latest: {repo.LatestVersion.Name} | Files: {repo.LatestVersion.FileCount} | Versions: {repo.VersionCount}");
                }
            }

            if (repos.ClonedRepos.Count > 0)
            {
                System.Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                System.Console.WriteLine("🔗 ACCESSIBLE REPOSITORIES");
                System.Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

                foreach (var repo in repos.ClonedRepos)
                {
                    System.Console.WriteLine($"\n📁 {repo.Name} (By {repo.Owner})");
                }
            }
        }

        private async Task PushRepository()
        {
            var path = Prompt("\n📁 Local Path: ");

            if (!Directory.Exists(path)) { System.Console.WriteLine("❌ Directory not found."); return; }

            var state = _fileService.GetLocalState(path);
            string repoName = null;

            if (state != null)
            {
                System.Console.WriteLine($"[LOG] Locked Target: {state.Owner}/{state.RepoName}");
            }
            else
            {
                repoName = Prompt("📝 New Repo Name: ");
            }

            if (Prompt("❓ Add items to .qaignore? (y/n): ").ToLower() == "y")
            {
                var items = Prompt("📝 Enter names (comma separated): ").Split(',');
                _fileService.AddIgnoreEntries(path, items);
            }

            var msg = Prompt("💬 Message: ");

            var result = await _repoOperations.PushAsync(path, repoName, msg, new ConsoleProgress("Pushing"));

            System.Console.WriteLine(result.Changed
                ? $"\n✅ Pushed {result.FileCount} files as {result.Version}."
                : $"\n✅ Nothing changed: the server is already at {result.Version}.");
            System.Console.WriteLine($"   {result.Note}");
        }

        private async Task HandleSwitchVersion()
        {
            var path = Prompt("\n📁 Repo Path: ");
            var state = _fileService.GetLocalState(path);

            if (state == null) { System.Console.WriteLine("❌ Not a tracked repository."); return; }

            var version = Prompt($"📌 Target Version (Current: {state.CurrentVersion}): ");
            if (string.IsNullOrWhiteSpace(version)) { System.Console.WriteLine("❌ Version is required."); return; }

            var switched = await _repoOperations.PullAsync(path, state.Owner, state.RepoName, version, new ConsoleProgress("Switching"));
            System.Console.WriteLine($"\n✅ Switched to {switched.Version}");
            System.Console.WriteLine($"   {switched.Note}");
        }

        private async Task CloneRepository()
        {
            var owner = Prompt("\n👤 Source Owner: ");
            var repoName = Prompt("📝 Repo Name: ");
            var clonePath = Prompt("📂 Destination Folder: ");

            var cloned = await _repoOperations.CloneAsync(owner, repoName, clonePath, new ConsoleProgress("Cloning"));
            System.Console.WriteLine($"\n✅ Cloned source {owner}/{repoName} ({cloned.Version}) successfully.");
            System.Console.WriteLine($"   {cloned.Note}");
        }

        private async Task PullRepository()
        {
            var path = Prompt("\n📁 Local Path: ");
            if (string.IsNullOrWhiteSpace(path)) { System.Console.WriteLine("❌ Path is required."); return; }

            var state = _fileService.GetLocalState(path);

            string owner = state?.Owner, repo = state?.RepoName;
            if (state == null)
            {
                owner = Prompt("👤 Owner: ");
                repo = Prompt("📝 Repo: ");
            }

            var pulled = await _repoOperations.PullAsync(path, owner, repo, null, new ConsoleProgress("Pulling"));
            System.Console.WriteLine($"\n✅ Pull complete ({pulled.Version}).");
            System.Console.WriteLine($"   {pulled.Note}");
        }

        private async Task CreateRepository()
        {
            var name = Prompt("\n📝 Name: ");
            var desc = Prompt("📄 Desc: ");

            await _repoOperations.CreateAsync(name, desc);
            System.Console.WriteLine("✅ Created.");
        }

        private async Task ShowRepositoryDetails()
        {
            var owner = Prompt("\n👤 Owner: ");
            var repo = Prompt("📝 Repo: ");
            var versions = await _repoOperations.GetVersionsAsync(owner, repo);

            if (versions.Count == 0) System.Console.WriteLine("ℹ️  No versions yet.");

            foreach (var v in versions)
                System.Console.WriteLine($"📌 {v.Name} | {v.CreatedAt:yyyy-MM-dd HH:mm} | By: {v.CreatedBy} | Files: {v.FileCount} | {v.Message}");
        }

        private async Task<bool> UpdateClient()
        {
            System.Console.WriteLine("\n🔄 Checking for a newer client ...");
            var result = await _updater.UpdateAsync(new ConsoleProgress("Downloading the new client"));
            if (result == null) return false;

            System.Console.WriteLine(result.Installed ? $"\n✅ {result.Message}" : $"   {result.Message}");
            if (!result.Installed) return false;

            System.Console.WriteLine("🔁 Restarting the client (you stay signed in) ...");
            _exit = true;
            return true;
        }

        // Pick a repository, then open its copy on this PC in VS Code, File Explorer or Visual Studio
        private async Task OpenLocalCopy()
        {
            var repos = await _repoOperations.ListAsync();
            var all = repos.OwnedRepos.Concat(repos.ClonedRepos).ToList();
            if (all.Count == 0) { System.Console.WriteLine("ℹ️  No repositories yet."); return; }

            System.Console.WriteLine();
            for (var i = 0; i < all.Count; i++)
            {
                var folder = _fileService.FindLocalFolder(all[i].Owner, all[i].Name);
                System.Console.WriteLine($"{i + 1,3}  {all[i].Owner}/{all[i].Name,-28} {folder ?? "(not on this PC yet)"}");
            }

            if (!int.TryParse(Prompt("\n➤ Repository number: "), out var number) || number < 1 || number > all.Count)
            {
                System.Console.WriteLine("❌ Invalid number");
                return;
            }
            var repo = all[number - 1];

            var path = _fileService.FindLocalFolder(repo.Owner, repo.Name);
            if (path == null)
            {
                System.Console.WriteLine($"\nNo copy of {repo.Owner}/{repo.Name} is known on this PC yet.");
                path = Prompt("📂 Folder that holds it (empty = clone it now): ");
                if (string.IsNullOrWhiteSpace(path))
                {
                    var parent = Prompt("📂 Clone into which folder: ");
                    if (string.IsNullOrWhiteSpace(parent)) return;

                    path = Path.Combine(parent, repo.Name);
                    var cloned = await _repoOperations.CloneAsync(repo.Owner, repo.Name, path, new ConsoleProgress("Cloning"));
                    System.Console.WriteLine($"\n✅ Cloned {cloned.Version} into {path}");
                }
                else
                {
                    var state = _fileService.GetLocalState(path);
                    if (state == null || !string.Equals(state.Owner, repo.Owner, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(state.RepoName, repo.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        System.Console.WriteLine(state == null
                            ? "❌ That folder is not linked to a repository (no .qarepo). Pull or Clone into it first."
                            : $"❌ That folder is a copy of {state.Owner}/{state.RepoName}, not of {repo.Owner}/{repo.Name}.");
                        return;
                    }
                    _fileService.RememberLocalFolder(path, state);
                }
            }

            System.Console.WriteLine($"\n📁 {path}");
            var tools = new[] { DevTool.VsCode, DevTool.Explorer, DevTool.VisualStudio };
            for (var i = 0; i < tools.Length; i++)
                System.Console.WriteLine($"  {i + 1}  {DevTools.Label(tools[i])}{(DevTools.IsInstalled(tools[i]) ? "" : " (not installed)")}");

            if (!int.TryParse(Prompt("➤ Open in: "), out var choice) || choice < 1 || choice > tools.Length)
            {
                System.Console.WriteLine("❌ Invalid option");
                return;
            }
            var tool = tools[choice - 1];

            var target = path;
            if (tool == DevTool.VisualStudio)
            {
                var projects = DevTools.FindProjects(path);
                if (projects.Count > 1)
                {
                    for (var i = 0; i < projects.Count; i++)
                        System.Console.WriteLine($"  {i + 1}  {Path.GetRelativePath(path, projects[i])}");
                    if (!int.TryParse(Prompt("➤ Solution / project: "), out var pick) || pick < 1 || pick > projects.Count)
                    {
                        System.Console.WriteLine("❌ Invalid number");
                        return;
                    }
                    target = projects[pick - 1];
                }
                else if (projects.Count == 1)
                {
                    target = projects[0];
                }
            }

            DevTools.Open(tool, target);
            System.Console.WriteLine($"✅ Opened {target} in {DevTools.Label(tool)}");
        }

        private async Task ChangePassword()
        {
            var current = ReadPassword("\n🔐 Current Password: ");
            var updated = ReadPassword("🔑 New Password: ");

            if (updated != ReadPassword("🔑 Confirm New Password: ")) { System.Console.WriteLine("❌ Passwords do not match."); return; }

            await _authService.ChangePasswordAsync(current, updated);
            System.Console.WriteLine("✅ Password changed.");
        }

        private void PrintMainMenu()
        {
            System.Console.WriteLine("╭─────────────────────────────────────╮");
            System.Console.WriteLine("│           MAIN MENU                 │");
            System.Console.WriteLine("├─────────────────────────────────────┤");
            System.Console.WriteLine("│ 1️⃣  View Repositories               │");
            System.Console.WriteLine("│ 2️⃣  Push (Upload)                   │");
            System.Console.WriteLine("│ 3️⃣  Pull (Sync Latest)              │");
            System.Console.WriteLine("│ 4️⃣  Clone Repository                │");
            System.Console.WriteLine("│ 5️⃣  Create Repository               │");
            System.Console.WriteLine("│ 6️⃣  View Details                    │");
            System.Console.WriteLine("│ 7️⃣  Switch/Downgrade Version        │");
            System.Console.WriteLine("│ 8️⃣  Change Password                 │");
            System.Console.WriteLine("│ 📂 O Open in VS Code / Explorer / VS│");
            System.Console.WriteLine("│ 🚀 D Deployments (control panel)    │");
            System.Console.WriteLine("│ 🎫 H Helpdesk (tickets, to-do)      │");
            System.Console.WriteLine("│ 9️⃣  Exit                            │");
            System.Console.WriteLine("│ 0️⃣  Logout                          │");
            System.Console.WriteLine("╰─────────────────────────────────────╯");
        }

        private static string Prompt(string label)
        {
            System.Console.Write(label);
            return System.Console.ReadLine()?.Trim().Trim('"') ?? throw new EndOfStreamException();
        }

        private static string ReadPassword(string label)
        {
            // Piped input (scripts) cannot be read key by key
            if (System.Console.IsInputRedirected) return Prompt(label);

            System.Console.Write(label);
            var pwd = "";
            while (true)
            {
                var i = System.Console.ReadKey(true);
                if (i.Key == ConsoleKey.Enter) break;
                if (i.Key == ConsoleKey.Backspace) { if (pwd.Length > 0) pwd = pwd[..^1]; }
                else if (!char.IsControl(i.KeyChar)) pwd += i.KeyChar;
            }
            System.Console.WriteLine();
            return pwd;
        }

        private static void Pause()
        {
            if (System.Console.IsInputRedirected) return;
            System.Console.Write("\nPress any key to continue...");
            System.Console.ReadKey();
        }

        private static void ClearScreen()
        {
            if (!System.Console.IsOutputRedirected) System.Console.Clear();
        }

        // Reports on the calling thread, so the bar is drawn in order
        private class ConsoleProgress : IProgress<TransferProgress>
        {
            private readonly string _label;

            public ConsoleProgress(string label)
            {
                _label = label;
            }

            public void Report(TransferProgress value)
            {
                int barLength = 30;
                int filled = value.Percent * barLength / 100;
                string bar = new string('█', filled) + new string('░', barLength - filled);
                System.Console.Write($"\r{_label} |{bar}| {value.Percent,3}% | {FormatFileSize(value.Bytes)} of {FormatFileSize(value.Total)}    ");
            }

            private static string FormatFileSize(long bytes)
            {
                string[] sizes = { "B", "KB", "MB", "GB" };
                double len = bytes;
                int order = 0;
                while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
                return $"{len:0.##} {sizes[order]}";
            }
        }
    }
}
