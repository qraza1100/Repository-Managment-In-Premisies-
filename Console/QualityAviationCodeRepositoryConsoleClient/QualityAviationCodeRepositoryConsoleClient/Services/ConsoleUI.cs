using Microsoft.Extensions.Configuration;
using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using System.IO.Compression;
using System.Text.Json;

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
        private readonly IConfiguration _configuration;

        public ConsoleUI(IAuthService authService, IApiService apiService, IFileService fileService, IConfiguration configuration)
        {
            _authService = authService;
            _apiService = apiService;
            _fileService = fileService;
            _configuration = configuration;
        }

        public async Task Run()
        {
            PrintWelcome();
            _authService.LoadSession();

            while (true)
            {
                if (!_authService.IsAuthenticated)
                {
                    await HandleLogin();
                }
                else
                {
                    await HandleMainMenu();
                }
            }
        }

        private async Task HandleLogin()
        {
            System.Console.WriteLine("\n╔════════════════════════════════════╗");
            System.Console.WriteLine("║       REPOSITORY MANAGEMENT        ║");
            System.Console.WriteLine("║           GIT CLONE                ║");
            System.Console.WriteLine("╚════════════════════════════════════╝\n");

            System.Console.Write("📝 Username: ");
            var username = System.Console.ReadLine();

            System.Console.Write("🔐 Password: ");
            var password = ReadPassword();

            if (await _authService.LoginAsync(username, password))
            {
                System.Console.WriteLine($"\n✅ Welcome, {_authService.CurrentUsername}!");
            }
        }

        private async Task HandleMainMenu()
        {
            System.Console.Clear();
            System.Console.WriteLine($"\n👤 Logged in as: {_authService.CurrentUsername}\n");

            PrintMainMenu();

            System.Console.Write("➤ Select option: ");
            var choice = System.Console.ReadLine();

            switch (choice)
            {
                case "1":
                    await ShowRepositories();
                    break;
                case "2":
                    await PushRepository();
                    break;
                case "3":
                    await PullRepository();
                    break;
                case "4":
                    await CloneRepository();
                    break;
                case "5":
                    await CreateRepository();
                    break;
                case "6":
                    await ShowRepositoryDetails();
                    break;
                case "0":
                    _authService.Logout();
                    System.Console.WriteLine("✅ Logged out successfully!");
                    break;
                default:
                    System.Console.WriteLine("❌ Invalid option");
                    break;
            }

            if (choice != "0")
            {
                System.Console.Write("\nPress any key to continue...");
                System.Console.ReadKey();
            }
        }

        private async Task ShowRepositories()
        {
            System.Console.WriteLine("\n📚 Fetching repositories...");
            var response = await _apiService.GetAsync<ApiResponse<ListReposResponse>>("repository/list");

            if (response?.Data == null)
            {
                System.Console.WriteLine("❌ Failed to fetch repositories");
                return;
            }

            System.Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            System.Console.WriteLine("📦 YOUR REPOSITORIES");
            System.Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            if (response.Data.OwnedRepos.Count == 0)
            {
                System.Console.WriteLine("No repositories yet");
            }
            else
            {
                foreach (var repo in response.Data.OwnedRepos)
                {
                    System.Console.WriteLine($"\n📁 {repo.Name} (Owner)");
                    System.Console.WriteLine($"   └─ Versions: {repo.Versions.Count}");

                    if (repo.Versions.Count > 0)
                    {
                        var latest = repo.Versions.First();
                        System.Console.WriteLine($"     Latest: {latest.Name} - {latest.CreatedAt:yyyy-MM-dd HH:mm}");
                        System.Console.WriteLine($"     Files: {latest.FileCount}");
                        System.Console.WriteLine($"     Message: {latest.Message}");
                    }
                }
            }

            if (response.Data.ClonedRepos.Count > 0)
            {
                System.Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                System.Console.WriteLine("🔗 AVAILABLE REPOSITORIES");
                System.Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

                foreach (var repo in response.Data.ClonedRepos)
                {
                    System.Console.WriteLine($"\n📁 {repo.Name} (By {repo.Owner})");
                    System.Console.WriteLine($"   └─ Versions: {repo.Versions.Count}");

                    if (repo.Versions.Count > 0)
                    {
                        var latest = repo.Versions.First();
                        System.Console.WriteLine($"     Latest: {latest.Name} - {latest.CreatedAt:yyyy-MM-dd HH:mm}");
                        System.Console.WriteLine($"     Files: {latest.FileCount}");
                    }
                }
            }
        }

        //private async Task PushRepository()
        //{
        //    System.Console.Write("\n📁 Local folder path to push: ");
        //    var folderPath = System.Console.ReadLine();

        //    if (!Directory.Exists(folderPath))
        //    {
        //        System.Console.WriteLine("❌ Directory not found.");
        //        return;
        //    }

        //    // --- NEW: Ignore Logic ---
        //    System.Console.Write("❓ Do you want to add specific folders/files to the ignore list? (y/n): ");
        //    if (System.Console.ReadLine()?.ToLower() == "y")
        //    {
        //        System.Console.Write("📝 Enter names to ignore (comma separated, e.g., bin, obj, secret.txt): ");
        //        var input = System.Console.ReadLine();
        //        if (!string.IsNullOrEmpty(input))
        //        {
        //            var items = input.Split(',').Select(x => x.Trim()).ToList();
        //            UpdateIgnoreFile(folderPath, items);
        //        }
        //    }

        //    System.Console.Write("📝 Repository name: ");
        //    var repoName = System.Console.ReadLine();
        //    System.Console.Write("💬 Commit message: ");
        //    var message = System.Console.ReadLine();

        //    System.Console.WriteLine("\n[LOG] Scanning directory and filtering ignored items...");

        //    try
        //    {
        //        var tempZipPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");

        //        // Use the updated FileService to create a filtered ZIP
        //        _fileService.CreateFilteredZip(folderPath, tempZipPath);

        //        var fileInfo = new FileInfo(tempZipPath);
        //        System.Console.WriteLine($"[LOG] Compressed size: {FormatFileSize(fileInfo.Length)}");

        //        var parameters = new Dictionary<string, string>
        //        {
        //            { "repoName", repoName },
        //            { "message", message }
        //        };

        //        System.Console.WriteLine("[LOG] Uploading to Quality Aviation Server...");
        //        await _apiService.UploadFileAsync("repository/push", tempZipPath, parameters);

        //        System.Console.WriteLine("✅ Push successful!");

        //        if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
        //    }
        //    catch (Exception ex)
        //    {
        //        System.Console.WriteLine($"❌ Error: {ex.Message}");
        //    }
        //}

        //private void UpdateIgnoreFile(string rootPath, List<string> newItems)
        //{
        //    var ignorePath = Path.Combine(rootPath, "repoignore.json");
        //    List<dynamic> ignoreList = new();

        //    if (File.Exists(ignorePath))
        //    {
        //        var existingJson = File.ReadAllText(ignorePath);
        //        ignoreList = System.Text.Json.JsonSerializer.Deserialize<List<dynamic>>(existingJson) ?? new();
        //    }

        //    foreach (var item in newItems)
        //    {
        //        ignoreList.Add(new
        //        {
        //            Name = item,
        //            DateIgnored = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        //            Reason = "User manual exclusion"
        //        });
        //    }

        //    File.WriteAllText(ignorePath, System.Text.Json.JsonSerializer.Serialize(ignoreList, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        //    System.Console.WriteLine("✅ repoignore.json updated.");
        //}

        private async Task PushRepository()
        {
            System.Console.Write("\n📁 Local folder path to push: ");
            var folderPath = System.Console.ReadLine();

            if (!Directory.Exists(folderPath))
            {
                System.Console.WriteLine("❌ Folder not found.");
                return;
            }

            // 1. ANCHOR CHECK (.qarepo)
            var statePath = Path.Combine(folderPath, ".qarepo");
            string repoName, targetOwner;

            if (File.Exists(statePath))
            {
                var state = JsonSerializer.Deserialize<LocalRepoState>(File.ReadAllText(statePath));
                repoName = state.RepoName;
                targetOwner = state.Owner;
                System.Console.WriteLine($"[LOG] Target detected: {targetOwner}/{repoName}");
            }
            else
            {
                System.Console.Write("📝 Repository name (New): ");
                repoName = System.Console.ReadLine();
                targetOwner = _authService.CurrentUsername;
            }

            // 2. IGNORE LOGIC (repoignore.json)
            System.Console.Write("❓ Add folders/files to ignore list? (y/n): ");
            if (System.Console.ReadLine()?.ToLower() == "y")
            {
                System.Console.Write("📝 Enter names (comma separated, e.g. bin, obj, .vs): ");
                var items = System.Console.ReadLine()?.Split(',').Select(x => x.Trim()).ToList();
                if (items != null) UpdateIgnoreFile(folderPath, items);
            }

            System.Console.Write("💬 Commit message: ");
            var message = System.Console.ReadLine();

            try
            {
                var tempZipPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");

                // Update local state before zipping
                var newState = new LocalRepoState
                {
                    RepoName = repoName,
                    Owner = targetOwner,
                    LastSync = DateTime.Now
                };
                File.WriteAllText(statePath, JsonSerializer.Serialize(newState));

                // 3. FILTERED COMPRESSION
                _fileService.CreateFilteredZip(folderPath, tempZipPath);

                var parameters = new Dictionary<string, string>
        {
            { "repoName", repoName },
            { "owner", targetOwner }, // Tells API whose repo to update
            { "message", message }
        };

                System.Console.WriteLine($"[LOG] Pushing {FormatFileSize(new FileInfo(tempZipPath).Length)} to {targetOwner}...");
                await _apiService.UploadFileAsync("repository/push", tempZipPath, parameters);

                System.Console.WriteLine("✅ Push successful!");
                if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
            }
            catch (Exception ex) { System.Console.WriteLine($"❌ Error: {ex.Message}"); }
        }

        private void UpdateIgnoreFile(string rootPath, List<string> newItems)
        {
            var ignorePath = Path.Combine(rootPath, "repoignore.json");
            var ignoreList = new List<object>();
            if (File.Exists(ignorePath))
            {
                try { ignoreList = JsonSerializer.Deserialize<List<object>>(File.ReadAllText(ignorePath)); } catch { }
            }
            foreach (var item in newItems)
            {
                ignoreList.Add(new { Name = item, Date = DateTime.Now.ToString("yyyy-MM-dd"), Type = "Manual" });
            }
            File.WriteAllText(ignorePath, JsonSerializer.Serialize(ignoreList, new JsonSerializerOptions { WriteIndented = true }));
        }

        private async Task PullRepository()
        {
            System.Console.Write("\n👤 Repository owner: ");
            var owner = System.Console.ReadLine();

            System.Console.Write("📝 Repository name: ");
            var repoName = System.Console.ReadLine();

            System.Console.Write("📌 Version name (leave empty for latest): ");
            var versionName = System.Console.ReadLine();

            System.Console.Write("📂 Download to folder: ");
            var downloadPath = System.Console.ReadLine();

            System.Console.WriteLine("\n⏳ Downloading...");

            try
            {
                var endpoint = string.IsNullOrEmpty(versionName)
                    ? $"repository/pull?owner={owner}&repoName={repoName}"
                    : $"repository/pull?owner={owner}&repoName={repoName}&versionName={versionName}";

                var fileContent = await _apiService.GetFileAsync(endpoint);

                if (fileContent == null)
                {
                    System.Console.WriteLine("❌ Download failed");
                    return;
                }

                _fileService.EnsureDirectoryExists(downloadPath);
                _fileService.ExtractZipToDirectory(fileContent, downloadPath);

                System.Console.WriteLine($"✅ Downloaded successfully to {downloadPath}");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"❌ Error: {ex.Message}");
            }
        }

        //private async Task CloneRepository()
        //{
        //    System.Console.Write("\n👤 Repository owner: ");
        //    var owner = System.Console.ReadLine();

        //    System.Console.Write("📝 Repository name: ");
        //    var repoName = System.Console.ReadLine();

        //    System.Console.Write("📂 Clone to folder: ");
        //    var clonePath = System.Console.ReadLine();

        //    System.Console.WriteLine("\n⏳ Cloning...");

        //    try
        //    {
        //        var fileContent = await _apiService.GetFileAsync($"repository/clone?owner={owner}&repoName={repoName}");

        //        if (fileContent == null)
        //        {
        //            System.Console.WriteLine("❌ Clone failed");
        //            return;
        //        }

        //        _fileService.EnsureDirectoryExists(clonePath);
        //        _fileService.ExtractZipToDirectory(fileContent, clonePath);

        //        System.Console.WriteLine($"✅ Cloned successfully to {clonePath}");
        //    }
        //    catch (Exception ex)
        //    {
        //        System.Console.WriteLine($"❌ Error: {ex.Message}");
        //    }
        //}
        private async Task CloneRepository()
        {
            System.Console.Write("\n👤 Repository owner: ");
            var owner = System.Console.ReadLine();
            System.Console.Write("📝 Repository name: ");
            var repoName = System.Console.ReadLine();
            System.Console.Write("📂 Clone to folder: ");
            var clonePath = System.Console.ReadLine();

            try
            {
                System.Console.WriteLine("[LOG] Downloading archive...");
                var fileContent = await _apiService.GetFileAsync($"repository/clone?owner={owner}&repoName={repoName}");

                if (fileContent == null) return;

                _fileService.EnsureDirectoryExists(clonePath);
                _fileService.ExtractZipToDirectory(fileContent, clonePath);

                // CREATE ANCHOR FILE
                var state = new LocalRepoState
                {
                    Owner = owner,
                    RepoName = repoName,
                    CurrentVersion = "cloned_latest",
                    LastSync = DateTime.Now
                };
                File.WriteAllText(Path.Combine(clonePath, ".qarepo"), JsonSerializer.Serialize(state));

                System.Console.WriteLine($"✅ Cloned successfully to {clonePath}");
            }
            catch (Exception ex) { System.Console.WriteLine($"❌ Error: {ex.Message}"); }
        }

        private async Task CreateRepository()
        {
            System.Console.Write("\n📝 Repository name: ");
            var repoName = System.Console.ReadLine();

            System.Console.Write("📄 Description (optional): ");
            var description = System.Console.ReadLine();

            System.Console.WriteLine("\n⏳ Creating repository...");

            try
            {
                dynamic request = new System.Dynamic.ExpandoObject();
                request.repoName = repoName;
                request.description = description;

                var response = await _apiService.PostAsync<ApiResponse>("repository/create", request);

                if (response?.Success == true)
                {
                    System.Console.WriteLine("✅ Repository created successfully!");
                }
                else
                {
                    System.Console.WriteLine($"❌ Error: {response?.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"❌ Error: {ex.Message}");
            }
        }

        private async Task ShowRepositoryDetails()
        {
            System.Console.Write("\n👤 Repository owner: ");
            var owner = System.Console.ReadLine();

            System.Console.Write("📝 Repository name: ");
            var repoName = System.Console.ReadLine();

            System.Console.WriteLine("\n📥 Fetching details...");

            try
            {
                var response = await _apiService.GetAsync<ApiResponse<List<RepositoryVersion>>>($"repository/versions?owner={owner}&repoName={repoName}");

                if (response?.Data == null || response.Data.Count == 0)
                {
                    System.Console.WriteLine("❌ No versions found");
                    return;
                }

                System.Console.WriteLine($"\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                System.Console.WriteLine($"📦 {repoName} - All Versions");
                System.Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

                foreach (var version in response.Data)
                {
                    System.Console.WriteLine($"\n📌 {version.VersionName}");
                    System.Console.WriteLine($"   Created: {version.CreatedAt:yyyy-MM-dd HH:mm:ss}");
                    System.Console.WriteLine($"   By: {version.CreatedBy}");
                    System.Console.WriteLine($"   Message: {version.Message}");
                    System.Console.WriteLine($"   Files: {version.Files.Count}");

                    if (version.Files.Count > 0 && version.Files.Count <= 10)
                    {
                        System.Console.WriteLine("   Files:");
                        foreach (var file in version.Files)
                        {
                            System.Console.WriteLine($"     - {file.FileName} ({FormatFileSize(file.FileSize)})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"❌ Error: {ex.Message}");
            }
        }

        private void PrintWelcome()
        {
            System.Console.Clear();
            System.Console.WriteLine(@"
╔══════════════════════════════════════════════════════╗
║                                                      ║
║          REPOSITORY MANAGEMENT SYSTEM v1.0          ║
║                (GIT-like Console)                    ║
║                                                      ║
║  Push • Pull • Clone • Version Control               ║
║                                                      ║
╚══════════════════════════════════════════════════════╝
");
        }

        private void PrintMainMenu()
        {
            System.Console.WriteLine("╭─────────────────────────────────────╮");
            System.Console.WriteLine("│      MAIN MENU                      │");
            System.Console.WriteLine("├─────────────────────────────────────┤");
            System.Console.WriteLine("│ 1️⃣  View Repositories              │");
            System.Console.WriteLine("│ 2️⃣  Push (Upload)                  │");
            System.Console.WriteLine("│ 3️⃣  Pull (Download)                │");
            System.Console.WriteLine("│ 4️⃣  Clone Repository               │");
            System.Console.WriteLine("│ 5️⃣  Create Repository              │");
            System.Console.WriteLine("│ 6️⃣  View Details                   │");
            System.Console.WriteLine("│ 0️⃣  Logout                         │");
            System.Console.WriteLine("╰─────────────────────────────────────╯");
        }

        private string ReadPassword()
        {
            var password = string.Empty;
            while (true)
            {
                var key = System.Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                    break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                        password = password.Substring(0, password.Length - 1);
                }
                else
                {
                    password += key.KeyChar;
                }
            }
            return password;
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}
