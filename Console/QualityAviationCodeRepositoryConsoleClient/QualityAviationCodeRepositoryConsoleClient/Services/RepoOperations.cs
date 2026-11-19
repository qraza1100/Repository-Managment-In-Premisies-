using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using QualityAviationRepositoryShared;
using System.Text.Json;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    // Every repository task, with no user interface in it. The terminal menu and the Windows
    // application both call these, so the two always behave the same.
    public interface IRepoOperations
    {
        Task<ListReposResponse> ListAsync();
        Task<List<VersionInfo>> GetVersionsAsync(string owner, string repoName);
        Task CreateAsync(string repoName, string description);
        Task<PushResult> PushAsync(string path, string repoName, string message, IProgress<TransferProgress> progress = null);
        Task<SyncResult> PullAsync(string path, string owner, string repoName, string versionName = null, IProgress<TransferProgress> progress = null);
        Task<SyncResult> CloneAsync(string owner, string repoName, string destination, IProgress<TransferProgress> progress = null);
    }

    // Pushes and pulls move only the files that changed when the deploy agent runs on the server: both sides
    // describe their files by SHA-256 hash, and only what differs travels. Without the agent, or when the
    // server's copy moved on in the meantime, the whole folder is sent or fetched as before.
    public class RepoOperations : IRepoOperations
    {
        private readonly IApiService _apiService;
        private readonly IAuthService _authService;
        private readonly IFileService _fileService;

        public RepoOperations(IApiService apiService, IAuthService authService, IFileService fileService)
        {
            _apiService = apiService;
            _authService = authService;
            _fileService = fileService;
        }

        public async Task<ListReposResponse> ListAsync()
        {
            var response = await _apiService.GetAsync<ApiResponse<ListReposResponse>>("repository/list");
            return response?.Data ?? new ListReposResponse();
        }

        public async Task<List<VersionInfo>> GetVersionsAsync(string owner, string repoName)
        {
            var response = await _apiService.GetAsync<ApiResponse<List<VersionInfo>>>(
                $"repository/versions?owner={Uri.EscapeDataString(owner)}&repoName={Uri.EscapeDataString(repoName)}");
            return response?.Data ?? new List<VersionInfo>();
        }

        public async Task CreateAsync(string repoName, string description)
        {
            await _apiService.PostAsync<ApiResponse>("repository/create", new { repoName, description });
        }

        public async Task<PushResult> PushAsync(string path, string repoName, string message, IProgress<TransferProgress> progress = null)
        {
            if (!Directory.Exists(path)) throw new ApiException("Directory not found.");

            // A tracked folder always goes back to the repository it came from (Direct-to-Source)
            var state = _fileService.GetLocalState(path);
            var targetOwner = state?.Owner ?? _authService.CurrentUsername;
            repoName = state?.RepoName ?? repoName;

            if (string.IsNullOrWhiteSpace(repoName)) throw new ApiException("Repository name is required.");

            var files = await Task.Run(() => _fileService.CollectFiles(path));
            var result = await TryPushChangesAsync(path, targetOwner, repoName, message, files, progress)
                         ?? await PushWholeAsync(path, targetOwner, repoName, message, files, progress);

            // Only a confirmed push updates the local anchor
            _fileService.SaveLocalState(path, new LocalRepoState
            {
                RepoName = repoName,
                Owner = targetOwner,
                CurrentVersion = result.Version,
                LastSync = DateTime.Now,
                Description = state?.Description
            });

            return result;
        }

        public Task<SyncResult> PullAsync(string path, string owner, string repoName, string versionName = null, IProgress<TransferProgress> progress = null)
        {
            var state = _fileService.GetLocalState(path);
            if (state != null)
            {
                owner = state.Owner;
                repoName = state.RepoName;
            }

            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repoName))
                throw new ApiException("Owner and repository name are required.");

            var endpoint = $"repository/pull?owner={Uri.EscapeDataString(owner)}&repoName={Uri.EscapeDataString(repoName)}";
            if (!string.IsNullOrWhiteSpace(versionName)) endpoint += $"&versionName={Uri.EscapeDataString(versionName.Trim())}";

            return DownloadIntoAsync(endpoint, path, owner, repoName, versionName?.Trim(), state?.Description, progress);
        }

        public Task<SyncResult> CloneAsync(string owner, string repoName, string destination, IProgress<TransferProgress> progress = null)
        {
            if (string.IsNullOrWhiteSpace(destination)) throw new ApiException("Destination folder is required.");

            var endpoint = $"repository/clone?owner={Uri.EscapeDataString(owner ?? "")}&repoName={Uri.EscapeDataString(repoName ?? "")}";
            return DownloadIntoAsync(endpoint, destination, owner, repoName, null, null, progress);
        }

        // ------------------------------------------------------------------ push

        // Null when the changed files cannot be sent on their own; the caller then sends the whole folder
        private async Task<PushResult> TryPushChangesAsync(string path, string owner, string repoName, string message,
            List<(string FullPath, string RelativePath)> files, IProgress<TransferProgress> progress)
        {
            if (!await _apiService.AgentAvailableAsync()) return null;

            FileManifest remote;
            try
            {
                remote = await _apiService.GetAsync<FileManifest>(Query("sync/manifest", owner, repoName), agent: true);
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 0)
            {
                // A new repository, or the agent went away: the whole folder goes to the repository API
                return null;
            }
            if (remote?.Version == null) return null;

            var local = await Task.Run(() => Manifests.FromFiles(files));
            var changes = Manifests.Diff(remote, local);
            if (changes.Count == 0)
                return new PushResult
                {
                    Version = remote.Version,
                    Changed = false,
                    FileCount = local.Files.Count,
                    Note = "Every file already matches the server, so nothing was sent"
                };

            var send = changes.Where(c => c.Kind != FileChange.Deleted).Select(c => c.Path).ToList();
            var deleted = changes.Where(c => c.Kind == FileChange.Deleted).Select(c => c.Path).ToList();
            var description = new DeltaDescription { BaseVersion = remote.Version, Deleted = deleted, Fingerprint = Manifests.Fingerprint(local) };

            var tempArchive = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
            try
            {
                await Task.Run(() => _fileService.CreateArchive(path, send, tempArchive,
                    new Dictionary<string, string> { [DeltaDescription.EntryName] = JsonSerializer.Serialize(description) }));

                var response = await _apiService.UploadFileAsync<ApiResponse<PushResult>>(
                    Query("sync/push", owner, repoName) + $"&message={Uri.EscapeDataString(message ?? "")}", tempArchive, progress, agent: true);

                var result = response?.Data ?? throw new ApiException("The server did not confirm the push.");
                result.Note = $"Sent {send.Count} changed file(s) of {local.Files.Count}" +
                              (deleted.Count > 0 ? $", {deleted.Count} removed" : "") +
                              $" ({FormatSize(new FileInfo(tempArchive).Length)})";
                return result;
            }
            catch (ApiException ex) when (ex.StatusCode == 409)
            {
                // Someone pushed in between and the server's copy is no longer what was compared: send everything
                return null;
            }
            finally
            {
                File.Delete(tempArchive);
            }
        }

        private async Task<PushResult> PushWholeAsync(string path, string owner, string repoName, string message,
            List<(string FullPath, string RelativePath)> files, IProgress<TransferProgress> progress)
        {
            var tempArchive = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
            try
            {
                await Task.Run(() => _fileService.CreateArchive(path, files.Select(f => f.RelativePath), tempArchive));

                var response = await _apiService.UploadFileAsync<ApiResponse<PushResult>>(
                    $"repository/push?owner={Uri.EscapeDataString(owner)}&repoName={Uri.EscapeDataString(repoName)}&message={Uri.EscapeDataString(message ?? "")}",
                    tempArchive, progress);

                var result = response?.Data ?? throw new ApiException("The server did not confirm the push.");
                result.Note = $"Sent the whole folder: {files.Count} file(s), {FormatSize(new FileInfo(tempArchive).Length)}";
                return result;
            }
            finally
            {
                File.Delete(tempArchive);
            }
        }

        // ------------------------------------------------------------------ pull and clone

        private async Task<SyncResult> DownloadIntoAsync(string endpoint, string path, string owner, string repoName, string versionName, string description,
            IProgress<TransferProgress> progress)
        {
            var result = await TryPullChangesAsync(path, owner, repoName, versionName, progress)
                         ?? await PullWholeAsync(endpoint, path, progress);

            _fileService.SaveLocalState(path, new LocalRepoState
            {
                RepoName = repoName,
                Owner = owner,
                CurrentVersion = result.Version,
                LastSync = DateTime.Now,
                Description = description
            });

            return result;
        }

        // Fetches only the files whose content differs from what is in the folder already
        private async Task<SyncResult> TryPullChangesAsync(string path, string owner, string repoName, string versionName, IProgress<TransferProgress> progress)
        {
            if (!await _apiService.AgentAvailableAsync()) return null;

            FileManifest remote;
            try
            {
                var endpoint = Query("sync/manifest", owner, repoName) + (string.IsNullOrEmpty(versionName) ? "" : $"&version={Uri.EscapeDataString(versionName)}");
                remote = await _apiService.GetAsync<FileManifest>(endpoint, agent: true);
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 0)
            {
                // The repository API gives the proper answer for a repository that does not exist
                return null;
            }
            if (remote?.Version == null) return null;

            var needed = await Task.Run(() => remote.Files
                .Where(file =>
                {
                    var local = Path.Combine(path, file.Path);
                    return !File.Exists(local) || new FileInfo(local).Length != file.Size || Manifests.HashFile(local) != file.Hash;
                })
                .Select(file => file.Path)
                .ToList());

            if (needed.Count == 0)
                return new SyncResult(remote.Version, "Every file already matches the server, so nothing was downloaded");

            var tempArchive = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
            try
            {
                var request = new SyncFilesRequest { Owner = owner, Repo = repoName, Version = remote.Version, Paths = needed };
                await _apiService.DownloadFileAsync("sync/files", tempArchive, progress, postBody: request, agent: true);

                // The working folder is only touched after the whole archive has arrived intact
                await Task.Run(() => _fileService.ExtractArchiveToDirectory(tempArchive, path));
                return new SyncResult(remote.Version, $"Downloaded {needed.Count} changed file(s) of {remote.Files.Count} ({FormatSize(new FileInfo(tempArchive).Length)})");
            }
            finally
            {
                File.Delete(tempArchive);
            }
        }

        private async Task<SyncResult> PullWholeAsync(string endpoint, string path, IProgress<TransferProgress> progress)
        {
            var tempArchive = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
            try
            {
                // The working folder is only touched after the whole archive has arrived intact
                var version = await _apiService.DownloadFileAsync(endpoint, tempArchive, progress);
                await Task.Run(() => _fileService.ExtractArchiveToDirectory(tempArchive, path));
                return new SyncResult(version, $"Downloaded the whole version ({FormatSize(new FileInfo(tempArchive).Length)})");
            }
            finally
            {
                File.Delete(tempArchive);
            }
        }

        private static string Query(string endpoint, string owner, string repoName)
        {
            return $"{endpoint}?owner={Uri.EscapeDataString(owner ?? "")}&repo={Uri.EscapeDataString(repoName ?? "")}";
        }

        public static string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double length = bytes;
            var order = 0;
            while (length >= 1024 && order < sizes.Length - 1) { order++; length /= 1024; }
            return $"{length:0.##} {sizes[order]}";
        }
    }
}
