using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    public interface IFileService
    {
        string ComputeFileHash(byte[] content);
        string ComputeFileHash(string filePath);
        void EnsureDirectoryExists(string path);
        void WriteFile(string filePath, byte[] content);
        byte[] ReadFile(string filePath);
        bool FileExists(string filePath);
        void DeleteFile(string filePath);
        void DeleteDirectory(string path);
        List<string> GetFilesInDirectory(string path);
        byte[] CreateZipFromDirectory(string directoryPath);
        void ExtractZipToDirectory(byte[] zipContent, string targetDirectory);
        long GetDirectorySize(string path);
        List<string> GetDirectoryStructure(string path);
        void CreateFilteredZip(string sourceDir, string zipPath);
    }

    public class FileService : IFileService
    {
        public string ComputeFileHash(byte[] content)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashedBytes = sha256.ComputeHash(content);
                return Convert.ToBase64String(hashedBytes);
            }
        }

        public string ComputeFileHash(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hashedBytes = sha256.ComputeHash(stream);
                return Convert.ToBase64String(hashedBytes);
            }
        }

        public void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        public void WriteFile(string filePath, byte[] content)
        {
            var directory = Path.GetDirectoryName(filePath);
            EnsureDirectoryExists(directory);
            File.WriteAllBytes(filePath, content);
        }

        public byte[] ReadFile(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            return File.ReadAllBytes(filePath);
        }

        public bool FileExists(string filePath)
        {
            return File.Exists(filePath);
        }

        public void DeleteFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        public List<string> GetFilesInDirectory(string path)
        {
            var files = new List<string>();

            if (!Directory.Exists(path))
                return files;

            try
            {
                var fileEntries = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".metadata.json") && !f.EndsWith(".repo.json"))
                    .ToList();

                files.AddRange(fileEntries);
            }
            catch { }

            return files;
        }

        public byte[] CreateZipFromDirectory(string directoryPath)
        {
            var tempZip = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");

            try
            {
                System.IO.Compression.ZipFile.CreateFromDirectory(directoryPath, tempZip);

                return File.ReadAllBytes(tempZip);
            }
            finally
            {
                if (File.Exists(tempZip))
                    File.Delete(tempZip);
            }
        }

        public void ExtractZipToDirectory(byte[] zipContent, string targetDirectory)
        {
            EnsureDirectoryExists(targetDirectory);

            using (var memoryStream = new MemoryStream(zipContent))
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(memoryStream, targetDirectory, true);
            }
        }

        public long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path))
                return 0;

            long size = 0;
            try
            {
                var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    size += new FileInfo(file).Length;
                }
            }
            catch { }

            return size;
        }

        public List<string> GetDirectoryStructure(string path)
        {
            var structure = new List<string>();

            if (!Directory.Exists(path))
                return structure;

            try
            {
                var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".metadata.json") && !f.EndsWith(".repo.json"))
                    .OrderBy(f => f);

                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(path, file);
                    var fileInfo = new FileInfo(file);
                    structure.Add($"{relativePath} ({FormatFileSize(fileInfo.Length)})");
                }
            }
            catch { }

            return structure;
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

        //public void CreateFilteredZip(string sourceDir, string zipPath)
        //{
        //    var ignorePath = Path.Combine(sourceDir, "repoignore.json");
        //    var ignoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".vs" }; // Hardcoded defaults

        //    if (File.Exists(ignorePath))
        //    {
        //        try
        //        {
        //            var json = File.ReadAllText(ignorePath);
        //            using var doc = System.Text.Json.JsonDocument.Parse(json);
        //            foreach (var element in doc.RootElement.EnumerateArray())
        //            {
        //                ignoredNames.Add(element.GetProperty("Name").GetString());
        //            }
        //        }
        //        catch { System.Console.WriteLine("[WARN] Could not parse repoignore.json"); }
        //    }

        //    using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        //    {
        //        var allFiles = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
        //        foreach (var file in allFiles)
        //        {
        //            var relativePath = Path.GetRelativePath(sourceDir, file);

        //            // Check if any part of the path is in the ignore list
        //            bool shouldIgnore = relativePath.Split(Path.DirectorySeparatorChar)
        //                                            .Any(part => ignoredNames.Contains(part));

        //            if (!shouldIgnore)
        //            {
        //                zip.CreateEntryFromFile(file, relativePath, System.IO.Compression.CompressionLevel.Optimal);
        //            }
        //        }
        //    }
        //}

        public void CreateFilteredZip(string sourceDir, string zipPath)
        {
            var ignoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", "bin", "obj" };
            var ignorePath = Path.Combine(sourceDir, "repoignore.json");

            if (File.Exists(ignorePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ignorePath));
                foreach (var element in doc.RootElement.EnumerateArray())
                    ignoredNames.Add(element.GetProperty("Name").GetString());
            }

            using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                var allFiles = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
                foreach (var file in allFiles)
                {
                    var relativePath = Path.GetRelativePath(sourceDir, file);
                    bool shouldIgnore = relativePath.Split(Path.DirectorySeparatorChar).Any(part => ignoredNames.Contains(part));

                    if (!shouldIgnore)
                        zip.CreateEntryFromFile(file, relativePath, System.IO.Compression.CompressionLevel.Optimal);
                }
            }
        }
    }
}
