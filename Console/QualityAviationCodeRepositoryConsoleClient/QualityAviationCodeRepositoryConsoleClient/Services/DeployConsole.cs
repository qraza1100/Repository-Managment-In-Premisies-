using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using QualityAviationRepositoryShared;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    // The deployment control panel in the terminal: the same actions as the Deployments tab of the Windows application
    public class DeployConsole
    {
        private static readonly char[] Blocks = { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

        private readonly IDeployClient _deploy;
        private readonly IRepoOperations _repoOperations;
        private DeployOverview _overview;

        public DeployConsole(IDeployClient deploy, IRepoOperations repoOperations)
        {
            _deploy = deploy;
            _repoOperations = repoOperations;
        }

        public async Task RunAsync()
        {
            while (true)
            {
                _overview = await _deploy.OverviewAsync();
                PrintOverview();

                var choice = Prompt("➤ Application number, or I/N/O/A/R, 0 to go back: ").ToUpperInvariant();
                if (choice is "0" or "") return;

                try
                {
                    if (int.TryParse(choice, out var number) && number >= 1 && number <= _overview.Apps.Count)
                        await AppMenuAsync(_overview.Apps[number - 1].App.Name);
                    else if (choice == "I") await IncidentsAsync();
                    else if (choice == "N") await EditAppAsync(null);
                    else if (choice == "O") await OptimizeStorageAsync();
                    else if (choice == "A") await AuditAsync();
                    else if (choice != "R") Write("❌ Invalid option", ConsoleColor.Red);
                }
                catch (ApiException ex) when (!ex.IsUnauthorized)
                {
                    Write($"❌ {ex.Message}", ConsoleColor.Red);
                }
            }
        }

        // ------------------------------------------------------------------ overview

        private void PrintOverview()
        {
            Header("🚀 DEPLOYMENTS", $"{_overview.User}{(_overview.IsAdmin ? " (administrator)" : "")} · agent {_overview.AgentVersion} · .NET SDK {_overview.SdkVersion ?? "not installed"}");
            foreach (var warning in _overview.Warnings) Write($"⚠️  {warning}", ConsoleColor.Yellow);

            if (_overview.Apps.Count == 0)
            {
                Write("   No applications are registered yet." + (_overview.IsAdmin ? " Press N to add one." : " An administrator can add them."), ConsoleColor.DarkGray);
            }
            else
            {
                Write($"  {"#",-3}{"Application",-18}{"State",-16}{"Live version",-20}{"Users",6}{"Req/min",9}{"Errors",8}  Last run", ConsoleColor.DarkGray);
                for (var i = 0; i < _overview.Apps.Count; i++)
                {
                    var status = _overview.Apps[i];
                    var now = status.Now ?? new MetricsSummary();
                    System.Console.Write($"  {i + 1,-3}{Cut(status.App.Name, 17),-18}");
                    Write($"{Dot(status.State)} {Cut(DeployText.StateLabel(status.State), 13),-14}", StateColor(status.State), newLine: false);
                    System.Console.Write($"{Cut(status.LiveVersion ?? "-", 19),-20}");
                    System.Console.Write(status.App.PublicPort > 0 ? $"{now.ActiveUsers,6}{now.RequestsPerMinute,9}{now.ErrorRate,8:P0}" : $"{"",6}{"",9}{"",8}");
                    var run = status.LastRun;
                    Write(run == null ? "  -" : $"  {run.Status} {DeployText.Ago(run.FinishedAt ?? run.CreatedAt)}" + (status.OpenIncidents > 0 ? $"  ⚠ {status.OpenIncidents} incident(s)" : ""),
                        run?.Status == DeployRun.Failed || status.OpenIncidents > 0 ? ConsoleColor.Yellow : ConsoleColor.Gray);
                }
            }

            System.Console.WriteLine();
            System.Console.WriteLine($"  I  Incidents{(_overview.OpenIncidents > 0 ? $" ({_overview.OpenIncidents} open)" : "")}" +
                                     (_overview.IsAdmin ? "   N  New application   O  Optimize storage   A  Audit trail" : "") + "   R  Refresh   0  Back");
        }

        // ------------------------------------------------------------------ one application

        private async Task AppMenuAsync(string name)
        {
            while (true)
            {
                var details = await _deploy.AppAsync(name);
                var status = details.Status;
                var app = status.App;

                Header($"📦 {app.Name}", $"{app.Owner}/{app.Repo}{(app.PublicPort > 0 ? $" · port {app.PublicPort}{(app.Https ? " (https)" : "")}" : " · no web address")}");
                Write($"  {Dot(status.State)} {DeployText.StateLabel(status.State)} · {status.StateDetail}", StateColor(status.State));
                if (status.LiveVersion != null)
                    System.Console.WriteLine($"  Live: {status.LiveVersion} (release {status.LiveReleaseId}) deployed by {status.DeployedBy} {DeployText.Ago(status.LiveSince)}" +
                                             (status.ProcessId != null ? $" · process {status.ProcessId}" : "") + (status.Restarts > 0 ? $" · {status.Restarts} automatic restart(s)" : ""));
                if (status.LatestVersion != null && status.LatestVersion != status.LiveVersion)
                    Write($"  Newer version in the repository: {status.LatestVersion}", ConsoleColor.Cyan);
                if (status.Now != null && app.PublicPort > 0)
                    System.Console.WriteLine($"  Now: {status.Now.ActiveUsers} user(s) active, {status.Now.RequestsPerMinute} requests/min, {status.Now.ErrorRate:P1} errors, p95 {status.Now.P95Ms:0} ms");
                if (status.OpenIncidents > 0) Write($"  ⚠ {status.OpenIncidents} open incident(s)", ConsoleColor.Yellow);
                if (status.LastRun != null)
                {
                    System.Console.WriteLine();
                    PrintPipeline(status.LastRun);
                }

                System.Console.WriteLine();
                if (status.CanOperate)
                    System.Console.WriteLine("  1 Deploy latest   2 Deploy a version   3 Roll back   4 Restart   5 " + (status.State == "stopped" ? "Start" : "Stop"));
                System.Console.WriteLine("  6 Pipeline history   " + (status.CanOperate ? "7 Live log   8 Access log   " : "") + "9 Users & traffic   I Incidents" +
                                         (_overview.IsAdmin ? "   E Environment   S Settings   X Remove" : "") + "   0 Back");

                var choice = Prompt("➤ ").ToUpperInvariant();
                try
                {
                    switch (choice)
                    {
                        case "0": case "": return;
                        case "1": await FollowAsync(await _deploy.DeployAsync(name, null)); break;
                        case "2": await DeployVersionAsync(app); break;
                        case "3": await RollbackAsync(details); break;
                        case "4": Show(await _deploy.ControlAsync(name, "restart")); break;
                        case "5": Show(await _deploy.ControlAsync(name, status.State == "stopped" ? "start" : "stop")); break;
                        case "6": await HistoryAsync(details); break;
                        case "7": await FollowLogAsync(name, "app"); break;
                        case "8": await FollowLogAsync(name, "access"); break;
                        case "9": await MetricsAsync(name); break;
                        case "I": await IncidentsAsync(name); break;
                        case "E": await EnvironmentAsync(details); break;
                        case "S": await EditAppAsync(app); break;
                        case "X":
                            if (Prompt($"Type the name '{app.Name}' to stop and remove it: ") == app.Name)
                            {
                                Write($"✅ {await _deploy.RemoveAppAsync(app.Name)}", ConsoleColor.Green);
                                Pause();
                                return;
                            }
                            break;
                        default: Write("❌ Invalid option", ConsoleColor.Red); break;
                    }
                }
                catch (ApiException ex) when (!ex.IsUnauthorized)
                {
                    Write($"❌ {ex.Message}", ConsoleColor.Red);
                }
                Pause();
            }
        }

        private async Task DeployVersionAsync(DeployApp app)
        {
            var versions = await _repoOperations.GetVersionsAsync(app.Owner, app.Repo);
            if (versions.Count == 0) { Write("❌ The repository has no versions yet.", ConsoleColor.Red); return; }

            System.Console.WriteLine();
            for (var i = 0; i < Math.Min(versions.Count, 15); i++)
                System.Console.WriteLine($"  {i + 1,-3}{versions[i].Name}  {versions[i].CreatedAt:yyyy-MM-dd HH:mm}  {versions[i].CreatedBy,-10} {Cut(versions[i].Message ?? "", 50)}");

            var choice = Prompt("➤ Number of the version to deploy: ");
            if (int.TryParse(choice, out var n) && n >= 1 && n <= Math.Min(versions.Count, 15))
                await FollowAsync(await _deploy.DeployAsync(app.Name, versions[n - 1].Name));
        }

        private async Task RollbackAsync(AppDetails details)
        {
            var candidates = details.Releases.Where(r => !r.IsLive).ToList();
            if (candidates.Count == 0) { Write("❌ There is no earlier release on the server to go back to.", ConsoleColor.Red); return; }

            System.Console.WriteLine();
            for (var i = 0; i < candidates.Count; i++)
                System.Console.WriteLine($"  {i + 1,-3}{candidates[i].Id}  version {candidates[i].Version}  built {candidates[i].CreatedAt:yyyy-MM-dd HH:mm} for {candidates[i].DeployedBy}");

            var choice = Prompt("➤ Release to go back to (Enter = 1, the previous one): ");
            var index = string.IsNullOrEmpty(choice) ? 1 : int.TryParse(choice, out var n) ? n : 0;
            if (index >= 1 && index <= candidates.Count)
                await FollowAsync(await _deploy.RollbackAsync(details.Status.App.Name, candidates[index - 1].Id));
        }

        // ------------------------------------------------------------------ pipeline

        // Shows the run as it goes: the pipeline line whenever a stage changes, and the build output underneath
        private async Task FollowAsync(DeployRun run)
        {
            System.Console.WriteLine();
            Write($"▶ {(run.Kind == "rollback" ? "Rollback" : "Deployment")} of {run.App} started ({run.Id})", ConsoleColor.Cyan);

            long offset = 0;
            var lastShape = "";
            var showLog = Prompt("   Show the build output while it runs? (y/N): ").ToLowerInvariant() == "y";

            while (true)
            {
                var progress = await _deploy.RunAsync(run.Id, offset);
                offset = progress.NextOffset;
                run = progress.Run;

                if (showLog && progress.Log.Length > 0)
                    foreach (var line in progress.Log.TrimEnd('\n', '\r').Split('\n')) Write("   │ " + line.TrimEnd('\r'), ConsoleColor.DarkGray);

                var shape = string.Join(",", run.Stages.Select(s => s.Status));
                if (shape != lastShape)
                {
                    lastShape = shape;
                    PrintNodes(run);
                }

                if (!run.IsActive) break;
                await Task.Delay(700);
            }

            System.Console.WriteLine();
            PrintRun(run);
        }

        private void PrintPipeline(DeployRun run)
        {
            Write($"  Last run: {(run.Kind == "rollback" ? "rollback" : "deployment")} of {run.Version ?? "?"} by {run.RequestedBy}, {run.Status} {DeployText.Ago(run.FinishedAt ?? run.CreatedAt)}",
                RunColor(run.Status));
            PrintNodes(run);
        }

        // The pipeline as a row of nodes: ● done  ▲ done with warnings  ✖ failed  ◐ running  ○ waiting  · skipped
        private static void PrintNodes(DeployRun run)
        {
            System.Console.Write("  ");
            for (var i = 0; i < run.Stages.Count; i++)
            {
                var stage = run.Stages[i];
                if (i > 0) Write(" ── ", ConsoleColor.DarkGray, newLine: false);
                Write($"{StageMark(stage.Status)} {stage.Title}", StageColor(stage.Status), newLine: false);
            }
            System.Console.WriteLine();
        }

        private static void PrintRun(DeployRun run)
        {
            foreach (var stage in run.Stages.Where(s => !string.IsNullOrEmpty(s.Summary)))
                Write($"  {StageMark(stage.Status)} {stage.Title,-9} {stage.Summary}", StageColor(stage.Status));

            var errors = run.Diagnostics.Where(d => d.Severity == "error").ToList();
            if (errors.Count > 0)
            {
                Write($"\n  Compile errors ({errors.Count}):", ConsoleColor.Red);
                foreach (var error in errors.Take(20)) Write($"    {error.Code} {DeployText.Where(error)}  {error.Message}", ConsoleColor.Red);
            }

            var warnings = run.Diagnostics.Count(d => d.Severity == "warning");
            if (warnings > 0) Write($"  {warnings} compiler warning(s)", ConsoleColor.DarkYellow);

            if (run.Tests?.Failures.Count > 0)
            {
                Write($"\n  Failed tests ({run.Tests.Failed} of {run.Tests.Total}):", ConsoleColor.Red);
                foreach (var failure in run.Tests.Failures.Take(10))
                    Write($"    ✖ {failure.Name}: {failure.Message?.Replace("\r", "").Replace("\n", " ")}", ConsoleColor.Red);
            }

            if (run.Findings.Count > 0)
            {
                Write($"\n  Security and integrity ({run.Findings.Count}):", ConsoleColor.Yellow);
                foreach (var finding in run.Findings.Take(15))
                    Write($"    {finding.Severity,-9}{finding.Title}  {DeployText.Where(finding)}", finding.Severity is "critical" or "high" ? ConsoleColor.Red : ConsoleColor.Yellow);
            }

            if (run.SourceChanges != null)
                System.Console.WriteLine($"\n  Source compared with {run.SourceChanges.ComparedTo}: {run.SourceChanges.Added} added, {run.SourceChanges.Modified} changed, {run.SourceChanges.Deleted} removed" +
                                         (run.SourceChanges.Files.Count > 0 ? $" ({string.Join(", ", run.SourceChanges.Files.Take(6).Select(f => f.Path))}{(run.SourceChanges.Files.Count > 6 ? ", ..." : "")})" : ""));

            System.Console.WriteLine();
            if (run.Status == DeployRun.Succeeded)
                Write($"✅ {run.Version} is live (release {run.ReleaseId}). Took {DeployText.Duration(run.StartedAt, run.FinishedAt)}.", ConsoleColor.Green);
            else
                Write($"❌ {run.Status}: {run.Error}", ConsoleColor.Red);
        }

        private async Task HistoryAsync(AppDetails details)
        {
            if (details.Runs.Count == 0) { System.Console.WriteLine("  No runs yet."); return; }

            System.Console.WriteLine();
            for (var i = 0; i < details.Runs.Count; i++)
            {
                var run = details.Runs[i];
                Write($"  {i + 1,-3}{run.CreatedAt:MM-dd HH:mm}  {run.Kind,-9}{run.Version ?? "",-19} {run.RequestedBy,-10} {run.Status,-10} {DeployText.Duration(run.StartedAt, run.FinishedAt)}",
                    RunColor(run.Status));
            }

            var choice = Prompt("➤ Number of a run to see its details: ");
            if (!int.TryParse(choice, out var n) || n < 1 || n > details.Runs.Count) return;

            var progress = await _deploy.RunAsync(details.Runs[n - 1].Id, 0);
            System.Console.WriteLine();
            PrintNodes(progress.Run);
            PrintRun(progress.Run);

            if (Prompt("   Show the full build output? (y/N): ").ToLowerInvariant() == "y")
                foreach (var line in progress.Log.Split('\n')) Write("   │ " + line.TrimEnd('\r'), ConsoleColor.DarkGray);
        }

        // ------------------------------------------------------------------ runtime

        private async Task FollowLogAsync(string app, string kind)
        {
            var chunk = await _deploy.LogsAsync(app, kind, 0, 40);
            Write($"\n  {(kind == "access" ? "Access log" : "Application log")} of {app} (last {chunk.Lines.Count} lines){(System.Console.IsInputRedirected ? "" : ", following: press any key to stop")}", ConsoleColor.Cyan);
            foreach (var line in chunk.Lines) System.Console.WriteLine("  " + line);

            if (System.Console.IsInputRedirected) return;

            var next = chunk.Next;
            while (!System.Console.KeyAvailable)
            {
                await Task.Delay(1000);
                chunk = await _deploy.LogsAsync(app, kind, next, 500);
                next = chunk.Next;
                foreach (var line in chunk.Lines) System.Console.WriteLine("  " + line);
            }
            System.Console.ReadKey(true);
        }

        private async Task MetricsAsync(string app)
        {
            var metrics = await _deploy.MetricsAsync(app, 60);
            var s = metrics.Summary;

            Write($"\n  Users and traffic of {app}", ConsoleColor.Cyan);
            System.Console.WriteLine($"  Active users (last 5 min): {s.ActiveUsers}    Users today: {s.UsersToday}    Requests today: {s.RequestsToday}    In progress: {s.ActiveRequests}");
            System.Console.WriteLine($"  Requests/min: {s.RequestsPerMinute}    Errors: {s.ErrorRate:P1}    Response time: avg {s.AverageMs:0} ms, p95 {s.P95Ms:0} ms");
            System.Console.WriteLine();
            System.Console.WriteLine($"  Requests per minute, last hour   {Sparkline(metrics.Points.Select(p => (double)p.Requests))}  max {metrics.Points.Select(p => p.Requests).DefaultIfEmpty().Max()}");
            System.Console.WriteLine($"  Users per minute                 {Sparkline(metrics.Points.Select(p => (double)p.Users))}  max {metrics.Points.Select(p => p.Users).DefaultIfEmpty().Max()}");
            Write($"  Server errors per minute         {Sparkline(metrics.Points.Select(p => (double)p.Errors))}  max {metrics.Points.Select(p => p.Errors).DefaultIfEmpty().Max()}",
                metrics.Points.Any(p => p.Errors > 0) ? ConsoleColor.Yellow : ConsoleColor.Gray);
            System.Console.WriteLine($"  p95 response time (ms)           {Sparkline(metrics.Points.Select(p => p.P95Ms))}  max {metrics.Points.Select(p => p.P95Ms).DefaultIfEmpty().Max():0}");
        }

        // ------------------------------------------------------------------ incidents

        private async Task IncidentsAsync(string app = null)
        {
            var incidents = (await _deploy.IncidentsAsync(false)).Where(i => app == null || i.App == app).Take(20).ToList();
            if (incidents.Count == 0) { Write("\n  ✅ No incidents.", ConsoleColor.Green); return; }

            System.Console.WriteLine();
            for (var i = 0; i < incidents.Count; i++)
            {
                var incident = incidents[i];
                Write($"  {i + 1,-3}{incident.OpenedAt:MM-dd HH:mm}  {incident.Status,-13}{incident.Severity,-9}{incident.App,-14}{Cut(incident.Title, 60)}",
                    incident.Status == Incident.Resolved ? ConsoleColor.DarkGray : incident.Severity == "critical" ? ConsoleColor.Red : ConsoleColor.Yellow);
            }

            var choice = Prompt("➤ Number of an incident to open it: ");
            if (!int.TryParse(choice, out var n) || n < 1 || n > incidents.Count) return;

            var selected = incidents[n - 1];
            while (true)
            {
                Write($"\n  {selected.Title}", selected.Status == Incident.Resolved ? ConsoleColor.Gray : ConsoleColor.Yellow);
                System.Console.WriteLine($"  {selected.App} · {selected.Type} · {selected.Severity} · {selected.Status}" + (selected.AcknowledgedBy != null ? $" (acknowledged by {selected.AcknowledgedBy})" : ""));
                if (!string.IsNullOrEmpty(selected.Details))
                    foreach (var line in selected.Details.Split('\n').Take(15)) Write("  " + line.TrimEnd('\r'), ConsoleColor.DarkGray);
                System.Console.WriteLine("  Timeline:");
                foreach (var entry in selected.Timeline) System.Console.WriteLine($"    {entry.At:MM-dd HH:mm:ss}  {entry.By,-10} {entry.Text}");

                if (selected.Status == Incident.Resolved) return;

                var action = Prompt("➤ A Acknowledge   R Resolve   N Add a note   0 Back: ").ToUpperInvariant();
                selected = action switch
                {
                    "A" => await _deploy.IncidentActionAsync(selected.Id, "acknowledge", null),
                    "R" => await _deploy.IncidentActionAsync(selected.Id, "resolve", Prompt("   What fixed it (optional): ")),
                    "N" => await _deploy.IncidentActionAsync(selected.Id, "note", Prompt("   Note: ")),
                    _ => null
                };
                if (selected == null) return;
            }
        }

        // ------------------------------------------------------------------ administration

        private async Task EditAppAsync(DeployApp existing)
        {
            var app = existing ?? new DeployApp();
            Write(existing == null ? "\n  New application (Enter keeps the value in brackets)" : $"\n  Settings of {app.Name} (Enter keeps the value in brackets)", ConsoleColor.Cyan);

            if (existing == null) app.Name = Ask("Name", app.Name);
            app.Owner = Ask("Repository owner", app.Owner);
            app.Repo = Ask("Repository name", app.Repo);
            app.Kind = Ask("Kind: dotnet or custom", app.Kind);
            if (app.Kind == DeployApp.KindCustom)
            {
                app.BuildCommand = Ask("Build command", app.BuildCommand);
                app.TestCommand = Ask("Test command", app.TestCommand);
                app.PublishCommand = Ask("Publish command ({output} = release folder; empty = copy the source)", app.PublishCommand);
                app.StartCommand = Ask("Start command ({port} = the port to listen on)", app.StartCommand);
            }
            else
            {
                app.Project = Ask("Project to publish (empty = the only one in the repository)", app.Project);
                app.SelfContained = Ask("Self-contained (y/n)", app.SelfContained ? "y" : "n") == "y";
            }
            app.PublicPort = int.TryParse(Ask("Public port (0 = no web address)", app.PublicPort.ToString()), out var port) ? port : app.PublicPort;
            if (app.PublicPort > 0)
            {
                app.Https = Ask("HTTPS with the server's certificate (y/n)", app.Https ? "y" : "n") == "y";
                app.HealthPath = Ask("Health check path", app.HealthPath);
            }
            app.RunTests = Ask("Run tests before deploying (y/n)", app.RunTests ? "y" : "n") == "y";
            app.SecurityGate = Ask("Security findings: report or block", app.SecurityGate);
            app.Deployers = Ask("Who may deploy it (comma separated logins)", string.Join(",", app.Deployers)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var saved = await _deploy.SaveAppAsync(app);
            Write($"✅ Saved '{saved.Name}'.{(existing == null ? " Open it and choose Deploy to bring it live." : "")}", ConsoleColor.Green);
        }

        private async Task EnvironmentAsync(AppDetails details)
        {
            var name = details.Status.App.Name;
            Write($"\n  Environment of {name}: values are stored encrypted on the server and can be replaced, never read back.", ConsoleColor.Cyan);
            System.Console.WriteLine("  Set now: " + (details.EnvironmentKeys.Count == 0 ? "(none)" : string.Join(", ", details.EnvironmentKeys)));

            var key = Prompt("  Name to set or replace (e.g. ConnectionStrings__Default), empty to go back: ");
            if (string.IsNullOrWhiteSpace(key)) return;

            var value = ReadSecret("  Value (hidden; empty removes it): ");
            var keys = await _deploy.SetEnvironmentAsync(name, key, value);
            Write($"✅ Saved. Set now: {string.Join(", ", keys)}. It takes effect at the next restart or deployment.", ConsoleColor.Green);
        }

        private async Task OptimizeStorageAsync()
        {
            var report = await _deploy.OptimizeStorageAsync(false);
            System.Console.WriteLine($"\n  {report.Versions} versions, {report.Files} files, {RepoOperations.FormatSize(report.TotalBytes)} in the repository store.");
            System.Console.WriteLine($"  Already shared: {report.LinkedFiles} files ({RepoOperations.FormatSize(report.SavedBytes)}).");
            System.Console.WriteLine($"  Identical copies that can be shared: {report.DuplicateFiles} files ({RepoOperations.FormatSize(report.DuplicateBytes)}).");
            if (report.DuplicateFiles == 0) return;

            if (Prompt("  Keep one copy of each and link the others to it? Nothing changes for pushes and pulls. (y/N): ").ToLowerInvariant() == "y")
            {
                report = await _deploy.OptimizeStorageAsync(true);
                Write($"✅ {report.LinkedFiles} files share storage now; {RepoOperations.FormatSize(report.SavedBytes)} saved in total.", ConsoleColor.Green);
            }
        }

        private async Task AuditAsync()
        {
            System.Console.WriteLine();
            foreach (var entry in await _deploy.AuditAsync(40))
                System.Console.WriteLine($"  {entry.At:MM-dd HH:mm:ss}  {entry.User,-10} {entry.Action,-22} {entry.Target,-16} {Cut(entry.Detail ?? "", 50)}  {entry.Address}");
        }

        // ------------------------------------------------------------------ terminal helpers

        private static void Show(DeployAppStatus status)
        {
            Write($"✅ {status.App.Name}: {DeployText.StateLabel(status.State)} · {status.StateDetail}", StateColor(status.State));
        }

        private static string Sparkline(IEnumerable<double> values)
        {
            var list = values.ToList();
            var max = list.DefaultIfEmpty().Max();
            return new string(list.Select(v => max <= 0 ? '·' : v <= 0 ? ' ' : Blocks[(int)Math.Round((Blocks.Length - 1) * v / max)]).ToArray());
        }

        private static void Header(string title, string subtitle)
        {
            System.Console.WriteLine();
            Write($"━━ {title} ━━", ConsoleColor.Cyan);
            Write($"   {subtitle}", ConsoleColor.DarkGray);
            System.Console.WriteLine();
        }

        private static void Write(string text, ConsoleColor color, bool newLine = true)
        {
            var previous = System.Console.ForegroundColor;
            System.Console.ForegroundColor = color;
            if (newLine) System.Console.WriteLine(text); else System.Console.Write(text);
            System.Console.ForegroundColor = previous;
        }

        private static string Prompt(string label)
        {
            System.Console.Write(label);
            return System.Console.ReadLine()?.Trim() ?? throw new EndOfStreamException();
        }

        private static string Ask(string label, string current)
        {
            var answer = Prompt($"  {label}{(string.IsNullOrEmpty(current) ? "" : $" [{current}]")}: ");
            return string.IsNullOrEmpty(answer) ? current : answer;
        }

        private static string ReadSecret(string label)
        {
            if (System.Console.IsInputRedirected) return Prompt(label);

            System.Console.Write(label);
            var value = "";
            while (true)
            {
                var key = System.Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value = value[..^1]; }
                else if (!char.IsControl(key.KeyChar)) value += key.KeyChar;
            }
            System.Console.WriteLine();
            return value;
        }

        private static void Pause()
        {
            if (System.Console.IsInputRedirected) return;
            System.Console.Write("\nPress any key to continue...");
            System.Console.ReadKey(true);
        }

        private static string Cut(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

        private static string Dot(string state) => state switch { "running" => "●", "stopped" or "not-deployed" => "○", _ => "◐" };

        private static ConsoleColor StateColor(string state) => state switch
        {
            "running" => ConsoleColor.Green,
            "starting" => ConsoleColor.Cyan,
            "unhealthy" => ConsoleColor.Yellow,
            "crashed" => ConsoleColor.Red,
            _ => ConsoleColor.Gray
        };

        private static string StageMark(string status) => status switch
        {
            DeployStage.Succeeded => "●",
            DeployStage.Warning => "▲",
            DeployStage.Failed => "✖",
            DeployStage.Running => "◐",
            DeployStage.Skipped => "·",
            _ => "○"
        };

        private static ConsoleColor StageColor(string status) => status switch
        {
            DeployStage.Succeeded => ConsoleColor.Green,
            DeployStage.Warning => ConsoleColor.Yellow,
            DeployStage.Failed => ConsoleColor.Red,
            DeployStage.Running => ConsoleColor.Cyan,
            _ => ConsoleColor.DarkGray
        };

        private static ConsoleColor RunColor(string status) => status switch
        {
            DeployRun.Succeeded => ConsoleColor.Green,
            DeployRun.Failed => ConsoleColor.Red,
            DeployRun.Running or DeployRun.Queued => ConsoleColor.Cyan,
            _ => ConsoleColor.Gray
        };
    }
}
