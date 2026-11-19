using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using QualityAviationCodeRepositoryConsoleClient.Console.Services;
using QualityAviationRepositoryShared;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Forms
{
    // The Deployments tab: the control panel for everything the deploy agent runs on the server.
    //
    //   left     the applications, with their state and current traffic
    //   top      the selected application and what can be done to it
    //   middle   its pipeline as nodes, live while a deployment runs
    //   bottom   run details, users and traffic, logs, incidents, releases and history
    //
    // It refreshes itself while it is visible, and does the same things as the terminal's Deployments menu.
    public class DeployPanel : UserControl
    {
        private readonly IDeployClient _deploy;
        private readonly IRepoOperations _repos;
        private readonly Action<string> _log;
        private readonly Func<Task<bool>> _reauthenticate;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

        // left
        private readonly ListBox _appList = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, BorderStyle = BorderStyle.None, IntegralHeight = false };
        private readonly Label _summary = new() { Dock = DockStyle.Top, AutoSize = false, ForeColor = Palette.Muted, Padding = new Padding(10, 6, 6, 0) };
        private readonly FlowLayoutPanel _adminBar = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6), WrapContents = true };

        // header
        private readonly Label _banner = new() { Dock = DockStyle.Top, AutoSize = false, Visible = false, ForeColor = Color.White, BackColor = Palette.Amber, Padding = new Padding(10, 6, 10, 6), TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label _title = new() { AutoSize = true, ForeColor = Palette.Text };
        private readonly Label _subtitle = new() { AutoSize = true, ForeColor = Palette.Muted };
        private readonly Label _state = new() { AutoSize = true };
        private readonly Label _live = new() { AutoSize = true, ForeColor = Palette.Text };
        private readonly FlowLayoutPanel _actions = new() { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        private readonly Button _deployButton = Action("Deploy ▾", primary: true);
        private readonly Button _rollbackButton = Action("Roll back");
        private readonly Button _restartButton = Action("Restart");
        private readonly Button _stopStartButton = Action("Stop");
        private readonly Button _settingsButton = Action("Settings");
        private readonly Button _environmentButton = Action("Environment");
        private readonly Button _removeButton = Action("Remove");
        private readonly ContextMenuStrip _deployMenu = new();

        // pipeline
        private readonly Label _runTitle = new() { Dock = DockStyle.Fill, AutoSize = false, ForeColor = Palette.Text, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button _cancelButton = Action("Cancel run");
        private readonly PipelineGraph _graph = new() { Dock = DockStyle.Fill };

        // tabs
        private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
        private readonly ListView _findings = NewList(("Kind", 80), ("Severity", 80), ("Message", 460), ("Where", 230));
        private readonly TextBox _runLog = LogBox();
        private readonly MetricsChart _chart = new() { Dock = DockStyle.Fill };
        private readonly FlowLayoutPanel _tiles = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        private readonly ComboBox _period = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly TextBox _logBox = LogBox();
        private readonly RadioButton _appLogChoice = new() { Text = "Application log", AutoSize = true, Checked = true };
        private readonly RadioButton _accessLogChoice = new() { Text = "Access log", AutoSize = true };
        private readonly CheckBox _followLog = new() { Text = "Follow", AutoSize = true, Checked = true };
        private readonly ListView _incidentList = NewList(("Opened", 110), ("Status", 100), ("Severity", 80), ("Application", 110), ("What happened", 420));
        private readonly TextBox _incidentDetail = LogBox();
        private readonly CheckBox _allApps = new() { Text = "All applications", AutoSize = true };
        private readonly ListView _releaseList = NewList(("Release", 140), ("Version", 140), ("Built", 120), ("For", 90), ("Files", 60), ("Size", 80), ("", 60));
        private readonly ListView _historyList = NewList(("Started", 110), ("Kind", 70), ("Version", 140), ("By", 90), ("Result", 90), ("Took", 80), ("Problem", 360));

        private DeployOverview _overview;
        private AppDetails _details;
        private string _selected;
        private string _shownRunId;
        private long _runLogOffset;
        private bool _runFinishedShown;
        private long _logNext;
        private string _logKind = "app";
        private bool _busy;
        private bool _connected;
        private int _tick;

        public DeployPanel(IDeployClient deploy, IRepoOperations repos, Action<string> log, Func<Task<bool>> reauthenticate)
        {
            _deploy = deploy;
            _repos = repos;
            _log = log;
            _reauthenticate = reauthenticate;

            Dock = DockStyle.Fill;
            BackColor = Palette.Background;
            Build();

            _timer.Tick += async (_, _) => await TickAsync();
            VisibleChanged += async (_, _) =>
            {
                _timer.Enabled = Visible;
                if (Visible) await RefreshAllAsync();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ layout

        private void Build()
        {
            // Left: applications
            var left = new Panel { Dock = DockStyle.Left, Width = LogicalToDeviceUnits(280), BackColor = Palette.Card, Padding = new Padding(0, 0, 1, 0) };
            var leftTitle = new Label { Dock = DockStyle.Top, Text = "Applications", Font = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold), ForeColor = Palette.Text, Height = LogicalToDeviceUnits(30), Padding = new Padding(10, 8, 0, 0) };
            _summary.Height = LogicalToDeviceUnits(40);
            _appList.ItemHeight = LogicalToDeviceUnits(64);
            _appList.DrawItem += DrawApp;
            _appList.SelectedIndexChanged += async (_, _) => await SelectAppAsync();

            var newApp = Action("+ New application");
            newApp.Click += async (_, _) => await Guard("Saving the application", () => EditAppAsync(null));
            var storage = Action("Storage…");
            storage.Click += async (_, _) => await Guard("Optimizing storage", OptimizeStorageAsync);
            var audit = Action("Audit trail…");
            audit.Click += async (_, _) => await Guard("Loading the audit trail", ShowAuditAsync);
            _adminBar.Controls.AddRange(new Control[] { newApp, storage, audit });

            left.Controls.Add(_appList);
            left.Controls.Add(_adminBar);
            left.Controls.Add(_summary);
            left.Controls.Add(leftTitle);

            // Right: header with actions
            var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(96), ColumnCount = 2, RowCount = 3, BackColor = Palette.Card, Padding = new Padding(14, 10, 10, 6) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _title.Font = new Font(Font.FontFamily, Font.Size * 1.5f, FontStyle.Bold);
            _state.Font = new Font(Font, FontStyle.Bold);

            var titleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            titleRow.Controls.Add(_title);
            titleRow.Controls.Add(new Label { Width = LogicalToDeviceUnits(10) });
            _state.Margin = new Padding(0, LogicalToDeviceUnits(9), 0, 0);
            titleRow.Controls.Add(_state);

            header.Controls.Add(titleRow, 0, 0);
            header.Controls.Add(_subtitle, 0, 1);
            header.Controls.Add(_live, 0, 2);
            header.Controls.Add(_actions, 1, 0);
            header.SetRowSpan(_actions, 3);

            _actions.Controls.AddRange(new Control[] { _deployButton, _rollbackButton, _restartButton, _stopStartButton, _settingsButton, _environmentButton, _removeButton });
            _deployButton.Click += (_, _) => _deployMenu.Show(_deployButton, new Point(0, _deployButton.Height));
            _rollbackButton.Click += async (_, _) => await Guard("Rolling back", () => RollbackAsync(null));
            _restartButton.Click += async (_, _) => await Guard("Restarting", () => ControlAsync("restart"));
            _stopStartButton.Click += async (_, _) => await Guard(_stopStartButton.Text, () => ControlAsync(_stopStartButton.Text == "Start" ? "start" : "stop"));
            _settingsButton.Click += async (_, _) => await Guard("Saving the settings", () => EditAppAsync(_details?.Status.App));
            _environmentButton.Click += async (_, _) => await Guard("Saving the environment", EnvironmentAsync);
            _removeButton.Click += async (_, _) => await Guard("Removing the application", RemoveAsync);

            // Pipeline
            var pipelineCard = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(190), BackColor = Palette.Card, Padding = new Padding(10, 4, 10, 6) };
            var runBar = new TableLayoutPanel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(32), ColumnCount = 2 };
            runBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            runBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            runBar.Controls.Add(_runTitle, 0, 0);
            runBar.Controls.Add(_cancelButton, 1, 0);
            _cancelButton.Click += async (_, _) => await Guard("Cancelling", async () => { await _deploy.CancelRunAsync(_shownRunId); _log("Cancelling the run"); });
            _graph.StageClicked += stage =>
            {
                _tabs.SelectedIndex = 0;
                if (!string.IsNullOrEmpty(stage.Summary)) _log($"{stage.Title}: {stage.Summary}");
            };
            pipelineCard.Controls.Add(_graph);
            pipelineCard.Controls.Add(runBar);

            // Tabs
            _tabs.TabPages.Add(Page("Run details", Split(_findings, _runLog, 0.45)));
            _tabs.TabPages.Add(Page("Users && traffic", TrafficPage()));
            _tabs.TabPages.Add(Page("Logs", LogsPage()));
            _tabs.TabPages.Add(Page("Incidents", IncidentsPage()));
            _tabs.TabPages.Add(Page("Releases", ReleasesPage()));
            _tabs.TabPages.Add(Page("History", _historyList));
            _tabs.SelectedIndexChanged += async (_, _) => await RefreshTabAsync(force: true);
            _historyList.SelectedIndexChanged += (_, _) =>
            {
                if (_historyList.SelectedItems.Count > 0) ShowRun(((DeployRun)_historyList.SelectedItems[0].Tag).Id);
            };

            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 10, 10) };
            var gap1 = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(8) };
            var gap2 = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(8) };
            right.Controls.Add(_tabs);
            right.Controls.Add(gap2);
            right.Controls.Add(pipelineCard);
            right.Controls.Add(gap1);
            right.Controls.Add(header);

            Controls.Add(right);
            Controls.Add(left);
            Controls.Add(_banner);

            ShowNothingSelected();
        }

        private Control TrafficPage()
        {
            _period.Items.AddRange(new object[] { "Last hour", "Last 6 hours", "Last 24 hours" });
            _period.SelectedIndex = 0;
            _period.SelectedIndexChanged += async (_, _) => await RefreshTabAsync(force: true);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 4, 4, 0) };
            bar.Controls.Add(new Label { Text = "Period:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            bar.Controls.Add(_period);

            var page = new Panel { Dock = DockStyle.Fill };
            page.Controls.Add(_chart);
            page.Controls.Add(_tiles);
            page.Controls.Add(bar);
            return page;
        }

        private Control LogsPage()
        {
            _appLogChoice.CheckedChanged += async (_, _) => { if (_appLogChoice.Checked) await SwitchLogAsync("app"); };
            _accessLogChoice.CheckedChanged += async (_, _) => { if (_accessLogChoice.Checked) await SwitchLogAsync("access"); };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
            bar.Controls.AddRange(new Control[] { _appLogChoice, _accessLogChoice, _followLog });

            var page = new Panel { Dock = DockStyle.Fill };
            page.Controls.Add(_logBox);
            page.Controls.Add(bar);
            return page;
        }

        private Control IncidentsPage()
        {
            var acknowledge = Action("Acknowledge");
            var resolve = Action("Resolve…");
            var note = Action("Add note…");
            acknowledge.Click += async (_, _) => await Guard("Acknowledging", () => IncidentActionAsync("acknowledge", null));
            resolve.Click += async (_, _) => await Guard("Resolving", () => IncidentActionAsync("resolve", "What fixed it (optional):"));
            note.Click += async (_, _) => await Guard("Adding the note", () => IncidentActionAsync("note", "Note:"));
            _allApps.CheckedChanged += async (_, _) => await RefreshTabAsync(force: true);
            _incidentList.SelectedIndexChanged += (_, _) => ShowIncident();

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
            bar.Controls.AddRange(new Control[] { acknowledge, resolve, note, _allApps });

            var page = new Panel { Dock = DockStyle.Fill };
            page.Controls.Add(Split(_incidentList, _incidentDetail, 0.55));
            page.Controls.Add(bar);
            return page;
        }

        private Control ReleasesPage()
        {
            var rollback = Action("Roll back to the selected release");
            rollback.Click += async (_, _) => await Guard("Rolling back", () =>
            {
                if (_releaseList.SelectedItems.Count == 0) throw new ApiException("Select a release first.");
                return RollbackAsync(((ReleaseInfo)_releaseList.SelectedItems[0].Tag).Id);
            });

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
            bar.Controls.Add(rollback);
            bar.Controls.Add(new Label { AutoSize = true, ForeColor = Palette.Muted, Margin = new Padding(8, 8, 3, 3), Text = "Releases are sealed builds kept on the server: going back to one does not build anything again." });

            var page = new Panel { Dock = DockStyle.Fill };
            page.Controls.Add(_releaseList);
            page.Controls.Add(bar);
            return page;
        }

        // ------------------------------------------------------------------ refreshing

        private async Task TickAsync()
        {
            if (_busy || !Visible) return;
            _busy = true;
            try
            {
                _tick++;
                if (_shownRunId != null && !_runFinishedShown) await RefreshRunAsync();
                if (_tick % 3 == 0) await RefreshOverviewAsync();
                if (_tick % 3 == 1 && _selected != null) await RefreshDetailsAsync();
                if (_selected != null) await RefreshTabAsync(force: false);
            }
            catch (ApiException ex) when (ex.IsUnauthorized)
            {
                await SessionExpiredAsync();
            }
            catch (ApiException ex)
            {
                ShowBanner(ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        public async Task RefreshAllAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                await RefreshOverviewAsync();
                if (_selected != null) await RefreshDetailsAsync();
                await RefreshTabAsync(force: true);
            }
            catch (ApiException ex) when (ex.IsUnauthorized)
            {
                await SessionExpiredAsync();
            }
            catch (ApiException ex)
            {
                ShowBanner(ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task RefreshOverviewAsync()
        {
            _overview = await _deploy.OverviewAsync();
            if (!_connected) _log("Connected to the deploy agent");
            _connected = true;

            _banner.Visible = _overview.Warnings.Count > 0;
            _banner.Text = string.Join("   ", _overview.Warnings.Select(w => "⚠ " + w));

            _summary.Text = $"{_overview.User}{(_overview.IsAdmin ? " · administrator" : "")}{Environment.NewLine}" +
                            $"{_overview.Apps.Count} application(s) · {_overview.OpenIncidents} open incident(s)";
            _adminBar.Visible = _overview.IsAdmin;

            // Keep the selection while the list is rebuilt
            _appList.BeginUpdate();
            var keep = _selected;
            _appList.Items.Clear();
            foreach (var app in _overview.Apps) _appList.Items.Add(app);
            var index = _overview.Apps.FindIndex(a => a.App.Name == keep);
            if (index >= 0) _appList.SelectedIndex = index;
            else if (_appList.Items.Count > 0 && keep == null) _appList.SelectedIndex = 0;
            _appList.EndUpdate();
        }

        private async Task SelectAppAsync()
        {
            if (_appList.SelectedItem is not DeployAppStatus status || status.App.Name == _selected) return;

            _selected = status.App.Name;
            _shownRunId = null;
            _logNext = 0;
            _logBox.Clear();
            await Guard("Loading the application", async () =>
            {
                await RefreshDetailsAsync();
                await RefreshTabAsync(force: true);
            });
        }

        private async Task RefreshDetailsAsync()
        {
            _details = await _deploy.AppAsync(_selected);
            var status = _details.Status;
            var app = status.App;
            var admin = _overview?.IsAdmin == true;

            _title.Text = app.Name;
            _state.Text = $"● {DeployText.StateLabel(status.State)}";
            _state.ForeColor = Palette.ForState(status.State);
            _subtitle.Text = $"{app.Owner}/{app.Repo}" +
                             (app.PublicPort > 0 ? $" · {(app.Https ? "https" : "http")}://<server>:{app.PublicPort}{app.HealthPath}" : " · no web address (worker)") +
                             $" · {status.StateDetail}";
            _live.Text = status.LiveVersion == null
                ? "Nothing is live yet."
                : $"Live: {status.LiveVersion}, deployed by {status.DeployedBy} {DeployText.Ago(status.LiveSince)}" +
                  (status.LatestVersion != null && status.LatestVersion != status.LiveVersion ? $" · newer in the repository: {status.LatestVersion}" : "") +
                  (status.Restarts > 0 ? $" · {status.Restarts} automatic restart(s)" : "");

            foreach (var button in new[] { _deployButton, _rollbackButton, _restartButton, _stopStartButton }) button.Visible = status.CanOperate;
            foreach (var button in new[] { _settingsButton, _environmentButton, _removeButton }) button.Visible = admin;
            _stopStartButton.Text = status.State == "stopped" ? "Start" : "Stop";
            _rollbackButton.Enabled = _details.Releases.Any(r => !r.IsLive);
            _restartButton.Enabled = status.LiveReleaseId != null;
            _stopStartButton.Enabled = status.LiveReleaseId != null;

            _deployMenu.Items.Clear();
            _deployMenu.Items.Add($"Latest version{(status.LatestVersion != null ? $" ({status.LatestVersion})" : "")}", null, async (_, _) => await Guard("Deploying", () => StartDeployAsync(null)));
            _deployMenu.Items.Add("Choose a version…", null, async (_, _) => await Guard("Deploying", ChooseVersionAndDeployAsync));

            // Follow the active run; otherwise show the last one, unless the user picked one in the history
            var runToShow = status.ActiveRunId ?? (_shownRunId != null && _details.Runs.Any(r => r.Id == _shownRunId) ? _shownRunId : _details.Runs.FirstOrDefault()?.Id);
            if (runToShow != _shownRunId) ShowRun(runToShow);

            Fill(_releaseList, _details.Releases, release => new[]
            {
                release.Id, release.Version, release.CreatedAt.ToString("yyyy-MM-dd HH:mm"), release.DeployedBy, release.FileCount.ToString(),
                RepoOperations.FormatSize(release.SizeBytes), release.IsLive ? "LIVE" : ""
            }, release => release.IsLive ? Palette.Green : Palette.Text);

            Fill(_historyList, _details.Runs, run => new[]
            {
                run.CreatedAt.ToString("MM-dd HH:mm:ss"), run.Kind, run.Version ?? "", run.RequestedBy, run.Status,
                DeployText.Duration(run.StartedAt, run.FinishedAt), run.Error?.Split('\n')[0] ?? ""
            }, run => Palette.ForRun(run.Status));

            _appList.Invalidate();
        }

        // Shows a run in the pipeline graph and the Run details tab, and follows it while it is active
        private void ShowRun(string runId)
        {
            _shownRunId = runId;
            _runLogOffset = 0;
            _runFinishedShown = false;
            _runLog.Clear();
            _findings.Items.Clear();
            _graph.Run = null;
            _runTitle.Text = runId == null ? "No deployment has run for this application yet." : "Loading…";
            _cancelButton.Visible = false;
        }

        private async Task RefreshRunAsync()
        {
            var progress = await _deploy.RunAsync(_shownRunId, _runLogOffset);
            var run = progress.Run;
            _runLogOffset = progress.NextOffset;

            if (progress.Log.Length > 0)
            {
                _runLog.AppendText(progress.Log.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
                _runLog.SelectionStart = _runLog.TextLength;
                _runLog.ScrollToCaret();
            }

            _graph.Run = run;
            _cancelButton.Visible = run.IsActive && _details?.Status.CanOperate == true;
            _runTitle.ForeColor = Palette.ForRun(run.Status);
            _runTitle.Text = $"{(run.Kind == "rollback" ? "Rollback" : "Deployment")} of {run.Version ?? "the latest version"} · requested by {run.RequestedBy} {DeployText.Ago(run.CreatedAt)} · " +
                             (run.IsActive ? $"{run.Status} {DeployText.Duration(run.StartedAt ?? run.CreatedAt, null)}" : $"{run.Status} in {DeployText.Duration(run.StartedAt, run.FinishedAt)}") +
                             (run.Error != null ? $" · {run.Error.Split('\n')[0]}" : "");

            FillFindings(run);

            // Finished, and the whole log is in
            if (!run.IsActive && progress.Log.Length == 0)
            {
                _runFinishedShown = true;
                if (run.Status == DeployRun.Succeeded) _log($"{run.App}: {run.Version} is live (release {run.ReleaseId})");
                else if (run.Status == DeployRun.Failed) _log($"{run.App}: {run.Kind} failed: {run.Error?.Split('\n')[0]}");
            }
        }

        private void FillFindings(DeployRun run)
        {
            var rows = new List<(string Kind, string Severity, string Message, string Where, Color Color)>();
            rows.AddRange(run.Diagnostics.Where(d => d.Severity == "error").Select(d => ("error", "error", $"{d.Code} {d.Message}", DeployText.Where(d), Palette.Red)));
            rows.AddRange(run.Tests?.Failures.Select(f => ("test", "failed", $"{f.Name}: {f.Message?.Replace("\r", "").Replace("\n", " ")}", "", Palette.Red)) ?? Enumerable.Empty<(string, string, string, string, Color)>());
            rows.AddRange(run.Findings.Select(f => (f.Kind, f.Severity, f.Title, DeployText.Where(f), f.Severity is "critical" or "high" ? Palette.Red : Palette.Amber)));
            rows.AddRange(run.Diagnostics.Where(d => d.Severity == "warning").Select(d => ("warning", "warning", $"{d.Code} {d.Message}", DeployText.Where(d), Palette.Amber)));
            if (run.Tests != null && run.Tests.Total > 0)
                rows.Add(("tests", run.Tests.Failed == 0 ? "passed" : "failed", $"{run.Tests.Passed} of {run.Tests.Total} tests passed", "", run.Tests.Failed == 0 ? Palette.Green : Palette.Red));
            if (run.SourceChanges != null)
                rows.AddRange(run.SourceChanges.Files.Select(c => ("change", c.Kind, $"{c.Path} (compared with {run.SourceChanges.ComparedTo})", "", Palette.Muted)));

            if (_findings.Items.Count == rows.Count) return;

            _findings.BeginUpdate();
            _findings.Items.Clear();
            foreach (var row in rows)
                _findings.Items.Add(new ListViewItem(new[] { row.Kind, row.Severity, row.Message, row.Where }) { ForeColor = row.Color });
            if (rows.Count == 0)
                _findings.Items.Add(new ListViewItem(new[] { "", "", run.IsActive ? "Nothing to report yet." : "No errors, failed tests or findings.", "" }) { ForeColor = Palette.Muted });
            _findings.EndUpdate();
        }

        // Only the visible tab is kept up to date
        private async Task RefreshTabAsync(bool force)
        {
            if (_selected == null) return;

            switch (_tabs.SelectedIndex)
            {
                case 1 when force || _tick % 10 == 0:
                    var minutes = _period.SelectedIndex switch { 1 => 360, 2 => 1440, _ => 60 };
                    var metrics = await _deploy.MetricsAsync(_selected, minutes);
                    _chart.Points = metrics.Points;
                    ShowTiles(metrics.Summary);
                    break;

                case 2 when _details?.Status.CanOperate == true && (force || (_followLog.Checked && _tick % 2 == 0)):
                    var chunk = await _deploy.LogsAsync(_selected, _logKind, _logNext, _logNext == 0 ? 300 : 1000);
                    _logNext = chunk.Next;
                    if (chunk.Lines.Count > 0)
                    {
                        _logBox.AppendText(string.Join(Environment.NewLine, chunk.Lines) + Environment.NewLine);
                        if (_followLog.Checked) { _logBox.SelectionStart = _logBox.TextLength; _logBox.ScrollToCaret(); }
                    }
                    break;

                case 2 when _details?.Status.CanOperate != true && force:
                    _logBox.Text = "You are not one of the people who may operate this application, so its logs are not shown.";
                    break;

                case 3 when force || _tick % 5 == 0:
                    var incidents = (await _deploy.IncidentsAsync(false)).Where(i => _allApps.Checked || i.App == _selected).ToList();
                    var keep = _incidentList.SelectedItems.Count > 0 ? ((Incident)_incidentList.SelectedItems[0].Tag).Id : null;
                    Fill(_incidentList, incidents, incident => new[]
                    {
                        incident.OpenedAt.ToString("MM-dd HH:mm:ss"), incident.Status, incident.Severity, incident.App, incident.Title
                    }, incident => incident.Status == Incident.Resolved ? Palette.Muted : incident.Severity == "critical" ? Palette.Red : Palette.Amber);
                    foreach (ListViewItem item in _incidentList.Items)
                        if (((Incident)item.Tag).Id == keep) item.Selected = true;
                    ShowIncident();
                    break;
            }
        }

        private void ShowTiles(MetricsSummary s)
        {
            _tiles.SuspendLayout();
            _tiles.Controls.Clear();
            foreach (var (label, value, color) in new[]
            {
                ("Active users (5 min)", s.ActiveUsers.ToString(), Palette.Blue),
                ("Users today", s.UsersToday.ToString(), Palette.Text),
                ("Requests / min", s.RequestsPerMinute.ToString(), Palette.Text),
                ("Requests today", s.RequestsToday.ToString(), Palette.Text),
                ("Server errors", s.ErrorRate.ToString("P1"), s.ErrorRate > 0.05 ? Palette.Red : Palette.Green),
                ("Response p95", $"{s.P95Ms:0} ms", s.P95Ms > 1000 ? Palette.Amber : Palette.Text),
                ("In progress", s.ActiveRequests.ToString(), Palette.Text)
            })
            {
                var tile = new Panel { Width = LogicalToDeviceUnits(130), Height = LogicalToDeviceUnits(54), BackColor = Palette.Background, Margin = new Padding(4) };
                tile.Controls.Add(new Label { Text = value, ForeColor = color, Font = new Font(Font.FontFamily, Font.Size * 1.5f, FontStyle.Bold), AutoSize = true, Location = new Point(LogicalToDeviceUnits(8), LogicalToDeviceUnits(4)) });
                tile.Controls.Add(new Label { Text = label, ForeColor = Palette.Muted, AutoSize = true, Location = new Point(LogicalToDeviceUnits(8), LogicalToDeviceUnits(32)) });
                _tiles.Controls.Add(tile);
            }
            _tiles.ResumeLayout();
        }

        private void ShowIncident()
        {
            if (_incidentList.SelectedItems.Count == 0)
            {
                _incidentDetail.Text = _incidentList.Items.Count == 0 ? "No incidents." : "Select an incident to see what happened and what was done.";
                return;
            }

            var incident = (Incident)_incidentList.SelectedItems[0].Tag;
            var lines = new List<string>
            {
                incident.Title,
                $"{incident.App} · {incident.Type} · {incident.Severity} · {incident.Status}" + (incident.AcknowledgedBy != null ? $" · acknowledged by {incident.AcknowledgedBy}" : ""),
                ""
            };
            if (!string.IsNullOrEmpty(incident.Details)) { lines.Add(incident.Details); lines.Add(""); }
            lines.Add("Timeline:");
            lines.AddRange(incident.Timeline.Select(e => $"  {e.At:yyyy-MM-dd HH:mm:ss}  {e.By,-10} {e.Text}"));
            _incidentDetail.Text = string.Join(Environment.NewLine, lines).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }

        private async Task SwitchLogAsync(string kind)
        {
            _logKind = kind;
            _logNext = 0;
            _logBox.Clear();
            await Guard("Loading the log", () => RefreshTabAsync(force: true));
        }

        // ------------------------------------------------------------------ actions

        private async Task StartDeployAsync(string version)
        {
            var run = await _deploy.DeployAsync(_selected, version);
            _log($"{_selected}: deployment of {version ?? "the latest version"} started");
            _tabs.SelectedIndex = 0;
            ShowRun(run.Id);
            await RefreshRunAsync();
        }

        private async Task ChooseVersionAndDeployAsync()
        {
            var app = _details.Status.App;
            var versions = await _repos.GetVersionsAsync(app.Owner, app.Repo);
            if (versions.Count == 0) throw new ApiException("The repository has no versions yet.");

            var choices = versions.Take(30).Select(v => $"{v.Name}  ·  {v.CreatedAt:yyyy-MM-dd HH:mm}  ·  {v.CreatedBy}  ·  {v.Message}").ToArray();
            var input = PromptDialog.Show(this, $"Deploy a version of {app.Name}", new PromptField { Label = "Version:", Choices = choices });
            if (input == null) return;

            await StartDeployAsync(input[0].Split(' ')[0]);
        }

        private async Task RollbackAsync(string releaseId)
        {
            var candidates = _details.Releases.Where(r => !r.IsLive).ToList();
            if (candidates.Count == 0) throw new ApiException("There is no earlier release on the server to go back to.");

            if (releaseId == null)
            {
                var input = PromptDialog.Show(this, $"Roll back {_selected}", new PromptField
                {
                    Label = "Go back to:",
                    Choices = candidates.Select(r => $"{r.Id}  ·  version {r.Version}  ·  built {r.CreatedAt:yyyy-MM-dd HH:mm} for {r.DeployedBy}").ToArray()
                });
                if (input == null) return;
                releaseId = input[0].Split(' ')[0];
            }
            else if (MessageBox.Show(this, $"Make release {releaseId} live again?", "Roll back", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                return;
            }

            var run = await _deploy.RollbackAsync(_selected, releaseId);
            _log($"{_selected}: rollback to {releaseId} started");
            _tabs.SelectedIndex = 0;
            ShowRun(run.Id);
            await RefreshRunAsync();
        }

        private async Task ControlAsync(string action)
        {
            if (action == "stop" && MessageBox.Show(this, $"Stop {_selected}? Users will see a 'temporarily unavailable' page until it is started again.", "Stop",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            var status = await _deploy.ControlAsync(_selected, action);
            _log($"{_selected}: {DeployText.StateLabel(status.State)} · {status.StateDetail}");
            await RefreshDetailsAsync();
        }

        private async Task IncidentActionAsync(string action, string question)
        {
            if (_incidentList.SelectedItems.Count == 0) throw new ApiException("Select an incident first.");
            var incident = (Incident)_incidentList.SelectedItems[0].Tag;

            string text = null;
            if (question != null)
            {
                var input = PromptDialog.Show(this, incident.Title, new PromptField { Label = question, Optional = action == "resolve" });
                if (input == null) return;
                text = input[0];
            }

            await _deploy.IncidentActionAsync(incident.Id, action, text);
            _log($"Incident on {incident.App}: {action}");
            await RefreshTabAsync(force: true);
        }

        private async Task EditAppAsync(DeployApp existing)
        {
            var app = existing ?? new DeployApp();
            var repositories = await _repos.ListAsync();
            var names = repositories.OwnedRepos.Concat(repositories.ClonedRepos).Select(r => $"{r.Owner}/{r.Name}").ToList();
            if (existing != null) names.Insert(0, $"{app.Owner}/{app.Repo}");
            if (names.Count == 0) throw new ApiException("There is no repository yet. Push the application's code first.");

            static string[] First(string current, params string[] all) => all.OrderBy(a => a == current ? 0 : 1).ToArray();

            var fields = new List<PromptField>();
            if (existing == null) fields.Add(new PromptField { Label = "Name:" });
            fields.AddRange(new[]
            {
                new PromptField { Label = "Repository:", Choices = names.Distinct().ToArray() },
                new PromptField { Label = "Kind:", Choices = First(app.Kind, "dotnet", "custom") },
                new PromptField { Label = "Project to publish:", Value = app.Project ?? "", Optional = true },
                new PromptField { Label = "Self-contained:", Choices = First(app.SelfContained ? "yes" : "no", "no", "yes") },
                new PromptField { Label = "Public port (0 = none):", Value = app.PublicPort.ToString() },
                new PromptField { Label = "HTTPS:", Choices = First(app.Https ? "yes" : "no", "no", "yes") },
                new PromptField { Label = "Health check path:", Value = app.HealthPath ?? "/" },
                new PromptField { Label = "Run tests:", Choices = First(app.RunTests ? "yes" : "no", "yes", "no") },
                new PromptField { Label = "Security findings:", Choices = First(app.SecurityGate, "report", "block") },
                new PromptField { Label = "Deployers (logins):", Value = string.Join(", ", app.Deployers), Optional = true },
                new PromptField { Label = "Build command (custom):", Value = app.BuildCommand ?? "", Optional = true },
                new PromptField { Label = "Test command (custom):", Value = app.TestCommand ?? "", Optional = true },
                new PromptField { Label = "Publish command (custom):", Value = app.PublishCommand ?? "", Optional = true },
                new PromptField { Label = "Start command (custom):", Value = app.StartCommand ?? "", Optional = true }
            });

            var input = PromptDialog.Show(this, existing == null ? "New application" : $"Settings of {app.Name}", fields.ToArray());
            if (input == null) return;

            var i = 0;
            if (existing == null) app.Name = input[i++].Trim();
            var repository = input[i++].Split('/');
            app.Owner = repository[0];
            app.Repo = repository[1];
            app.Kind = input[i++];
            app.Project = input[i++].Trim();
            app.SelfContained = input[i++] == "yes";
            app.PublicPort = int.TryParse(input[i++], out var port) ? port : throw new ApiException("The public port must be a number.");
            app.Https = input[i++] == "yes";
            app.HealthPath = input[i++].Trim();
            app.RunTests = input[i++] == "yes";
            app.SecurityGate = input[i++];
            app.Deployers = input[i++].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            app.BuildCommand = input[i++].Trim();
            app.TestCommand = input[i++].Trim();
            app.PublishCommand = input[i++].Trim();
            app.StartCommand = input[i].Trim();

            var saved = await _deploy.SaveAppAsync(app);
            _log($"Saved application {saved.Name}");
            _selected = saved.Name;
            await RefreshOverviewAsync();
            await RefreshDetailsAsync();
        }

        private async Task EnvironmentAsync()
        {
            var keys = _details.EnvironmentKeys;
            var input = PromptDialog.Show(this, $"Environment of {_selected}",
                new PromptField { Label = "Name (e.g. ConnectionStrings__Default):", Choices = null, Value = "" },
                new PromptField { Label = "Value (empty removes it):", IsPassword = true, Optional = true });
            if (input == null) return;

            var saved = await _deploy.SetEnvironmentAsync(_selected, input[0].Trim(), input[1]);
            _log($"{_selected}: environment saved ({string.Join(", ", saved)}). It takes effect at the next restart or deployment.");
            MessageBox.Show(this, $"Saved, encrypted on the server. Values are never shown again.{Environment.NewLine}{Environment.NewLine}Set now: {(saved.Count == 0 ? "(none)" : string.Join(", ", saved))}" +
                                  $"{Environment.NewLine}{Environment.NewLine}Restart or deploy the application for it to take effect.",
                "Environment", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await RefreshDetailsAsync();
        }

        private async Task RemoveAsync()
        {
            if (MessageBox.Show(this, $"Stop {_selected} and remove it from the deploy agent?{Environment.NewLine}{Environment.NewLine}Its releases and logs are set aside on the server, not deleted. The repository is not touched.",
                    "Remove application", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            _log(await _deploy.RemoveAppAsync(_selected));
            _selected = null;
            ShowNothingSelected();
            await RefreshOverviewAsync();
        }

        private async Task OptimizeStorageAsync()
        {
            var report = await _deploy.OptimizeStorageAsync(false);
            var text = $"{report.Versions} versions, {report.Files} files, {RepoOperations.FormatSize(report.TotalBytes)} in the repository store.{Environment.NewLine}" +
                       $"Already shared: {report.LinkedFiles} files ({RepoOperations.FormatSize(report.SavedBytes)}).{Environment.NewLine}" +
                       $"Identical copies that can be shared: {report.DuplicateFiles} files ({RepoOperations.FormatSize(report.DuplicateBytes)}).";

            if (report.DuplicateFiles == 0)
            {
                MessageBox.Show(this, text, "Storage", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(this, text + $"{Environment.NewLine}{Environment.NewLine}Keep one copy of each and link the others to it? Pushes and pulls work exactly as before.",
                    "Storage", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            report = await _deploy.OptimizeStorageAsync(true);
            _log($"Storage: {report.LinkedFiles} files share storage now, {RepoOperations.FormatSize(report.SavedBytes)} saved in total");
        }

        private async Task ShowAuditAsync()
        {
            var entries = await _deploy.AuditAsync(300);
            using var dialog = new Form { Text = "Audit trail", Size = new Size(LogicalToDeviceUnits(980), LogicalToDeviceUnits(560)), StartPosition = FormStartPosition.CenterParent };
            var list = NewList(("When", 130), ("Who", 90), ("Action", 160), ("Target", 140), ("Detail", 300), ("From", 110));
            Fill(list, entries, e => new[] { e.At.ToString("yyyy-MM-dd HH:mm:ss"), e.User, e.Action, e.Target, e.Detail ?? "", e.Address ?? "" }, e => e.Action.EndsWith("failed") ? Palette.Red : Palette.Text);
            dialog.Controls.Add(list);
            dialog.ShowDialog(this);
        }

        // ------------------------------------------------------------------ helpers

        // Runs one action and turns every failure into a message instead of a crash
        private async Task Guard(string what, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (ApiException ex) when (ex.IsUnauthorized)
            {
                await SessionExpiredAsync();
            }
            catch (Exception ex) when (ex is ApiException or IOException)
            {
                _log($"{what} failed: {ex.Message}");
                if (!IsDisposed) MessageBox.Show(this, ex.Message, what, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task SessionExpiredAsync()
        {
            _timer.Stop();
            if (await _reauthenticate()) _timer.Start();
        }

        private void ShowBanner(string message)
        {
            _connected = false;
            _banner.Text = "⚠ " + message;
            _banner.Visible = true;
        }

        private void ShowNothingSelected()
        {
            _title.Text = "Deployments";
            _state.Text = "";
            _subtitle.Text = "Select an application on the left.";
            _live.Text = "";
            foreach (Control button in _actions.Controls) button.Visible = false;
            ShowRun(null);
        }

        private void DrawApp(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _appList.Items.Count) return;

            var status = (DeployAppStatus)_appList.Items[e.Index];
            var g = e.Graphics;
            var bounds = e.Bounds;
            var selected = (e.State & DrawItemState.Selected) != 0;

            using (var background = new SolidBrush(selected ? Palette.Selected : Palette.Card)) g.FillRectangle(background, bounds);
            using (var line = new Pen(Palette.Border)) g.DrawLine(line, bounds.Left + 10, bounds.Bottom - 1, bounds.Right - 10, bounds.Bottom - 1);
            if (selected) using (var accent = new SolidBrush(Palette.Blue)) g.FillRectangle(accent, bounds.Left, bounds.Top, LogicalToDeviceUnits(3), bounds.Height);

            var pad = LogicalToDeviceUnits(12);
            using var bold = new Font(Font, FontStyle.Bold);
            using var small = new Font(Font.FontFamily, Font.Size * 0.9f);
            var row = LogicalToDeviceUnits(18);

            TextRenderer.DrawText(g, status.App.Name, bold, new Point(bounds.Left + pad, bounds.Top + LogicalToDeviceUnits(6)), Palette.Text);

            var state = $"● {DeployText.StateLabel(status.State)}";
            var stateSize = TextRenderer.MeasureText(state, small);
            TextRenderer.DrawText(g, state, small, new Point(bounds.Right - stateSize.Width - pad / 2, bounds.Top + LogicalToDeviceUnits(7)), Palette.ForState(status.State));

            TextRenderer.DrawText(g, status.LiveVersion ?? "not deployed", small, new Point(bounds.Left + pad, bounds.Top + LogicalToDeviceUnits(6) + row), Palette.Muted);

            var traffic = status.App.PublicPort == 0 ? "worker" : status.Now == null ? "" : $"{status.Now.ActiveUsers} user(s) · {status.Now.RequestsPerMinute} req/min";
            if (status.ActiveRunId != null) traffic = "deploying…";
            TextRenderer.DrawText(g, traffic, small, new Point(bounds.Left + pad, bounds.Top + LogicalToDeviceUnits(6) + 2 * row), status.ActiveRunId != null ? Palette.Blue : Palette.Muted);

            if (status.OpenIncidents > 0)
            {
                var badge = $"⚠ {status.OpenIncidents}";
                var badgeSize = TextRenderer.MeasureText(badge, small);
                TextRenderer.DrawText(g, badge, small, new Point(bounds.Right - badgeSize.Width - pad / 2, bounds.Top + LogicalToDeviceUnits(6) + 2 * row), Palette.Red);
            }
        }

        private static void Fill<T>(ListView list, IEnumerable<T> items, Func<T, string[]> columns, Func<T, Color> color)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var item in items) list.Items.Add(new ListViewItem(columns(item)) { Tag = item, ForeColor = color(item) });
            list.EndUpdate();
        }

        private static ListView NewList(params (string Title, int Width)[] columns)
        {
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None };
            foreach (var (title, width) in columns) list.Columns.Add(title, list.LogicalToDeviceUnits(width));
            return list;
        }

        private static TextBox LogBox()
        {
            return new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(30, 34, 40),
                ForeColor = Color.FromArgb(220, 223, 228),
                Font = new Font(FontFamily.GenericMonospace, 9f)
            };
        }

        private static Button Action(string text, bool primary = false)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(3),
                Padding = new Padding(6, 2, 6, 2),
                BackColor = primary ? Palette.Blue : Palette.Card,
                ForeColor = primary ? Color.White : Palette.Text,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = primary ? Palette.Blue : Palette.Border;
            return button;
        }

        private static TabPage Page(string title, Control content)
        {
            var page = new TabPage(title) { BackColor = Palette.Card, Padding = new Padding(4) };
            page.Controls.Add(content);
            return page;
        }

        private static SplitContainer Split(Control top, Control bottom, double share)
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
            split.Panel1.Controls.Add(top);
            split.Panel2.Controls.Add(bottom);
            split.HandleCreated += (_, _) => split.BeginInvoke(() =>
            {
                if (split.Height > 100) split.SplitterDistance = (int)(split.Height * share);
            });
            return split;
        }
    }
}
