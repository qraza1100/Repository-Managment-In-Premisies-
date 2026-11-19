namespace QualityAviationCodeRepositoryConsoleClient.Console.Models
{
    // Standard Generic Response
    public class ApiResponse<T>
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public T Data { get; set; }
        public int StatusCode { get; set; }
    }

    // Standard Simple Response
    public class ApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int StatusCode { get; set; }
    }

    // Security & Auth
    public class AuthRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; }
        public string NewPassword { get; set; }
    }

    public class AuthResponse
    {
        public bool Authenticated { get; set; }
        public string GatePass { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Message { get; set; }
        public User User { get; set; }
    }

    public class User
    {
        public string Username { get; set; }
        public string Email { get; set; }
    }

    // Repository Listing Info
    public class RepoInfo
    {
        public string Name { get; set; }
        public string Owner { get; set; }
        public string Description { get; set; }
        public int VersionCount { get; set; }
        public VersionInfo LatestVersion { get; set; }
    }

    public class VersionInfo
    {
        public string Name { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; }
        public string Message { get; set; }
        public int FileCount { get; set; }
    }

    public class ListReposResponse
    {
        public List<RepoInfo> OwnedRepos { get; set; } = new();
        public List<RepoInfo> ClonedRepos { get; set; } = new();
    }

    // File Transfer Models
    public class PushResult
    {
        public string Version { get; set; }
        public bool Changed { get; set; }
        public int FileCount { get; set; }

        // How it travelled, for the user: only the changed files, or the whole folder
        public string Note { get; set; }
    }

    // What a pull or clone ended at, and how much had to be fetched to get there
    public record SyncResult(string Version, string Note);

    public readonly record struct TransferProgress(long Bytes, long Total)
    {
        public int Percent => Total <= 0 ? 0 : (int)Math.Min(100, Bytes * 100 / Total);
    }

    // CLIENT SYSTEM TRACKING (The .qarepo Anchor)
    public class LocalRepoState
    {
        public string RepoName { get; set; }
        public string Owner { get; set; } // The target repository owner (Direct-to-Source)
        public string CurrentVersion { get; set; }
        public DateTime LastSync { get; set; }
        public string Description { get; set; }
    }

    // Any failure worth showing to the user as-is. StatusCode is 0 when the server was never reached.
    public class ApiException : Exception
    {
        public int StatusCode { get; }
        public bool IsUnauthorized => StatusCode == 401;

        public ApiException(string message, int statusCode = 0, Exception inner = null) : base(message, inner)
        {
            StatusCode = statusCode;
        }
    }
}
