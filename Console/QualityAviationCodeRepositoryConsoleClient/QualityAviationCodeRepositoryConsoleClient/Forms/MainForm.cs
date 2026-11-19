using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using QualityAviationCodeRepositoryConsoleClient.Console.Services;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Forms
{
    // The Windows application. It offers the same tasks as the terminal menu and runs them through the same
    // IRepoOperations and IDeployClient, so there is no second copy of the logic here.
    //   Repositories   push, pull, clone, versions; the selected repository's local copy opens in VS Code,
    //                  File Explorer or Visual Studio
    //   Deployments    the control panel of the deploy agent: pipelines, releases, traffic, logs, incidents
    //   Helpdesk       the Q+ Helpdesk's IT side: review and complete tickets, edit the web form, the team's to-do list
    public class MainForm : Form
    {
        private readonly IAuthService _authService;
        private readonly IApiService _apiService;
        private readonly IFileService _fileService;
        private readonly IRepoOperations _repoOperations;
        private readonly IDeployClient _deployClient;
        private readonly IClientUpdater _updater;
        private readonly IHelpdeskClient _helpdesk;
        private readonly TabPage _helpdeskPage = new("Helpdesk");
        private HelpdeskPanel _helpdeskPanel;
        private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
        private readonly TabPage _deployPage = new("Deployments");
        private DeployPanel _deployPanel;

        private readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 4, 6, 4) };
        private readonly ListView _repoList = NewList(("Owner", 110), ("Repository", 190), ("Latest version", 140), ("Files", 55), ("Versions", 75), ("Description", 220));
        private readonly ListView _versionList = NewList(("Version", 140), ("Created", 125), ("By", 100), ("Files", 55), ("Message", 320));

        // The selected repository: where its copy is on this PC, and the buttons that open it
        private readonly ToolStrip _repoBar = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 3, 6, 3) };
        private readonly ToolStripLabel _repoTitle = new("Select a repository") { Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
        private readonly ToolStripLabel _folderLabel = new("") { ForeColor = SystemColors.GrayText };
        private readonly Dictionary<DevTool, ToolStripItem[]> _openItems = new();
        private readonly ToolStripButton _changeFolder = new("Change folder...") { DisplayStyle = ToolStripItemDisplayStyle.Text, Alignment = ToolStripItemAlignment.Right };
        private readonly TextBox _log = new() { Dock = DockStyle.Bottom, Height = 120, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
        private readonly ToolStripStatusLabel _status = new("Starting...") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolStripProgressBar _progress = new() { Width = 220 };

        public MainForm(IAuthService authService, IApiService apiService, IFileService fileService, IRepoOperations repoOperations, IDeployClient deployClient,
            IClientUpdater updater, IHelpdeskClient helpdesk)
        {
            _helpdesk = helpdesk;
            _authService = authService;
            _apiService = apiService;
            _fileService = fileService;
            _repoOperations = repoOperations;
            _deployClient = deployClient;
            _updater = updater;

            Text = "QA Repository Management";
            StartPosition = FormStartPosition.CenterScreen;

            // Sizes in this file are written for 100% display scaling; convert them once for this screen
            ClientSize = LogicalToDeviceUnits(new Size(1280, 820));
            MinimumSize = LogicalToDeviceUnits(new Size(900, 600));
            _log.Height = LogicalToDeviceUnits(_log.Height);
            foreach (var column in _repoList.Columns.Cast<ColumnHeader>().Concat(_versionList.Columns.Cast<ColumnHeader>()))
                column.Width = LogicalToDeviceUnits(column.Width);

            AddButton("Refresh", () => RefreshRepositories());
            _toolbar.Items.Add(new ToolStripSeparator());
            AddButton("Create", CreateRepository);
            AddButton("Push", PushRepository);
            AddButton("Pull latest", () => PullRepository(false));
            AddButton("Clone", CloneRepository);
            AddButton("Switch version", () => PullRepository(true));
            _toolbar.Items.Add(new ToolStripSeparator());
            AddButton("Change password", ChangePassword);
            AddButton("Logout", Logout);

            BuildRepoBar();

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(_repoList);
            split.Panel2.Controls.Add(_versionList);
            split.Panel2.Controls.Add(_repoBar);

            var statusBar = new StatusStrip();
            statusBar.Items.Add(_status);
            statusBar.Items.Add(_progress);

            // Added back to front: the fill panel first, then the edges that dock around it
            var repositoriesPage = new TabPage("Repositories");
            repositoriesPage.Controls.Add(split);
            repositoriesPage.Controls.Add(_toolbar);
            _tabs.TabPages.Add(repositoriesPage);
            _tabs.TabPages.Add(_deployPage);
            _tabs.TabPages.Add(_helpdeskPage);

            Controls.Add(_tabs);
            Controls.Add(_log);
            Controls.Add(statusBar);

            _apiService.StatusChanged += ShowNetworkStatus;
            _repoList.SelectedIndexChanged += async (_, _) =>
            {
                UpdateRepoBar();
                await ShowVersions();
            };
            Shown += async (_, _) =>
            {
                split.SplitterDistance = split.Height / 2;
                await Start();
            };
        }

        private RepoInfo SelectedRepo => _repoList.SelectedItems.Count > 0 ? (RepoInfo)_repoList.SelectedItems[0].Tag : null;

        private async Task Start()
        {
            _toolbar.Enabled = false;

            while (true)
            {
                try
                {
                    _status.Text = "Finding the server ...";
                    await _authService.ConnectAsync();
                    Log($"Secure connection to {_apiService.CurrentServer}");
                    break;
                }
                catch (ApiException ex)
                {
                    if (MessageBox.Show(this, ex.Message, "Cannot connect", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry)
                    {
                        Close();
                        return;
                    }
                }
            }

            if (!_authService.IsAuthenticated && !await Login())
            {
                Close();
                return;
            }

            if (await UpdateClient()) return;

            // Only after a login: it starts talking to the deploy agent as soon as it becomes visible
            _deployPanel = new DeployPanel(_deployClient, _repoOperations, Log, async () =>
            {
                _authService.Logout();
                MessageBox.Show(this, "Your session has expired. Please log in again.", "Deployments", MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (await Login()) return true;
                Close();
                return false;
            });
            _deployPage.Controls.Add(_deployPanel);

            // Follows the helpdesk from the start, so the tab shows new tickets while another tab is open
            _helpdeskPanel = new HelpdeskPanel(_helpdesk, Log, async () =>
            {
                _authService.Logout();
                MessageBox.Show(this, "Your session has expired. Please log in again.", "Helpdesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (await Login()) return true;
                Close();
                return false;
            });
            _helpdeskPanel.TitleChanged += title => _helpdeskPage.Text = title;
            _helpdeskPage.Controls.Add(_helpdeskPanel);
            _ = _helpdeskPanel.StartAsync();

            await RefreshRepositories();
        }

        private async Task<bool> Login()
        {
            while (true)
            {
                var input = PromptDialog.Show(this, "Login",
                    new PromptField { Label = "Username:", Value = _authService.CurrentUsername ?? "" },
                    new PromptField { Label = "Password:", IsPassword = true });

                if (input == null) return false;

                try
                {
                    _status.Text = "Signing in...";
                    await _authService.LoginAsync(input[0].Trim(), input[1]);
                    Log($"Logged in as {_authService.CurrentUsername}");
                    return true;
                }
                catch (ApiException ex)
                {
                    MessageBox.Show(this, ex.Message, "Login failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private async Task RefreshRepositories()
        {
            await Run("Loading repositories", async _ =>
            {
                var repos = await _repoOperations.ListAsync();

                _repoList.BeginUpdate();
                _repoList.Items.Clear();
                foreach (var repo in repos.OwnedRepos.Concat(repos.ClonedRepos))
                {
                    _repoList.Items.Add(new ListViewItem(new[]
                    {
                        repo.Owner, repo.Name, repo.LatestVersion?.Name ?? "(empty)",
                        repo.LatestVersion?.FileCount.ToString() ?? "", repo.VersionCount.ToString(), repo.Description ?? ""
                    })
                    { Tag = repo });
                }
                _repoList.EndUpdate();
                _versionList.Items.Clear();

                var client = _updater.InstalledVersion;
                Text = $"QA Repository Management - {_authService.CurrentUsername} @ {_apiService.CurrentServer}" + (client != null ? $"  ·  client {client}" : "");
            });
            UpdateRepoBar();
        }

        // After every sign-in: a newer client published to the update repository is installed, then the client restarts
        private async Task<bool> UpdateClient()
        {
            _status.Text = "Checking for a newer client...";
            var progress = new Progress<TransferProgress>(p =>
            {
                if (IsDisposed) return;
                _progress.Value = p.Percent;
                _status.Text = $"Downloading the new client... {p.Percent}%";
            });

            var result = await _updater.UpdateAsync(progress);
            if (IsDisposed) return true;
            _progress.Value = 0;
            _status.Text = "Ready";

            if (result == null) return false;
            Log(result.Message);
            if (!result.Installed) return false;

            MessageBox.Show(this, $"{result.Message}.\n\nThe client restarts now and you stay signed in.", "Client updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
            return true;
        }

        // ------------------------------------------------------------------ local copy: VS Code, File Explorer, Visual Studio

        private void BuildRepoBar()
        {
            _repoBar.ImageScalingSize = LogicalToDeviceUnits(new Size(16, 16));
            _repoBar.Items.Add(_repoTitle);
            _repoBar.Items.Add(new ToolStripSeparator());

            var menu = new ContextMenuStrip { ImageScalingSize = _repoBar.ImageScalingSize };
            foreach (var tool in new[] { DevTool.VsCode, DevTool.Explorer, DevTool.VisualStudio })
            {
                var installed = DevTools.IsInstalled(tool);
                var text = installed ? DevTools.Label(tool) : $"{DevTools.Label(tool)} (not installed)";
                var icon = installed ? ProgramIcon(DevTools.ProgramPath(tool)) : null;

                var button = new ToolStripButton(text, icon) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Margin = new Padding(2, 0, 2, 0) };
                button.ToolTipText = tool == DevTool.VisualStudio
                    ? "Open the solution (or project) of the local copy in Visual Studio"
                    : $"Open the local copy's folder in {DevTools.Label(tool)}";
                button.Click += async (_, _) => await OpenLocalCopy(tool);

                var menuItem = new ToolStripMenuItem($"Open in {text}", icon);
                menuItem.Click += async (_, _) => await OpenLocalCopy(tool);

                _repoBar.Items.Add(button);
                menu.Items.Add(menuItem);
                _openItems[tool] = new ToolStripItem[] { button, menuItem };
            }

            _repoBar.Items.Add(new ToolStripSeparator());
            _repoBar.Items.Add(_folderLabel);
            _changeFolder.ToolTipText = "Use another copy of this repository on this PC";
            _changeFolder.Click += (_, _) => { if (SelectedRepo != null) ChooseLocalCopy(SelectedRepo); };
            _repoBar.Items.Add(_changeFolder);

            var changeItem = new ToolStripMenuItem("Change local folder...");
            changeItem.Click += (_, _) => { if (SelectedRepo != null) ChooseLocalCopy(SelectedRepo); };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(changeItem);
            menu.Opening += (_, e) => e.Cancel = SelectedRepo == null || !_repoBar.Enabled;
            _repoList.ContextMenuStrip = menu;

            UpdateRepoBar();
        }

        private void UpdateRepoBar()
        {
            var repo = SelectedRepo;
            var folder = repo == null ? null : _fileService.FindLocalFolder(repo.Owner, repo.Name);

            _repoTitle.Text = repo == null ? "Select a repository" : $"{repo.Owner}/{repo.Name}";
            _folderLabel.Text = repo == null ? "" : folder ?? "not on this PC yet (opening it offers to clone it)";
            _folderLabel.ToolTipText = folder;
            _changeFolder.Visible = repo != null;

            foreach (var (tool, items) in _openItems)
                foreach (var item in items)
                    item.Enabled = repo != null && DevTools.IsInstalled(tool);
        }

        private async Task OpenLocalCopy(DevTool tool)
        {
            var repo = SelectedRepo;
            if (repo == null) return;

            var folder = _fileService.FindLocalFolder(repo.Owner, repo.Name) ?? await LocateLocalCopy(repo);
            if (folder == null) return;

            var target = folder;
            if (tool == DevTool.VisualStudio)
            {
                var projects = await Task.Run(() => DevTools.FindProjects(folder));
                if (projects.Count == 1)
                {
                    target = projects[0];
                }
                else if (projects.Count > 1)
                {
                    var input = PromptDialog.Show(this, $"Open {repo.Name} in Visual Studio",
                        new PromptField { Label = "Solution / project:", Choices = projects.Select(p => Path.GetRelativePath(folder, p)).ToArray() });
                    if (input == null) return;
                    target = Path.Combine(folder, input[0]);
                }
                else
                {
                    Log($"{folder} has no solution or project file: Visual Studio opens the folder");
                }
            }

            try
            {
                DevTools.Open(tool, target);
                Log($"Opened {target} in {DevTools.Label(tool)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, $"Open in {DevTools.Label(tool)}", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // The repository has no known copy on this PC: clone it, or point to a copy made before
        private async Task<string> LocateLocalCopy(RepoInfo repo)
        {
            var page = new TaskDialogPage
            {
                Caption = "Local copy",
                Heading = $"Where is {repo.Owner}/{repo.Name} on this PC?",
                Text = "No copy of this repository is known on this PC yet. Clone it into a folder of your choice, " +
                       "or choose a copy you already have (a folder you pushed from, pulled or cloned into).",
                Icon = TaskDialogIcon.Information
            };
            var clone = new TaskDialogButton("Clone it...");
            var choose = new TaskDialogButton("Choose its folder...");
            page.Buttons.Add(clone);
            page.Buttons.Add(choose);
            page.Buttons.Add(TaskDialogButton.Cancel);

            var answer = TaskDialog.ShowDialog(this, page);
            if (answer == clone) return await CloneInto(repo);
            if (answer == choose) return ChooseLocalCopy(repo);
            return null;
        }

        // Only a folder that is a tracked copy of this very repository is accepted
        private string ChooseLocalCopy(RepoInfo repo)
        {
            var path = ChooseFolder($"Folder that holds {repo.Owner}/{repo.Name}");
            if (path == null) return null;

            var state = _fileService.GetLocalState(path);
            var matches = state != null &&
                          string.Equals(state.Owner, repo.Owner, StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(state.RepoName, repo.Name, StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                MessageBox.Show(this, state == null
                        ? "That folder is not linked to a repository (it has no .qarepo file). Pull or Clone into it first."
                        : $"That folder is a copy of {state.Owner}/{state.RepoName}, not of {repo.Owner}/{repo.Name}.",
                    "Local copy", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            _fileService.RememberLocalFolder(path, state);
            UpdateRepoBar();
            Log($"{repo.Owner}/{repo.Name} on this PC: {path}");
            return path;
        }

        private static Image ProgramIcon(string program)
        {
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(program);
                return icon?.ToBitmap();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task ShowVersions()
        {
            var repo = SelectedRepo;
            if (repo == null || !_toolbar.Enabled) return;

            await Run($"Loading history of {repo.Owner}/{repo.Name}", async _ =>
            {
                var versions = await _repoOperations.GetVersionsAsync(repo.Owner, repo.Name);

                _versionList.BeginUpdate();
                _versionList.Items.Clear();
                foreach (var version in versions)
                {
                    _versionList.Items.Add(new ListViewItem(new[]
                    {
                        version.Name, version.CreatedAt.ToString("yyyy-MM-dd HH:mm"), version.CreatedBy ?? "",
                        version.FileCount.ToString(), version.Message ?? ""
                    }));
                }
                _versionList.EndUpdate();
            });
        }

        private async Task CreateRepository()
        {
            var input = PromptDialog.Show(this, "Create repository",
                new PromptField { Label = "Name:" },
                new PromptField { Label = "Description:", Optional = true });
            if (input == null) return;

            await Run($"Creating {input[0]}", async _ =>
            {
                await _repoOperations.CreateAsync(input[0].Trim(), input[1]);
                Log($"Created repository {input[0]}");
            });
            await RefreshRepositories();
        }

        private async Task PushRepository()
        {
            var path = ChooseFolder("Folder to push");
            if (path == null) return;

            var state = _fileService.GetLocalState(path);
            var target = state != null ? $"{state.Owner}/{state.RepoName}" : null;

            var fields = new List<PromptField>();
            if (state == null) fields.Add(new PromptField { Label = "New repo name:", Value = Path.GetFileName(path) });
            fields.Add(new PromptField { Label = "Message:" });
            fields.Add(new PromptField { Label = "Add to .qaignore:", Optional = true });

            var input = PromptDialog.Show(this, target != null ? $"Push to {target}" : "Push new repository", fields.ToArray());
            if (input == null) return;

            var repoName = state == null ? input[0].Trim() : null;
            var message = input[^2];
            var ignore = input[^1];

            await Run("Pushing", async progress =>
            {
                if (!string.IsNullOrWhiteSpace(ignore)) _fileService.AddIgnoreEntries(path, ignore.Split(','));

                var result = await _repoOperations.PushAsync(path, repoName, message, progress);
                Log((result.Changed
                    ? $"Pushed {result.FileCount} files from {path} as {result.Version}"
                    : $"Nothing changed in {path}: the server is already at {result.Version}") + $" · {result.Note}");
            });
            await RefreshRepositories();
        }

        // Pull latest, or with chooseVersion a specific older/newer version (switch/downgrade)
        private async Task PullRepository(bool chooseVersion)
        {
            var path = ChooseFolder(chooseVersion ? "Tracked folder to switch" : "Folder to pull into");
            if (path == null) return;

            var state = _fileService.GetLocalState(path);
            var owner = state?.Owner ?? SelectedRepo?.Owner;
            var repoName = state?.RepoName ?? SelectedRepo?.Name;

            if (chooseVersion && state == null)
            {
                MessageBox.Show(this, "That folder is not a tracked repository (it has no .qarepo file).", "Switch version", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (owner == null)
            {
                MessageBox.Show(this, "That folder is not tracked yet. Select the repository to pull in the list first.", "Pull", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string version = null;
            if (chooseVersion)
            {
                List<VersionInfo> versions = null;
                await Run("Loading versions", async _ => versions = await _repoOperations.GetVersionsAsync(owner, repoName));
                if (versions == null || versions.Count == 0) return;

                var input = PromptDialog.Show(this, $"Switch {owner}/{repoName} (current: {state.CurrentVersion})",
                    new PromptField { Label = "Version:", Choices = versions.Select(v => v.Name).ToArray() });
                if (input == null) return;
                version = input[0];
            }
            else if (MessageBox.Show(this, $"Pull the latest version of {owner}/{repoName} into\n{path}?\n\nFiles with the same name are overwritten.",
                         "Pull", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                return;
            }

            await Run(chooseVersion ? "Switching version" : "Pulling", async progress =>
            {
                var pulled = await _repoOperations.PullAsync(path, owner, repoName, version, progress);
                Log($"{path} is now at {owner}/{repoName} {pulled.Version} · {pulled.Note}");
            });
            UpdateRepoBar();
        }

        private async Task CloneRepository()
        {
            var repo = SelectedRepo;
            if (repo == null)
            {
                MessageBox.Show(this, "Select the repository to clone in the list first.", "Clone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            await CloneInto(repo);
        }

        // The folder the clone went into, or null when it was cancelled or failed
        private async Task<string> CloneInto(RepoInfo repo)
        {
            var parent = ChooseFolder($"Where to put the clone of {repo.Owner}/{repo.Name}");
            if (parent == null) return null;

            var destination = Path.Combine(parent, repo.Name);
            string done = null;
            await Run("Cloning", async progress =>
            {
                var cloned = await _repoOperations.CloneAsync(repo.Owner, repo.Name, destination, progress);
                Log($"Cloned {repo.Owner}/{repo.Name} {cloned.Version} into {destination} · {cloned.Note}");
                done = destination;
            });
            UpdateRepoBar();
            return done;
        }

        private async Task ChangePassword()
        {
            var input = PromptDialog.Show(this, "Change password",
                new PromptField { Label = "Current password:", IsPassword = true },
                new PromptField { Label = "New password:", IsPassword = true },
                new PromptField { Label = "Confirm new password:", IsPassword = true });
            if (input == null) return;

            if (input[1] != input[2])
            {
                MessageBox.Show(this, "The new passwords do not match.", "Change password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            await Run("Changing password", async _ =>
            {
                await _authService.ChangePasswordAsync(input[0], input[1]);
                Log("Password changed");
            });
        }

        private async Task Logout()
        {
            _authService.Logout();
            _repoList.Items.Clear();
            _versionList.Items.Clear();
            Log("Logged out");

            if (!await Login())
            {
                Close();
                return;
            }
            if (await UpdateClient()) return;
            await RefreshRepositories();
        }

        // Runs one task with the toolbar locked, shows progress, and turns every failure into a message
        private async Task Run(string action, Func<IProgress<TransferProgress>, Task> work)
        {
            _toolbar.Enabled = false;
            _repoBar.Enabled = false;
            _status.Text = $"{action}...";

            var progress = new Progress<TransferProgress>(p =>
            {
                if (IsDisposed) return;
                _progress.Value = p.Percent;
                _status.Text = $"{action}... {p.Percent}%";
            });

            try
            {
                await work(progress);
            }
            catch (Exception) when (IsDisposed)
            {
                // The window was closed while the task was running; there is nobody left to tell
            }
            catch (ApiException ex) when (ex.IsUnauthorized)
            {
                Log("Session expired");
                _authService.Logout();
                MessageBox.Show(this, "Your session has expired. Please log in again, then repeat the action.", action, MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (!await Login()) Close();
            }
            catch (Exception ex)
            {
                // Whatever went wrong (network, disk, locked file), report it and keep the window usable
                Log($"{action} failed: {ex.Message}");
                MessageBox.Show(this, ex.Message, $"{action} failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (!IsDisposed)
                {
                    _progress.Value = 0;
                    _status.Text = "Ready";
                    _toolbar.Enabled = true;
                    _repoBar.Enabled = true;
                }
            }
        }

        private void AddButton(string text, Func<Task> action)
        {
            var button = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, Margin = new Padding(2, 0, 2, 0) };
            button.Click += async (_, _) => await action();
            _toolbar.Items.Add(button);
        }

        private string ChooseFolder(string description)
        {
            using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
        }

        // What the network layer is doing (searching, falling back, retrying) is shown as it happens
        private void ShowNetworkStatus(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(() => ShowNetworkStatus(message)); return; }

            _status.Text = message;
            Log(message);
        }

        private void Log(string message)
        {
            if (IsDisposed) return;
            _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        }

        private static ListView NewList(params (string Title, int Width)[] columns)
        {
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
            foreach (var (title, width) in columns) list.Columns.Add(title, width);
            return list;
        }
    }
}
