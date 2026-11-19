using System.Diagnostics;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    public enum DevTool { VsCode, Explorer, VisualStudio }

    // Opens a local copy of a repository in VS Code, File Explorer or Visual Studio. Used by both the
    // Windows application (the buttons above the version list) and the terminal menu (O).
    public static class DevTools
    {
        // Folders that never hold the project to open, and can be huge
        private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".vs", ".git", ".vscode", "node_modules", "packages", ".qaupdate"
        };

        private static readonly Lazy<string> VsCodePath = new(LocateVsCode);
        private static readonly Lazy<string> VisualStudioPath = new(LocateVisualStudio);

        public static string Label(DevTool tool) => tool switch
        {
            DevTool.VsCode => "VS Code",
            DevTool.Explorer => "File Explorer",
            _ => "Visual Studio"
        };

        // The program that opens this tool, or null when it is not installed on this PC
        public static string ProgramPath(DevTool tool) => tool switch
        {
            DevTool.VsCode => VsCodePath.Value,
            DevTool.Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            _ => VisualStudioPath.Value
        };

        public static bool IsInstalled(DevTool tool) => ProgramPath(tool) != null;

        // Solutions first (they load every project in them); without one, the project files.
        // Shallowest first, so the repository's main solution or project leads the list.
        public static List<string> FindProjects(string folder)
        {
            var solutions = new List<string>();
            var projects = new List<string>();
            Walk(folder, 0);

            return (solutions.Count > 0 ? solutions : projects)
                .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            void Walk(string directory, int depth)
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    switch (Path.GetExtension(file).ToLowerInvariant())
                    {
                        case ".sln": case ".slnx": solutions.Add(file); break;
                        case ".csproj": case ".vbproj": case ".fsproj": projects.Add(file); break;
                    }
                }

                if (depth >= 6) return;
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    if (SkippedFolders.Contains(Path.GetFileName(sub))) continue;
                    if (File.GetAttributes(sub).HasFlag(FileAttributes.ReparsePoint)) continue;
                    Walk(sub, depth + 1);
                }
            }
        }

        // target: the folder, or for Visual Studio the solution/project file to load (the folder when there is none)
        public static void Open(DevTool tool, string target)
        {
            var program = ProgramPath(tool) ?? throw new InvalidOperationException($"{Label(tool)} was not found on this PC.");
            if (!File.Exists(target) && !Directory.Exists(target)) throw new InvalidOperationException($"{target} does not exist.");

            // Started through the shell, so the editor gets its own window and never shares the terminal's console
            Process.Start(new ProcessStartInfo(program, Quote(target))
            {
                UseShellExecute = true,
                WorkingDirectory = Directory.Exists(target) ? target : Path.GetDirectoryName(target)
            })?.Dispose();
        }

        private static string Quote(string path)
        {
            // A trailing backslash would escape the closing quote ("D:\" -> D:")
            if (path.EndsWith('\\')) path += ".";
            return $"\"{path}\"";
        }

        private static string LocateVsCode()
        {
            // The installer puts ...\Microsoft VS Code\bin on PATH; Code.exe is one folder up
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (!File.Exists(Path.Combine(dir, "code.cmd"))) continue;
                    var exe = Path.GetFullPath(Path.Combine(dir, "..", "Code.exe"));
                    if (File.Exists(exe)) return exe;
                }
                catch (ArgumentException) { /* a malformed PATH entry */ }
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return new[]
                {
                    Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"),
                    Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"),
                    Path.Combine(local, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe"),
                    Path.Combine(programFiles, "Microsoft VS Code Insiders", "Code - Insiders.exe")
                }
                .FirstOrDefault(File.Exists);
        }

        private static string LocateVisualStudio()
        {
            // vswhere ships with every Visual Studio since 2017 and knows where each edition is installed
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere)) return null;

            foreach (var arguments in new[] { "-latest -property productPath", "-latest -prerelease -property productPath" })
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(vswhere, arguments)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    });
                    if (process == null) continue;

                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);

                    var devenv = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith("devenv.exe", StringComparison.OrdinalIgnoreCase));
                    if (devenv != null && File.Exists(devenv)) return devenv;
                }
                catch (Exception)
                {
                    // vswhere could not run: treat as not installed
                }
            }
            return null;
        }
    }
}
