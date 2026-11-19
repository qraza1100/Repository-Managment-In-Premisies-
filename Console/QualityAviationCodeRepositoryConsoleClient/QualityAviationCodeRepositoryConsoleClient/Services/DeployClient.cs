using QualityAviationRepositoryShared;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    // The deployment control panel's calls to the deploy agent on the server. Shared by the terminal menu and the
    // Windows application, so both offer exactly the same actions and see exactly the same data.
    public interface IDeployClient
    {
        Task<DeployOverview> OverviewAsync();
        Task<AppDetails> AppAsync(string app);
        Task<DeployApp> SaveAppAsync(DeployApp app);
        Task<string> RemoveAppAsync(string app);
        Task<List<string>> SetEnvironmentAsync(string app, string key, string value);

        Task<DeployRun> DeployAsync(string app, string version);
        Task<DeployRun> RollbackAsync(string app, string releaseId);
        Task<DeployAppStatus> ControlAsync(string app, string action);

        Task<RunProgress> RunAsync(string runId, long logOffset);
        Task<List<DeployRun>> RunsAsync(string app, int take);
        Task CancelRunAsync(string runId);

        Task<LogChunk> LogsAsync(string app, string kind, long after, int lines);
        Task<AppMetrics> MetricsAsync(string app, int minutes);

        Task<List<Incident>> IncidentsAsync(bool openOnly);
        Task<Incident> IncidentActionAsync(string incidentId, string action, string text);

        Task<List<AuditEntry>> AuditAsync(int take);
        Task<StorageReport> OptimizeStorageAsync(bool apply);
    }

    public class DeployClient : IDeployClient
    {
        private readonly IApiService _api;

        public DeployClient(IApiService api)
        {
            _api = api;
        }

        public Task<DeployOverview> OverviewAsync() => _api.GetAsync<DeployOverview>("deploy/overview", agent: true);

        public Task<AppDetails> AppAsync(string app) => _api.GetAsync<AppDetails>($"deploy/apps/{Escape(app)}", agent: true);

        public Task<DeployApp> SaveAppAsync(DeployApp app) => _api.PostAsync<DeployApp>("deploy/apps", app, agent: true);

        public async Task<string> RemoveAppAsync(string app)
        {
            var answer = await _api.PostAsync<MessageAnswer>($"deploy/apps/{Escape(app)}/remove", new { }, agent: true);
            return answer?.Message;
        }

        public async Task<List<string>> SetEnvironmentAsync(string app, string key, string value)
        {
            var answer = await _api.PostAsync<MessageAnswer>($"deploy/apps/{Escape(app)}/environment", new EnvironmentRequest { Key = key, Value = value }, agent: true);
            return answer?.Keys ?? new();
        }

        public Task<DeployRun> DeployAsync(string app, string version) =>
            _api.PostAsync<DeployRun>($"deploy/apps/{Escape(app)}/deploy", new DeployRequest { Version = version }, agent: true);

        public Task<DeployRun> RollbackAsync(string app, string releaseId) =>
            _api.PostAsync<DeployRun>($"deploy/apps/{Escape(app)}/rollback", new RollbackRequest { ReleaseId = releaseId }, agent: true);

        // start | stop | restart
        public Task<DeployAppStatus> ControlAsync(string app, string action) =>
            _api.PostAsync<DeployAppStatus>($"deploy/apps/{Escape(app)}/{action}", new { }, agent: true);

        public Task<RunProgress> RunAsync(string runId, long logOffset) =>
            _api.GetAsync<RunProgress>($"deploy/runs/{Escape(runId)}?offset={logOffset}", agent: true);

        public Task<List<DeployRun>> RunsAsync(string app, int take) =>
            _api.GetAsync<List<DeployRun>>($"deploy/runs?take={take}" + (app == null ? "" : $"&app={Escape(app)}"), agent: true);

        public Task CancelRunAsync(string runId) => _api.PostAsync<MessageAnswer>($"deploy/runs/{Escape(runId)}/cancel", new { }, agent: true);

        // app | access
        public Task<LogChunk> LogsAsync(string app, string kind, long after, int lines) =>
            _api.GetAsync<LogChunk>($"deploy/apps/{Escape(app)}/logs?kind={kind}&after={after}&lines={lines}", agent: true);

        public Task<AppMetrics> MetricsAsync(string app, int minutes) =>
            _api.GetAsync<AppMetrics>($"deploy/apps/{Escape(app)}/metrics?minutes={minutes}", agent: true);

        public Task<List<Incident>> IncidentsAsync(bool openOnly) =>
            _api.GetAsync<List<Incident>>($"deploy/incidents?open={openOnly.ToString().ToLowerInvariant()}", agent: true);

        // acknowledge | resolve | note
        public Task<Incident> IncidentActionAsync(string incidentId, string action, string text) =>
            _api.PostAsync<Incident>($"deploy/incidents/{Escape(incidentId)}/{action}", new NoteRequest { Text = text }, agent: true);

        public Task<List<AuditEntry>> AuditAsync(int take) => _api.GetAsync<List<AuditEntry>>($"deploy/audit?take={take}", agent: true);

        public Task<StorageReport> OptimizeStorageAsync(bool apply) =>
            _api.PostAsync<StorageReport>($"storage/optimize?apply={apply.ToString().ToLowerInvariant()}", new { }, agent: true);

        private static string Escape(string value) => Uri.EscapeDataString(value ?? "");

        private sealed class MessageAnswer
        {
            public string Message { get; set; }
            public List<string> Keys { get; set; }
        }
    }

    // Small helpers both control panels use to describe things the same way
    public static class DeployText
    {
        public static string Ago(DateTime? time)
        {
            if (time == null) return "";
            var span = DateTime.Now - time.Value;
            return span.TotalSeconds < 60 ? "just now"
                : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago"
                : span.TotalHours < 24 ? $"{(int)span.TotalHours} h ago"
                : $"{(int)span.TotalDays} d ago";
        }

        public static string Duration(DateTime? from, DateTime? to)
        {
            if (from == null) return "";
            var span = (to ?? DateTime.Now) - from.Value;
            return span.TotalSeconds < 60 ? $"{span.TotalSeconds:0.0} s" : $"{(int)span.TotalMinutes} min {span.Seconds} s";
        }

        public static string StateLabel(string state) => state switch
        {
            "running" => "Running",
            "starting" => "Starting",
            "unhealthy" => "Not answering",
            "stopped" => "Stopped",
            "crashed" => "Crashed",
            "not-deployed" => "Not deployed",
            _ => state
        };

        public static string Where(BuildDiagnostic diagnostic) =>
            string.IsNullOrEmpty(diagnostic.File) ? "" : $"{diagnostic.File}{(diagnostic.Line > 0 ? $":{diagnostic.Line}" : "")}{(diagnostic.Column > 0 ? $":{diagnostic.Column}" : "")}";

        public static string Where(SecurityFinding finding) =>
            finding.File == null ? finding.Package ?? "" : $"{finding.File}{(finding.Line > 0 ? $":{finding.Line}" : "")}";
    }
}
