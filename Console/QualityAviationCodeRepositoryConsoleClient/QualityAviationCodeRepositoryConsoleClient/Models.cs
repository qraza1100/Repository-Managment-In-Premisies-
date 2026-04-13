namespace QualityAviationCodeRepositoryConsoleClient.Console.Models
{
    public class ApiResponse<T>
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public T Data { get; set; }
        public int StatusCode { get; set; }
    }

    public class ApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int StatusCode { get; set; }
    }

    public class AuthRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class AuthResponse
    {
        public bool Authenticated { get; set; }
        public string GatePass { get; set; }
        public string Message { get; set; }
        public User User { get; set; }
    }

    public class User
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string Email { get; set; }
        public string GatePass { get; set; }
    }

    public class RepoInfo
    {
        public string Name { get; set; }
        public string Owner { get; set; }
        public List<VersionInfo> Versions { get; set; } = new();
    }

    public class VersionInfo
    {
        public string Name { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Message { get; set; }
        public int FileCount { get; set; }
    }

    public class ListReposResponse
    {
        public List<RepoInfo> OwnedRepos { get; set; } = new();
        public List<RepoInfo> ClonedRepos { get; set; } = new();
    }

    public class FileUpload
    {
        public string FileName { get; set; }
        public byte[] FileContent { get; set; }
        public string FileHash { get; set; }
    }

    public class RepositoryVersion
    {
        public string VersionName { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; }
        public string Message { get; set; }
        public List<FileMetadata> Files { get; set; } = new();
    }

    public class FileMetadata
    {
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string FileHash { get; set; }
        public DateTime LastModified { get; set; }
    }

    public class RepositoryState
    {
        public string OwnerUsername { get; set; }
        public string RepositoryName { get; set; }
        public string LocalPath { get; set; }
        public DateTime CreatedAt { get; set; }
        public string LatestVersion { get; set; }
    }

    public class LocalRepoState
    {
        public string RepoName { get; set; }
        public string Owner { get; set; }
        public string CurrentVersion { get; set; }
        public DateTime LastSync { get; set; }
    }
}
