using QualityAviationCodeRepositoryManagmentApi.API.Models;
using System.IO;
using System.Text.Json;

namespace QualityAviationCodeRepositoryManagmentApi.API.Services
{
    public interface IVersionService
    {
        string GenerateVersionName();
        string GetLatestVersion(string repoPath);
        bool NeedsNewVersion(string repoPath, List<FileUpload> newFiles);
        void CreateVersionMetadata(string versionPath, string message, string creator, List<FileMetadata> files);
        RepositoryVersion LoadVersionMetadata(string versionPath);
        List<RepositoryVersion> GetAllVersions(string repoPath);
        Dictionary<string, string> GetVersionComparison(string versionPath1, string versionPath2);
    }

    public class VersionService : IVersionService
    {
        private readonly IFileService _fileService;

        public VersionService(IFileService fileService)
        {
            _fileService = fileService;
        }

        public string GenerateVersionName()
        {
            return $"v{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        public string GetLatestVersion(string repoPath)
        {
            if (!Directory.Exists(repoPath))
                return null;

            var versionDirs = Directory.GetDirectories(repoPath)
                .Select(Path.GetFileName)
                .Where(d => d.StartsWith("v"))
                .OrderByDescending(d => d)
                .FirstOrDefault();

            return versionDirs;
        }

        public bool NeedsNewVersion(string repoPath, List<FileUpload> newFiles)
        {
            var latestVersion = GetLatestVersion(repoPath);
            
            if (latestVersion == null)
                return true;

            var latestVersionPath = Path.Combine(repoPath, latestVersion);
            var metadataFile = Path.Combine(latestVersionPath, ".metadata.json");

            if (!File.Exists(metadataFile))
                return true;

            try
            {
                var json = File.ReadAllText(metadataFile);
                var metadata = JsonSerializer.Deserialize<RepositoryVersion>(json);

                var newHashes = newFiles.ToDictionary(f => f.FileName, f => f.FileHash);
                var oldHashes = metadata.Files.ToDictionary(f => f.FileName, f => f.FileHash);

                if (newHashes.Count != oldHashes.Count)
                    return true;

                foreach (var newFile in newHashes)
                {
                    if (!oldHashes.ContainsKey(newFile.Key) || oldHashes[newFile.Key] != newFile.Value)
                        return true;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        public void CreateVersionMetadata(string versionPath, string message, string creator, List<FileMetadata> files)
        {
            _fileService.EnsureDirectoryExists(versionPath);

            var version = new RepositoryVersion
            {
                VersionName = Path.GetFileName(versionPath),
                CreatedAt = DateTime.Now,
                CreatedBy = creator,
                Message = message,
                Files = files
            };

            var metadataPath = Path.Combine(versionPath, ".metadata.json");
            var json = JsonSerializer.Serialize(version, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metadataPath, json);
        }

        public RepositoryVersion LoadVersionMetadata(string versionPath)
        {
            var metadataFile = Path.Combine(versionPath, ".metadata.json");
            
            if (!File.Exists(metadataFile))
                return null;

            try
            {
                var json = File.ReadAllText(metadataFile);
                return JsonSerializer.Deserialize<RepositoryVersion>(json);
            }
            catch
            {
                return null;
            }
        }

        public List<RepositoryVersion> GetAllVersions(string repoPath)
        {
            var versions = new List<RepositoryVersion>();

            if (!Directory.Exists(repoPath))
                return versions;

            var versionDirs = Directory.GetDirectories(repoPath)
                .Select(Path.GetFileName)
                .Where(d => d.StartsWith("v"))
                .OrderByDescending(d => d)
                .ToList();

            foreach (var versionDir in versionDirs)
            {
                var versionPath = Path.Combine(repoPath, versionDir);
                var metadata = LoadVersionMetadata(versionPath);
                if (metadata != null)
                {
                    versions.Add(metadata);
                }
            }

            return versions;
        }

        public Dictionary<string, string> GetVersionComparison(string versionPath1, string versionPath2)
        {
            var comparison = new Dictionary<string, string>();
            
            if (!Directory.Exists(versionPath1) || !Directory.Exists(versionPath2))
                return comparison;

            var files1 = Directory.GetFiles(versionPath1, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".metadata.json"))
                .ToHashSet();

            var files2 = Directory.GetFiles(versionPath2, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".metadata.json"))
                .ToHashSet();

            foreach (var file in files1.Union(files2))
            {
                var exists1 = files1.Contains(file);
                var exists2 = files2.Contains(file);
                var relativePath = Path.GetRelativePath(versionPath1, file);

                if (exists1 && !exists2)
                    comparison[relativePath] = "deleted";
                else if (!exists1 && exists2)
                    comparison[relativePath] = "added";
                else if (exists1 && exists2)
                {
                    comparison[relativePath] = "modified";
                }
            }

            return comparison;
        }
    }
}
