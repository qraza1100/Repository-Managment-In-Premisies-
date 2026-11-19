using Microsoft.Extensions.Configuration;
using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using QualityAviationRepositoryShared;
using System.Diagnostics;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    // Installed: files were replaced and the client must restart to run them
    public record UpdateResult(bool Installed, string Message);

    public interface IClientUpdater
    {
        // The version this installation was last updated to (null when it never was)
        string InstalledVersion { get; }
        bool RestartPending { get; }

        // Checks the update repository and installs a newer client into this installation folder.
        // Null when no update repository is configured. Never throws: a failure is the message,
        // and the current client simply keeps running.
        Task<UpdateResult> UpdateAsync(IProgress<TransferProgress> progress = null);

        // Removes what an earlier update left behind (the files that were running at the time)
        void CleanUpInBackground();
    }

    // The client updates itself from a repository on the server that holds the published client (dist\client,
    // pushed as is). After every sign-in it compares the newest version pushed by a trusted publisher with the
    // version recorded in its own folder (.qarepo). Only the files that differ are fetched (by hash, through the
    // deploy agent; without it the whole version), then each one is swapped in: a running program file cannot be
    // overwritten, but it can be moved aside, so the old file goes to .qaupdate\old-* and the new one takes its
    // place. If any file cannot be swapped, everything is moved back. Then the client restarts.
    public class ClientUpdater : IClientUpdater
    {
        private const string WorkFolderName = ".qaupdate";

        private readonly IApiService _apiService;
        private readonly IRepoOperations _repoOperations;
        private readonly IFileService _fileService;
        private readonly string _owner;
        private readonly string _repo;
        private readonly HashSet<string> _publishers;
        private readonly bool _disabled;
        private readonly string _installDir;
        private readonly string _workDir;

        public bool RestartPending { get; private set; }

        public string InstalledVersion
        {
            get
            {
                var state = _fileService.GetLocalState(_installDir);
                return state != null && Same(state.Owner, _owner) && Same(state.RepoName, _repo) ? state.CurrentVersion : null;
            }
        }

        public ClientUpdater(IConfiguration configuration, IApiService apiService, IRepoOperations repoOperations, IFileService fileService)
        {
            _apiService = apiService;
            _repoOperations = repoOperations;
            _fileService = fileService;

            var repository = (configuration["Update:Repository"] ?? "").Trim().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (repository.Length == 2)
            {
                _owner = repository[0];
                _repo = repository[1];
            }

            // Anyone may push to any repository, so only versions from these logins are ever installed
            var publishers = configuration.GetSection("Update:Publishers").GetChildren().Select(c => c.Value);
            _publishers = new HashSet<string>(publishers.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()), StringComparer.OrdinalIgnoreCase);
            if (_publishers.Count == 0 && _owner != null) _publishers.Add(_owner);

            _disabled = string.Equals(configuration["Update:Disabled"], "true", StringComparison.OrdinalIgnoreCase);
            _installDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            _workDir = Path.Combine(_installDir, WorkFolderName);
        }

        public async Task<UpdateResult> UpdateAsync(IProgress<TransferProgress> progress = null)
        {
            if (_owner == null) return null;
            if (_disabled) return new UpdateResult(false, "Client update check skipped for this start (--no-update)");
            if (RunningFromBuildOutput()) return new UpdateResult(false, "Client update check skipped: this copy runs from a build folder");

            var staging = Path.Combine(_workDir, "new");
            try
            {
                var versions = await _repoOperations.GetVersionsAsync(_owner, _repo);
                var latest = versions
                    .Where(v => v.CreatedBy != null && _publishers.Contains(v.CreatedBy))
                    .OrderByDescending(v => v.Name, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (latest == null)
                    return new UpdateResult(false, $"No client version from {string.Join(", ", _publishers)} in {_owner}/{_repo} yet");
                if (latest.Name == InstalledVersion)
                    return new UpdateResult(false, $"The client is up to date ({latest.Name})");

                // Starting with an empty folder inside the installation also proves it can be written to
                DeleteFolder(staging);
                Directory.CreateDirectory(staging);

                var changed = await DownloadChangesAsync(latest.Name, staging, progress);
                if (changed.Count == 0)
                {
                    Stamp(latest.Name);
                    return new UpdateResult(false, $"The client already matches {latest.Name}");
                }

                await Task.Run(() => Swap(changed, staging));
                Stamp(latest.Name);
                RestartPending = true;

                var note = string.IsNullOrWhiteSpace(latest.Message) ? "" : $": {latest.Message}";
                return new UpdateResult(true, $"Updated the client to {latest.Name} ({changed.Count} file(s) replaced; published by {latest.CreatedBy}{note})");
            }
            catch (UnauthorizedAccessException)
            {
                return new UpdateResult(false, $"A newer client is available, but this Windows account cannot write to {_installDir}. Copy the new client there, or move the client to a folder you can write to.");
            }
            catch (ApiException ex) when (ex.StatusCode is 400 or 404)
            {
                return new UpdateResult(false, $"Client update repository {_owner}/{_repo} not found ({ex.Message})");
            }
            catch (Exception ex)
            {
                return new UpdateResult(false, $"Client update failed, this version keeps running: {ex.Message}");
            }
            finally
            {
                DeleteFolder(staging);
            }
        }

        public void CleanUpInBackground()
        {
            if (!Directory.Exists(_workDir)) return;

            Task.Run(() =>
            {
                foreach (var folder in Directory.EnumerateDirectories(_workDir, "old-*"))
                    DeleteFolder(folder);
            });
        }

        // ------------------------------------------------------------------ download

        // Fills the staging folder with the files of the version that differ from this installation,
        // and returns their paths (relative, with / separators)
        private async Task<List<string>> DownloadChangesAsync(string version, string staging, IProgress<TransferProgress> progress)
        {
            var archive = Path.Combine(_workDir, $"{Guid.NewGuid():N}.zip");
            try
            {
                if (await _apiService.AgentAvailableAsync())
                {
                    FileManifest remote = null;
                    try
                    {
                        remote = await _apiService.GetAsync<FileManifest>(
                            $"sync/manifest?owner={Uri.EscapeDataString(_owner)}&repo={Uri.EscapeDataString(_repo)}&version={Uri.EscapeDataString(version)}", agent: true);
                    }
                    catch (ApiException ex) when (ex.StatusCode is 404 or 0)
                    {
                        // The agent went away: fall back to the whole version below
                    }

                    if (remote?.Version == version)
                    {
                        var needed = await Task.Run(() => remote.Files.Where(f => Differs(InstallPath(f.Path), f.Size, f.Hash)).ToList());
                        if (needed.Count == 0) return new List<string>();

                        var request = new SyncFilesRequest { Owner = _owner, Repo = _repo, Version = version, Paths = needed.Select(f => f.Path).ToList() };
                        await _apiService.DownloadFileAsync("sync/files", archive, progress, postBody: request, agent: true);
                        await Task.Run(() => _fileService.ExtractArchiveToDirectory(archive, staging));

                        // Every file must arrive exactly as it was pushed
                        foreach (var file in needed)
                        {
                            var staged = Path.Combine(staging, file.Path);
                            if (!File.Exists(staged) || Manifests.HashFile(staged) != file.Hash)
                                throw new InvalidOperationException($"{file.Path} did not arrive intact.");
                        }
                        return needed.Select(f => f.Path).ToList();
                    }
                }

                // Without the agent: the whole version, then only what differs is swapped in
                await _apiService.DownloadFileAsync(
                    $"repository/pull?owner={Uri.EscapeDataString(_owner)}&repoName={Uri.EscapeDataString(_repo)}&versionName={Uri.EscapeDataString(version)}",
                    archive, progress);
                await Task.Run(() => _fileService.ExtractArchiveToDirectory(archive, staging));

                return await Task.Run(() => Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(staging, f).Replace('\\', '/'))
                    .Where(rel =>
                    {
                        var staged = Path.Combine(staging, rel);
                        return Differs(InstallPath(rel), new FileInfo(staged).Length, Manifests.HashFile(staged));
                    })
                    .ToList());
            }
            finally
            {
                if (File.Exists(archive)) File.Delete(archive);
            }
        }

        private static bool Differs(string installed, long size, string hash)
        {
            return !File.Exists(installed) || new FileInfo(installed).Length != size || Manifests.HashFile(installed) != hash;
        }

        // ------------------------------------------------------------------ swap

        private void Swap(List<string> changed, string staging)
        {
            var backup = Path.Combine(_workDir, $"old-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
            var movedAside = new List<string>();
            var placed = new List<string>();

            try
            {
                foreach (var relative in changed)
                {
                    var target = InstallPath(relative);
                    if (File.Exists(target))
                    {
                        var aside = Path.Combine(backup, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(aside));
                        File.Move(target, aside);
                        movedAside.Add(relative);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Move(Path.Combine(staging, relative), target);
                    placed.Add(relative);
                }
            }
            catch (Exception ex)
            {
                // Put the installation back exactly as it was
                foreach (var relative in placed) TryDo(() => File.Delete(InstallPath(relative)));
                foreach (var relative in movedAside) TryDo(() => File.Move(Path.Combine(backup, relative), InstallPath(relative), true));

                if (ex is UnauthorizedAccessException) throw;
                throw new InvalidOperationException($"{ex.Message} Nothing was changed.", ex);
            }
        }

        // Records the version in the installation's .qarepo; this also remembers the folder as the local copy of the repository
        private void Stamp(string version)
        {
            _fileService.SaveLocalState(_installDir, new LocalRepoState
            {
                Owner = _owner,
                RepoName = _repo,
                CurrentVersion = version,
                LastSync = DateTime.Now,
                Description = "QA client installation, updated automatically"
            });
        }

        // A path from the server, kept inside the installation folder and out of the update workspace
        private string InstallPath(string relative)
        {
            var full = Path.GetFullPath(Path.Combine(_installDir, relative));
            if (!full.StartsWith(_installDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(_workDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{relative}' is not a valid file of the client.");
            return full;
        }

        // A developer's build (bin\Debug, bin\Release) must never be replaced by the published client
        private bool RunningFromBuildOutput()
        {
            return Debugger.IsAttached ||
                   _installDir.Split(Path.DirectorySeparatorChar).Any(s => Same(s, "bin") || Same(s, "obj"));
        }

        private static void DeleteFolder(string path)
        {
            TryDo(() => { if (Directory.Exists(path)) Directory.Delete(path, true); });
        }

        private static void TryDo(Action action)
        {
            try { action(); } catch (Exception) { /* best effort */ }
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
