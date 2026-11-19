namespace QualityAviationRepositoryShared
{
    // The contract between the deploy agent on the server and the clients that control it.
    // Both sides compile these same classes, so they cannot drift apart.

    // ------------------------------------------------------------------ applications

    // How one repository is built and run on the server. Kept on the server and edited by administrators only:
    // nothing in here can be changed by pushing to the repository.
    public class DeployApp
    {
        public const string KindDotnet = "dotnet";
        public const string KindCustom = "custom";

        public string Name { get; set; }
        public string Owner { get; set; }
        public string Repo { get; set; }
        public string Kind { get; set; } = KindDotnet;

        // dotnet: the project to publish, relative to the repository root. Empty = the only web project in it.
        public string Project { get; set; }
        public bool SelfContained { get; set; }
        public bool RunTests { get; set; } = true;

        // The port users reach the application on. The agent listens there and forwards to the running release.
        // 0 = not a web application (a worker or scheduled job): it is only started, watched and restarted.
        public int PublicPort { get; set; }
        public bool Https { get; set; }
        public string HealthPath { get; set; } = "/";

        // "report" lists what the security checks find; "block" also stops the deployment on serious findings
        public string SecurityGate { get; set; } = "report";

        // Who may deploy, roll back, start and stop it, besides the administrators
        public List<string> Deployers { get; set; } = new();

        // custom: commands run in the checked-out source ({output} = release folder) and, for Start, in the release
        // folder ({port} = the port the application must listen on, also given as the PORT environment variable)
        public string BuildCommand { get; set; }
        public string TestCommand { get; set; }
        public string PublishCommand { get; set; }
        public string StartCommand { get; set; }
    }

    public class DeployAppStatus
    {
        public DeployApp App { get; set; }

        // not-deployed | starting | running | unhealthy | stopped | crashed
        public string State { get; set; }
        public string StateDetail { get; set; }

        public string LiveVersion { get; set; }
        public string LiveReleaseId { get; set; }
        public DateTime? LiveSince { get; set; }
        public string DeployedBy { get; set; }
        public string LatestVersion { get; set; }

        public int? ProcessId { get; set; }
        public DateTime? StartedAt { get; set; }
        public int Restarts { get; set; }

        public string ActiveRunId { get; set; }
        public DeployRun LastRun { get; set; }
        public int OpenIncidents { get; set; }
        public MetricsSummary Now { get; set; }

        // The caller may deploy, roll back, start and stop this application and read its logs
        public bool CanOperate { get; set; }
    }

    public class AppDetails
    {
        public DeployAppStatus Status { get; set; }
        public List<ReleaseInfo> Releases { get; set; } = new();
        public List<DeployRun> Runs { get; set; } = new();
        public List<string> EnvironmentKeys { get; set; } = new();
        public List<string> ConfigFiles { get; set; } = new();
    }

    public class ReleaseInfo
    {
        public string Id { get; set; }
        public string Version { get; set; }
        public string RunId { get; set; }
        public string DeployedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public int FileCount { get; set; }
        public long SizeBytes { get; set; }
        public string Fingerprint { get; set; }
        public bool IsLive { get; set; }
    }

    public class DeployOverview
    {
        public string User { get; set; }
        public bool IsAdmin { get; set; }
        public string AgentVersion { get; set; }
        public string SdkVersion { get; set; }
        public List<string> Warnings { get; set; } = new();
        public List<DeployAppStatus> Apps { get; set; } = new();
        public int OpenIncidents { get; set; }
    }

    // ------------------------------------------------------------------ pipeline runs

    public class DeployRun
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";

        public string Id { get; set; }
        public string App { get; set; }
        public string Kind { get; set; }             // deploy | rollback
        public string Status { get; set; }
        public string Error { get; set; }

        public string Version { get; set; }
        public string VersionAuthor { get; set; }
        public string VersionMessage { get; set; }
        public string ReleaseId { get; set; }
        public string RequestedBy { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }

        public List<DeployStage> Stages { get; set; } = new();
        public List<BuildDiagnostic> Diagnostics { get; set; } = new();
        public TestSummary Tests { get; set; }
        public List<SecurityFinding> Findings { get; set; } = new();
        public ChangeSummary SourceChanges { get; set; }
        public ChangeSummary ReleaseChanges { get; set; }

        public bool IsActive => Status == Queued || Status == Running;
    }

    // One node of the pipeline graph
    public class DeployStage
    {
        public const string Pending = "pending";
        public const string Running = "running";
        public const string Succeeded = "succeeded";
        public const string Warning = "warning";
        public const string Failed = "failed";
        public const string Skipped = "skipped";

        public string Key { get; set; }
        public string Title { get; set; }
        public string Status { get; set; } = Pending;
        public string Summary { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
    }

    public class BuildDiagnostic
    {
        public string Severity { get; set; }         // error | warning
        public string Code { get; set; }
        public string Message { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Project { get; set; }
    }

    public class TestSummary
    {
        public int Total { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public List<TestFailure> Failures { get; set; } = new();
    }

    public class TestFailure
    {
        public string Name { get; set; }
        public string Message { get; set; }
    }

    public class SecurityFinding
    {
        public string Kind { get; set; }             // secret | vulnerability | integrity
        public string Severity { get; set; }         // critical | high | moderate | low | info
        public string Title { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public string Package { get; set; }
        public string Url { get; set; }
    }

    public class ChangeSummary
    {
        public string ComparedTo { get; set; }
        public int Added { get; set; }
        public int Modified { get; set; }
        public int Deleted { get; set; }
        public List<FileChange> Files { get; set; } = new();     // capped; the counts above are always complete
    }

    public class RunProgress
    {
        public DeployRun Run { get; set; }
        public string Log { get; set; }
        public long NextOffset { get; set; }
    }

    // ------------------------------------------------------------------ runtime

    public class MetricsSummary
    {
        public int ActiveUsers { get; set; }         // distinct client addresses in the last 5 minutes
        public int UsersToday { get; set; }
        public int RequestsPerMinute { get; set; }
        public long RequestsToday { get; set; }
        public int ActiveRequests { get; set; }
        public double ErrorRate { get; set; }        // share of requests answered with 5xx, last 5 minutes (0..1)
        public double AverageMs { get; set; }
        public double P95Ms { get; set; }
    }

    public class MetricsPoint
    {
        public DateTime Minute { get; set; }
        public int Requests { get; set; }
        public int Errors { get; set; }
        public int Users { get; set; }
        public double AverageMs { get; set; }
        public double P95Ms { get; set; }
    }

    public class AppMetrics
    {
        public MetricsSummary Summary { get; set; }
        public List<MetricsPoint> Points { get; set; } = new();
    }

    public class LogChunk
    {
        public List<string> Lines { get; set; } = new();
        public long Next { get; set; }
    }

    public class Incident
    {
        public const string Open = "open";
        public const string Acknowledged = "acknowledged";
        public const string Resolved = "resolved";

        public string Id { get; set; }
        public string App { get; set; }
        public string Type { get; set; }             // down | unhealthy | errors | integrity | deployment
        public string Severity { get; set; }         // warning | critical
        public string Status { get; set; }
        public string Title { get; set; }
        public string Details { get; set; }
        public DateTime OpenedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public string AcknowledgedBy { get; set; }
        public List<IncidentEvent> Timeline { get; set; } = new();
    }

    public class IncidentEvent
    {
        public DateTime At { get; set; }
        public string By { get; set; }
        public string Text { get; set; }
    }

    public class AuditEntry
    {
        public DateTime At { get; set; }
        public string User { get; set; }
        public string Action { get; set; }
        public string Target { get; set; }
        public string Detail { get; set; }
        public string Address { get; set; }
    }

    public class StorageReport
    {
        public int Versions { get; set; }
        public int Files { get; set; }
        public long TotalBytes { get; set; }
        public int DuplicateFiles { get; set; }
        public long DuplicateBytes { get; set; }     // space identical copies take (dry run) or took (after optimizing)
        public int LinkedFiles { get; set; }
        public long SavedBytes { get; set; }
        public bool Applied { get; set; }
    }

    // ------------------------------------------------------------------ requests

    public class DeployRequest
    {
        public string Version { get; set; }          // empty = latest
    }

    public class RollbackRequest
    {
        public string ReleaseId { get; set; }        // empty = the release that was live before the current one
    }

    public class EnvironmentRequest
    {
        public string Key { get; set; }
        public string Value { get; set; }            // null or empty removes the key
    }

    public class NoteRequest
    {
        public string Text { get; set; }
    }

    public class SyncFilesRequest
    {
        public string Owner { get; set; }
        public string Repo { get; set; }
        public string Version { get; set; }
        public List<string> Paths { get; set; } = new();
    }
}
