using QualityAviationCodeRepositoryManagmentApi.API.Models;
using System.IO;
using System.Text.Json;

namespace QualityAviationCodeRepositoryManagmentApi.API.Services
{
    public interface IRepositoryService
    {
        void CreateRepository(string owner, string repoName, string description);
        bool RepositoryExists(string owner, string repoName);
        void PushFiles(string owner, string repoName, string message, List<FileUpload> files);
        byte[] PullFiles(string owner, string repoName, string versionName);
        byte[] CloneRepository(string owner, string repoName);
        List<RepoInfo> GetUserRepositories(string username);
        List<RepoInfo> GetAllAvailableRepositories(string username);
        RepositoryVersion GetLatestVersion(string owner, string repoName);
        List<RepositoryVersion> GetAllVersions(string owner, string repoName);
        bool ValidateOwnership(string owner, string repoName);
    }

    public class RepositoryService : IRepositoryService
    {
        private readonly IFileService _fileService;
        private readonly IVersionService _versionService;
        private readonly IConfiguration _configuration;
        private readonly string _basePath;

        public RepositoryService(IFileService fileService, IVersionService versionService, IConfiguration configuration)
        {
            _fileService = fileService;
            _versionService = versionService;
            _configuration = configuration;
            _basePath = configuration["RepositorySettings:BasePath"];
        }

        public void CreateRepository(string owner, string repoName, string description)
        {
            var repoPath = Path.Combine(_basePath, owner, repoName);
            _fileService.EnsureDirectoryExists(repoPath);

            var repoMetadata = new
            {
                Name = repoName,
                Owner = owner,
                Description = description,
                CreatedAt = DateTime.Now
            };

            var metadataPath = Path.Combine(repoPath, ".repo.json");
            var json = JsonSerializer.Serialize(repoMetadata, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metadataPath, json);
        }

        public bool RepositoryExists(string owner, string repoName)
        {
            var repoPath = Path.Combine(_basePath, owner, repoName);
            var metadataFile = Path.Combine(repoPath, ".repo.json");
            return File.Exists(metadataFile);
        }

        public void PushFiles(string owner, string repoName, string message, List<FileUpload> files)
        {
            if (!RepositoryExists(owner, repoName))
            {
                CreateRepository(owner, repoName, "");
            }

            var repoPath = Path.Combine(_basePath, owner, repoName);
            var needsNewVersion = _versionService.NeedsNewVersion(repoPath, files);

            string versionName;
            if (needsNewVersion)
            {
                versionName = _versionService.GenerateVersionName();
            }
            else
            {
                versionName = _versionService.GetLatestVersion(repoPath);
            }

            var versionPath = Path.Combine(repoPath, versionName);
            _fileService.EnsureDirectoryExists(versionPath);

            var fileMetadatas = new List<FileMetadata>();

            foreach (var file in files)
            {
                var filePath = Path.Combine(versionPath, file.FileName);
                _fileService.WriteFile(filePath, file.FileContent);

                fileMetadatas.Add(new FileMetadata
                {
                    FileName = file.FileName,
                    FileSize = _fileService.GetFileSize(file.FileContent),
                    FileHash = file.FileHash,
                    LastModified = DateTime.Now
                });
            }

            _versionService.CreateVersionMetadata(versionPath, message, owner, fileMetadatas);
        }

        public byte[] PullFiles(string owner, string repoName, string versionName)
        {
            var repoPath = Path.Combine(_basePath, owner, repoName);
            var versionPath = Path.Combine(repoPath, versionName ?? _versionService.GetLatestVersion(repoPath));

            if (!Directory.Exists(versionPath))
                throw new Exception("Version not found");

            return CreateZipFromDirectory(versionPath);
        }

        public byte[] CloneRepository(string owner, string repoName)
        {
            var repoPath = Path.Combine(_basePath, owner, repoName);

            if (!Directory.Exists(repoPath))
                throw new Exception("Repository not found");

            return CreateZipFromDirectory(repoPath);
        }

        public List<RepoInfo> GetUserRepositories(string username)
        {
            var repos = new List<RepoInfo>();
            var userPath = Path.Combine(_basePath, username);

            if (!Directory.Exists(userPath))
                return repos;

            var repoDirs = Directory.GetDirectories(userPath);

            foreach (var repoDir in repoDirs)
            {
                var repoName = Path.GetFileName(repoDir);
                var versions = _versionService.GetAllVersions(repoDir);

                repos.Add(new RepoInfo
                {
                    Name = repoName,
                    Owner = username,
                    Versions = versions.Select(v => new VersionInfo
                    {
                        Name = v.VersionName,
                        CreatedAt = v.CreatedAt,
                        Message = v.Message,
                        FileCount = v.Files.Count
                    }).ToList()
                });
            }

            return repos;
        }

        public List<RepoInfo> GetAllAvailableRepositories(string username)
        {
            var repos = new List<RepoInfo>();

            if (!Directory.Exists(_basePath))
                return repos;

            var userDirs = Directory.GetDirectories(_basePath);

            foreach (var userDir in userDirs)
            {
                var owner = Path.GetFileName(userDir);
                var repoDirs = Directory.GetDirectories(userDir);

                foreach (var repoDir in repoDirs)
                {
                    var repoName = Path.GetFileName(repoDir);
                    var versions = _versionService.GetAllVersions(repoDir);

                    repos.Add(new RepoInfo
                    {
                        Name = repoName,
                        Owner = owner,
                        Versions = versions.Select(v => new VersionInfo
                        {
                            Name = v.VersionName,
                            CreatedAt = v.CreatedAt,
                            Message = v.Message,
                            FileCount = v.Files.Count
                        }).ToList()
                    });
                }
            }

            return repos;
        }

        public RepositoryVersion GetLatestVersion(string owner, string repoName)
        {
            var repoPath = Path.Combine(_basePath, owner, repoName);
            var latestVersionName = _versionService.GetLatestVersion(repoPath);

            if (latestVersionName == null)
                return null;

            var latestVersionPath = Path.Combine(repoPath, latestVersionName);
            return _versionService.LoadVersionMetadata(latestVersionPath);
        }

        public List<RepositoryVersion> GetAllVersions(string owner, string repoName)
        {
            var repoPath = Path.Combine(_basePath, owner, repoName);
            return _versionService.GetAllVersions(repoPath);
        }

        public bool ValidateOwnership(string owner, string repoName)
        {
            var repoPath = Path.Combine(_basePath, owner, repoName);
            var metadataFile = Path.Combine(repoPath, ".repo.json");

            if (!File.Exists(metadataFile))
                return false;

            try
            {
                var json = File.ReadAllText(metadataFile);
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("Owner", out var ownerElement))
                    {
                        return ownerElement.GetString() == owner;
                    }
                }
            }
            catch { }

            return false;
        }

        private byte[] CreateZipFromDirectory(string directoryPath)
        {
            using (var memoryStream = new MemoryStream())
            {
                System.IO.Compression.ZipFile.CreateFromDirectory(directoryPath, memoryStream);
                return memoryStream.ToArray();
            }
        }
    }
}
