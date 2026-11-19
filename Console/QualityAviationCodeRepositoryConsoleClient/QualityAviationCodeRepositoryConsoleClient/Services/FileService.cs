using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    public interface IFileService
    {
        string ComputeFileHash(byte[] content);
        void EnsureDirectoryExists(string path);
        void WriteFile(string filePath, byte[] content);
        byte[] ReadFile(string filePath);
        void CreateFilteredArchive(string sourceDir, string archivePath);
        // The files a push sends: everything except system files, bin, obj, .vs, .git and what .qaignore lists
        List<(string FullPath, string RelativePath)> CollectFiles(string sourceDir);
        void CreateArchive(string sourceDir, IEnumerable<string> relativePaths, string archivePath, IDictionary<string, string> extraEntries = null);
        void ExtractArchiveToDirectory(string archivePath, string targetDirectory);
        void AddIgnoreEntries(string rootPath, IEnumerable<string> newItems);
        // State management for .qarepo
        LocalRepoState GetLocalState(string path);
        void SaveLocalState(string path, LocalRepoState state);
        // Where the copy of a repository lives on this PC (the last folder pushed, pulled or cloned), or null
        string FindLocalFolder(string owner, string repoName);
        // Remembers a tracked folder as the copy of the repository its .qarepo names
        void RememberLocalFolder(string path, LocalRepoState state);
    }

    public class FileService : IFileService
    {
        // STRICT SYSTEM FILE POLICY: These files are for logic/metadata only and are NEVER pushed to the API
        private readonly HashSet<string> _systemFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ".qarepo",      // Local anchor/owner info
            ".qaignore",    // File/Folder exclusion list
            ".qarepinfo",   // Remote repo descriptors
            ".qametadata",  // Version control hashes
            ".metadata.json",  // Server-side version manifest
            "repoignore.json", // Legacy support
            ".repo.json"       // Legacy support
        };

        public string ComputeFileHash(byte[] content)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(content));
        }

        public void EnsureDirectoryExists(string path)
        {
            if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        public void WriteFile(string filePath, byte[] content)
        {
            EnsureDirectoryExists(Path.GetDirectoryName(filePath));
            File.WriteAllBytes(filePath, content);
        }

        public byte[] ReadFile(string filePath) => File.Exists(filePath) ? File.ReadAllBytes(filePath) : null;

        private static HashSet<string> LoadIgnoredNames(string sourceDir)
        {
            // Default ignores
            // .qaupdate: the client's own update workspace, in its installation folder
            var ignoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".vs", ".git", ".qaupdate" };

            // Priority: .qaignore -> repoignore.json
            string ignoreFile = Path.Combine(sourceDir, ".qaignore");
            if (!File.Exists(ignoreFile)) ignoreFile = Path.Combine(sourceDir, "repoignore.json");

            if (File.Exists(ignoreFile))
            {
                try
                {
                    // Check if it's the new line-delimited format or old JSON format
                    var content = File.ReadAllText(ignoreFile).Trim();
                    if (content.StartsWith("[")) // Old JSON Format
                    {
                        using var doc = JsonDocument.Parse(content);
                        foreach (var element in doc.RootElement.EnumerateArray())
                            ignoredNames.Add(element.GetProperty("Name").GetString());
                    }
                    else // New Line-Delimited Format (.qaignore style)
                    {
                        var lines = File.ReadAllLines(ignoreFile);
                        foreach (var line in lines)
                            if (!string.IsNullOrWhiteSpace(line)) ignoredNames.Add(line.Trim());
                    }
                }
                catch { /* Silent fail on malformed ignore files */ }
            }

            return ignoredNames;
        }

        public void CreateFilteredArchive(string sourceDir, string archivePath)
        {
            CreateArchive(sourceDir, CollectFiles(sourceDir).Select(f => f.RelativePath), archivePath);
        }

        public List<(string FullPath, string RelativePath)> CollectFiles(string sourceDir)
        {
            var files = new List<(string, string)>();
            AddDirectory(files, sourceDir, sourceDir, LoadIgnoredNames(sourceDir));
            return files;
        }

        public void CreateArchive(string sourceDir, IEnumerable<string> relativePaths, string archivePath, IDictionary<string, string> extraEntries = null)
        {
            using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            foreach (var relativePath in relativePaths)
            {
                // Use Fastest compression for transfer efficiency as discussed
                zip.CreateEntryFromFile(Path.Combine(sourceDir, relativePath), relativePath.Replace('\\', '/'), CompressionLevel.Fastest);
            }

            foreach (var (name, content) in extraEntries ?? new Dictionary<string, string>())
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open());
                writer.Write(content);
            }
        }

        private void AddDirectory(List<(string, string)> files, string rootDir, string currentDir, HashSet<string> ignoredNames)
        {
            foreach (var file in Directory.EnumerateFiles(currentDir))
            {
                string fileName = Path.GetFileName(file);
                if (_systemFiles.Contains(fileName) || ignoredNames.Contains(fileName)) continue;

                files.Add((file, Path.GetRelativePath(rootDir, file).Replace('\\', '/')));
            }

            foreach (var directory in Directory.EnumerateDirectories(currentDir))
            {
                // Ignored folders are skipped whole, so large trees like bin or .git are never even walked.
                // Links are not followed: they can point outside the repo or loop back into it.
                if (ignoredNames.Contains(Path.GetFileName(directory))) continue;
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;

                AddDirectory(files, rootDir, directory, ignoredNames);
            }
        }

        public void ExtractArchiveToDirectory(string archivePath, string targetDirectory)
        {
            EnsureDirectoryExists(targetDirectory);
            var targetRoot = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                // Directory entries carry no data; system files are never taken from an archive
                if (string.IsNullOrEmpty(entry.Name) || _systemFiles.Contains(entry.Name)) continue;

                var destinationPath = Path.GetFullPath(Path.Combine(targetRoot, entry.FullName));

                // Security check to prevent zip-slip (extracting outside target)
                if (!destinationPath.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                    continue;

                EnsureDirectoryExists(Path.GetDirectoryName(destinationPath));
                entry.ExtractToFile(destinationPath, true);
            }
        }

        public void AddIgnoreEntries(string rootPath, IEnumerable<string> newItems)
        {
            var ignorePath = Path.Combine(rootPath, ".qaignore");
            var existing = File.Exists(ignorePath) ? File.ReadAllLines(ignorePath).ToList() : new List<string>();
            foreach (var item in newItems)
                if (!string.IsNullOrWhiteSpace(item) && !existing.Contains(item.Trim())) existing.Add(item.Trim());
            File.WriteAllLines(ignorePath, existing);
        }

        public LocalRepoState GetLocalState(string path)
        {
            string stateFile = Path.Combine(path, ".qarepo");
            if (!File.Exists(stateFile)) return null;

            try
            {
                return JsonSerializer.Deserialize<LocalRepoState>(File.ReadAllText(stateFile));
            }
            catch { return null; }
        }

        public void SaveLocalState(string path, LocalRepoState state)
        {
            EnsureDirectoryExists(path);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(Path.Combine(path, ".qarepo"), JsonSerializer.Serialize(state, options));
            RememberLocalFolder(path, state);
        }

        // ------------------------------------------------------------------ local copies on this PC

        // Per Windows user, next to the saved session: { "owner/repo": "D:\Work\Folder" }
        private static readonly string FoldersFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QARepository", "folders.json");

        public string FindLocalFolder(string owner, string repoName)
        {
            var folders = LoadFolders();
            if (!folders.TryGetValue(FolderKey(owner, repoName), out var path)) return null;

            // Only while the folder is still there and still a copy of that repository
            var state = GetLocalState(path);
            return state != null && Same(state.Owner, owner) && Same(state.RepoName, repoName) ? path : null;
        }

        public void RememberLocalFolder(string path, LocalRepoState state)
        {
            if (state?.Owner == null || state.RepoName == null) return;
            try
            {
                var folders = LoadFolders();
                folders[FolderKey(state.Owner, state.RepoName)] = Path.GetFullPath(path);

                Directory.CreateDirectory(Path.GetDirectoryName(FoldersFile));
                var temp = FoldersFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(folders, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, FoldersFile, true);
            }
            catch (Exception)
            {
                // Only a convenience: the push or pull itself has already succeeded
            }
        }

        private static Dictionary<string, string> LoadFolders()
        {
            try
            {
                if (File.Exists(FoldersFile))
                    return new Dictionary<string, string>(
                        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FoldersFile)) ?? new(),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception) { /* unreadable: start over */ }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static string FolderKey(string owner, string repoName) => $"{owner}/{repoName}";

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
