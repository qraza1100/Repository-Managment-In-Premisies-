#nullable disable
using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using QualityAviationCodeRepositoryConsoleClient.Console.Services;
using QualityAviationHelpdesk.Contracts;
using System.Runtime.InteropServices;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Forms
{
    // The Helpdesk tab: where IT reviews and completes the tickets Q+ CMR users raise on the web, edits the web
    // form at run time, and keeps the team's shared to-do list.
    //
    //   Tickets            the queue (filters, search) and the selected ticket: details, conversation, files,
    //                      history, and the next steps it allows (accept, start, resolve...)
    //   To-do              who is doing what, grouped by state; nothing is deleted, only archived
    //   Form & settings    the web form's extra fields and the helpdesk settings (IT leads change them)
    //   Audit              the tamper-evident trail and its check (IT leads)
    //
    // It stays in step with the server by itself: a long poll learns about every change within a second, and the
    // lists that changed are reloaded. The terminal's Helpdesk menu does the same things.
    public class HelpdeskPanel : UserControl
    {
        private static readonly (string Key, string Label)[] StatusFilters =
        {
            ("open", "Open"), ("overdue", "Overdue"), (TicketStatus.New, "New"), (TicketStatus.Accepted, "Accepted"), (TicketStatus.InProgress, "In progress"),
            (TicketStatus.OnHold, "On hold"), (TicketStatus.Resolved, "Resolved"), (TicketStatus.Closed, "Closed"), (TicketStatus.Rejected, "Rejected"),
            (TicketStatus.Cancelled, "Cancelled"), ("all", "All")
        };

        private readonly IHelpdeskClient _helpdesk;
        private readonly Action<string> _log;
        private readonly Func<Task<bool>> _reauthenticate;
        private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 600 };
        private CancellationTokenSource _follow;

        private readonly Label _banner = new() { Dock = DockStyle.Top, AutoSize = false, Visible = false, ForeColor = Color.White, BackColor = Palette.Amber, Padding = new Padding(10, 6, 10, 6), TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand };
        private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
        private readonly TabPage _auditPage = new("Audit");

        // tickets
        private readonly ComboBox _statusFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        private readonly ComboBox _assigneeFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        private readonly TextBox _search = new() { Width = 200, PlaceholderText = "QA-1001, words, name, department" };
        private readonly Label _queueSummary = new() { AutoSize = true, ForeColor = Palette.Muted, Margin = new Padding(12, 8, 0, 0) };
        private readonly ListView _ticketList = NewList(("Ticket", 75), ("P", 34), ("Status", 90), ("Summary", 300), ("Requester", 130), ("Department", 100), ("With", 80), ("Raised", 60), ("Target", 75));
        private readonly Label _ticketTitle = new() { AutoSize = true, ForeColor = Palette.Text, Margin = new Padding(0, 0, 0, 2) };
        private readonly Label _ticketMeta = new() { AutoSize = true, ForeColor = Palette.Muted };
        private readonly FlowLayoutPanel _actionBar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 4, 0, 4) };
        private readonly TabControl _detailTabs = new() { Dock = DockStyle.Fill };
        private readonly TextBox _detailsBox = TextView();
        private readonly TextBox _conversationBox = TextView();
        private readonly ListView _fileList = NewList(("File", 260), ("Size", 70), ("From", 120), ("Who sees it", 110), ("When", 120));
        private readonly ListView _historyList = NewList(("When", 120), ("Who", 110), ("What", 120), ("Detail", 420));

        // to-do
        private readonly ListView _todoList = NewList(("What", 330), ("Who", 100), ("Priority", 85), ("Due", 85), ("Ticket", 70), ("Updated", 150), ("Notes", 260));
        private readonly CheckBox _onlyMine = new() { Text = "Only mine", AutoSize = true, Margin = new Padding(12, 7, 0, 0) };
        private readonly CheckBox _olderDone = new() { Text = "Older done items", AutoSize = true, Margin = new Padding(6, 7, 0, 0) };
        private readonly CheckBox _archived = new() { Text = "Archive", AutoSize = true, Margin = new Padding(6, 7, 0, 0) };

        // fields and settings
        private readonly ListView _fieldList = NewList(("#", 34), ("Label", 200), ("Key", 120), ("Kind", 90), ("Filled in by", 90), ("Required", 65), ("Shown", 55), ("Ticket types", 200), ("Options", 260));
        private readonly Label _settingsSummary = new() { Dock = DockStyle.Bottom, AutoSize = false, ForeColor = Palette.Muted, Padding = new Padding(8) };
        private readonly FlowLayoutPanel _fieldButtons = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };

        // audit
        private readonly ListView _auditList = NewList(("When", 125), ("Who", 120), ("What", 120), ("On", 110), ("Detail", 520));
        private readonly Label _auditResult = new() { AutoSize = true, Margin = new Padding(12, 8, 0, 0) };

        private ItMe _me;
        private HelpdeskSettings _settings;
        private List<FieldDefinitionDto> _fields = new();
        private TicketDetail _ticket;
        private List<TodoDto> _todos = new();
        private bool _started;
        private bool _busy;
        private bool _ticketsChanged, _todosChanged, _fieldsChanged, _auditChanged;
        private readonly HashSet<string> _changedTickets = new();

        public event Action<string> TitleChanged;

        public HelpdeskPanel(IHelpdeskClient helpdesk, Action<string> log, Func<Task<bool>> reauthenticate)
        {
            _helpdesk = helpdesk;
            _log = log;
            _reauthenticate = reauthenticate;

            Dock = DockStyle.Fill;
            BackColor = Palette.Background;
            Build();

            _debounce.Tick += async (_, _) =>
            {
                _debounce.Stop();
                await ApplyChangesAsync();
            };
            _banner.Click += async (_, _) => await StartAsync();
            VisibleChanged += async (_, _) =>
            {
                if (Visible && !_started) await StartAsync();
                else if (Visible) await ApplyChangesAsync();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _follow?.Cancel();
                _debounce.Dispose();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ start and live updates

        // Loads everything once, then follows the server's change feed for as long as the window is open
        public async Task StartAsync()
        {
            if (_busy) return;
            await Guard("Opening the helpdesk", async () =>
            {
                _busy = true;
                try
                {
                    _me = await _helpdesk.MeAsync();
                    _settings = _me.Settings;
                    _fields = await _helpdesk.FieldsAsync();
                    _banner.Visible = false;
                    _started = true;

                    FillAssignees();
                    if (_me.IsLead && !_tabs.TabPages.Contains(_auditPage)) _tabs.TabPages.Add(_auditPage);
                    foreach (Control button in _fieldButtons.Controls) button.Enabled = _me.IsLead || button.Tag as string == "always";
                    _log($"Helpdesk at {_helpdesk.Address} · signed in as {_me.Username}{(_me.IsLead ? " (IT lead)" : "")}");

                    await LoadTicketsAsync();
                    await LoadTodosAsync();
                    ShowFields();
                    if (_me.IsLead) await LoadAuditAsync();
                }
                finally
                {
                    _busy = false;
                }

                _follow?.Cancel();
                _follow = new CancellationTokenSource();
                _ = FollowAsync(_follow.Token);
            });
        }

        private async Task FollowAsync(CancellationToken stop)
        {
            long after = 0;
            var failures = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var batch = await _helpdesk.ChangesAsync(after, stop);
                    failures = 0;
                    if (after == 0)
                    {
                        after = batch.Last;
                        continue;
                    }
                    if (batch.Last <= after && !batch.Reset) continue;
                    after = batch.Last;
                    if (IsDisposed) return;
                    BeginInvoke(() => Noticed(batch));
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (ApiException ex) when (ex.IsUnauthorized)
                {
                    // The login ran out: say so; the banner (or the next action) asks for a new one and follows again
                    if (!IsDisposed)
                        BeginInvoke(() =>
                        {
                            _started = false;
                            _banner.Text = "⚠ Your login has expired, so the helpdesk is no longer updating. Click here to sign in again.";
                            _banner.Visible = true;
                        });
                    return;
                }
                catch (Exception)
                {
                    failures++;
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * failures)), stop);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private void Noticed(ChangeBatch batch)
        {
            if (IsDisposed) return;
            if (batch.Reset)
            {
                _ticketsChanged = _todosChanged = _fieldsChanged = _auditChanged = true;
            }
            foreach (var change in batch.Changes)
            {
                switch (change.Topic)
                {
                    case ChangeTopics.Ticket:
                        _ticketsChanged = true;
                        _changedTickets.Add(change.EntityId);
                        break;
                    case ChangeTopics.Todo:
                        _todosChanged = true;
                        break;
                    default:
                        _fieldsChanged = true;
                        break;
                }
                _auditChanged = true;
            }
            _debounce.Stop();
            _debounce.Start();
        }

        // Reloads what changed. While the tab is hidden only the queue count is kept current.
        private async Task ApplyChangesAsync()
        {
            if (!_started || _busy || IsDisposed) return;
            await Guard("Updating the helpdesk", async () =>
            {
                if (_fieldsChanged)
                {
                    _fieldsChanged = false;
                    _me = await _helpdesk.MeAsync();
                    _settings = _me.Settings;
                    _fields = await _helpdesk.FieldsAsync();
                    ShowFields();
                    FillAssignees();
                }
                if (_ticketsChanged)
                {
                    _ticketsChanged = false;
                    var selected = _ticket?.Id.ToString();
                    var reloadSelected = selected != null && _changedTickets.Contains(selected);
                    _changedTickets.Clear();
                    await LoadTicketsAsync(keepDetail: !reloadSelected);
                }
                if (_todosChanged && Visible)
                {
                    _todosChanged = false;
                    await LoadTodosAsync();
                }
                if (_auditChanged && Visible && _me.IsLead && _tabs.SelectedTab == _auditPage)
                {
                    _auditChanged = false;
                    await LoadAuditAsync();
                }
            });
        }

        // ------------------------------------------------------------------ layout

        private void Build()
        {
            var ticketsPage = new TabPage("Tickets") { BackColor = Palette.Background };
            var todoPage = new TabPage("To-do") { BackColor = Palette.Background };
            var fieldsPage = new TabPage("Form & settings") { BackColor = Palette.Background };
            _tabs.TabPages.AddRange(new[] { ticketsPage, todoPage, fieldsPage });
            _tabs.SelectedIndexChanged += async (_, _) =>
            {
                if (_tabs.SelectedTab == _auditPage) await Guard("Loading the audit trail", LoadAuditAsync);
                else if (_tabs.SelectedTab == todoPage && _todosChanged) await ApplyChangesAsync();
            };

            BuildTickets(ticketsPage);
            BuildTodos(todoPage);
            BuildFields(fieldsPage);
            BuildAudit(_auditPage);

            Controls.Add(_tabs);
            Controls.Add(_banner);
            _banner.Height = LogicalToDeviceUnits(34);
        }

        private void BuildTickets(TabPage page)
        {
            foreach (var (_, label) in StatusFilters) _statusFilter.Items.Add(label);
            _statusFilter.SelectedIndex = 0;
            _assigneeFilter.Items.AddRange(new object[] { "Anyone", "Me", "Nobody yet" });
            _assigneeFilter.SelectedIndex = 0;
            _statusFilter.Width = LogicalToDeviceUnits(_statusFilter.Width);
            _assigneeFilter.Width = LogicalToDeviceUnits(_assigneeFilter.Width);
            _search.Width = LogicalToDeviceUnits(_search.Width);

            var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(6, 6, 6, 2), BackColor = Palette.Card };
            var searchButton = Action("Search");
            var refresh = Action("Refresh");
            filters.Controls.AddRange(new Control[] { Caption("Status"), _statusFilter, Caption("With"), _assigneeFilter, _search, searchButton, refresh, _queueSummary });
            _statusFilter.SelectedIndexChanged += async (_, _) => await Guard("Loading tickets", () => LoadTicketsAsync());
            _assigneeFilter.SelectedIndexChanged += async (_, _) => await Guard("Loading tickets", () => LoadTicketsAsync());
            searchButton.Click += async (_, _) => await Guard("Searching", () => LoadTicketsAsync());
            refresh.Click += async (_, _) => await Guard("Loading tickets", () => LoadTicketsAsync(keepDetail: false));
            _search.KeyDown += async (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                await Guard("Searching", () => LoadTicketsAsync());
            };

            _ticketList.SelectedIndexChanged += async (_, _) =>
            {
                if (_ticketList.SelectedItems.Count == 0 || _ticketList.SelectedItems[0].Tag is not TicketListItem item || item.Id == _ticket?.Id) return;
                await Guard("Opening the ticket", () => ShowTicketAsync(item.Id));
            };

            var header = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 10, 12, 4), BackColor = Palette.Card };
            _ticketTitle.Font = new Font(Font.FontFamily, Font.Size * 1.3f, FontStyle.Bold);
            _ticketTitle.MaximumSize = new Size(LogicalToDeviceUnits(900), 0);
            var headerText = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            headerText.Controls.Add(_ticketTitle);
            headerText.Controls.Add(_ticketMeta);
            header.Controls.Add(_actionBar);
            header.Controls.Add(headerText);

            var details = new TabPage("Details");
            details.Controls.Add(_detailsBox);
            var conversation = new TabPage("Conversation");
            conversation.Controls.Add(_conversationBox);
            var files = new TabPage("Files");
            var fileButtons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
            var saveFile = Action("Save as…");
            var preview = Action("Preview image");
            saveFile.Click += async (_, _) => await Guard("Saving the file", SaveFileAsync);
            preview.Click += async (_, _) => await Guard("Opening the image", PreviewFileAsync);
            _fileList.DoubleClick += async (_, _) => await Guard("Opening the image", PreviewFileAsync);
            fileButtons.Controls.AddRange(new Control[] { saveFile, preview, new Label { Text = "Files from requesters are saved, never run: open them only if you trust them.", AutoSize = true, ForeColor = Palette.Muted, Margin = new Padding(8, 8, 0, 0) } });
            files.Controls.Add(_fileList);
            files.Controls.Add(fileButtons);
            var history = new TabPage("History");
            history.Controls.Add(_historyList);
            _detailTabs.TabPages.AddRange(new[] { details, conversation, files, history });

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(_detailTabs);
            right.Controls.Add(header);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Palette.Border };
            split.Panel1.Controls.Add(_ticketList);
            split.Panel2.Controls.Add(right);
            split.Panel2.BackColor = Palette.Background;
            page.Controls.Add(split);
            page.Controls.Add(filters);
            // The queue gets the top third once the tab has its real size; after that the user's drag is kept
            var placed = false;
            page.Layout += (_, _) =>
            {
                if (placed || split.Height < 300) return;
                placed = true;
                split.SplitterDistance = split.Height / 3;
            };

            ShowTicket(null);
        }

        private void BuildTodos(TabPage page)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), BackColor = Palette.Card };
            var add = Action("+ New to-do", primary: true);
            var edit = Action("Edit…");
            var doing = Action("Doing");
            var blocked = Action("Blocked");
            var done = Action("Done ✓");
            var todo = Action("Back to To do");
            var assign = Action("Give to…");
            var archive = Action("Archive");
            var restore = Action("Restore");
            var history = Action("History…");
            add.Click += async (_, _) => await Guard("Adding the to-do", () => EditTodoAsync(null));
            edit.Click += async (_, _) => await Guard("Saving the to-do", () => EditTodoAsync(SelectedTodo));
            _todoList.DoubleClick += async (_, _) => await Guard("Saving the to-do", () => EditTodoAsync(SelectedTodo));
            doing.Click += async (_, _) => await Guard("Updating the to-do", () => SetTodoAsync(t => t.Status = TodoStatus.Doing));
            blocked.Click += async (_, _) => await Guard("Updating the to-do", () => SetTodoAsync(t => t.Status = TodoStatus.Blocked));
            done.Click += async (_, _) => await Guard("Updating the to-do", () => SetTodoAsync(t => t.Status = TodoStatus.Done));
            todo.Click += async (_, _) => await Guard("Updating the to-do", () => SetTodoAsync(t => t.Status = TodoStatus.Todo));
            assign.Click += async (_, _) => await Guard("Updating the to-do", AssignTodoAsync);
            archive.Click += async (_, _) => await Guard("Archiving the to-do", () => ArchiveTodoAsync(restore: false));
            restore.Click += async (_, _) => await Guard("Restoring the to-do", () => ArchiveTodoAsync(restore: true));
            history.Click += async (_, _) => await Guard("Loading the history", TodoHistoryAsync);
            foreach (var box in new[] { _onlyMine, _olderDone, _archived })
                box.CheckedChanged += async (_, _) => await Guard("Loading the to-do list", LoadTodosAsync);
            bar.Controls.AddRange(new Control[] { add, edit, doing, blocked, done, todo, assign, archive, restore, history, _onlyMine, _olderDone, _archived });

            foreach (var status in TodoStatus.All) _todoList.Groups.Add(status, TodoStatus.Label(status));
            _todoList.ShowGroups = true;
            page.Controls.Add(_todoList);
            page.Controls.Add(bar);
        }

        private void BuildFields(TabPage page)
        {
            var add = Action("+ Add field", primary: true);
            var edit = Action("Edit…");
            var up = Action("Move up");
            var down = Action("Move down");
            var toggle = Action("Hide / show");
            var settings = Action("Settings…");
            add.Click += async (_, _) => await Guard("Saving the field", () => EditFieldAsync(null));
            edit.Click += async (_, _) => await Guard("Saving the field", () => EditFieldAsync(SelectedField));
            _fieldList.DoubleClick += async (_, _) => { if (_me?.IsLead == true) await Guard("Saving the field", () => EditFieldAsync(SelectedField)); };
            up.Click += async (_, _) => await Guard("Moving the field", () => MoveFieldAsync(-1));
            down.Click += async (_, _) => await Guard("Moving the field", () => MoveFieldAsync(1));
            toggle.Click += async (_, _) => await Guard("Saving the field", ToggleFieldAsync);
            settings.Click += async (_, _) => await Guard("Saving the settings", EditSettingsAsync);
            _fieldButtons.Controls.AddRange(new Control[] { add, edit, up, down, toggle, settings,
                new Label { Text = "The web form shows these fields to requesters, or keeps them for IT. Changes apply at once. Only IT leads can change them.", AutoSize = true, ForeColor = Palette.Muted, Margin = new Padding(10, 8, 0, 0) } });
            _settingsSummary.AutoSize = true;
            _settingsSummary.MaximumSize = new Size(LogicalToDeviceUnits(1600), 0);

            page.Controls.Add(_fieldList);
            page.Controls.Add(_settingsSummary);
            page.Controls.Add(_fieldButtons);
        }

        private void BuildAudit(TabPage page)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), BackColor = Palette.Card };
            var verify = Action("Verify the whole trail", primary: true);
            var refresh = Action("Refresh");
            verify.Click += async (_, _) => await Guard("Verifying the audit trail", VerifyAuditAsync);
            refresh.Click += async (_, _) => await Guard("Loading the audit trail", LoadAuditAsync);
            bar.Controls.AddRange(new Control[] { verify, refresh, _auditResult });
            page.Controls.Add(_auditList);
            page.Controls.Add(bar);
        }

        // ------------------------------------------------------------------ tickets

        private async Task LoadTicketsAsync(bool keepDetail = true)
        {
            var status = StatusFilters[Math.Max(_statusFilter.SelectedIndex, 0)].Key;
            var assignee = _assigneeFilter.SelectedIndex switch
            {
                <= 0 => null,
                1 => "me",
                2 => "none",
                _ => _assigneeFilter.SelectedItem as string
            };

            var page = await _helpdesk.TicketsAsync(status, assignee, _search.Text.Trim(), 1, 200);
            var selectedId = _ticket?.Id;

            _ticketList.BeginUpdate();
            _ticketList.Items.Clear();
            foreach (var t in page.Items)
            {
                var item = new ListViewItem(new[]
                {
                    t.Number, $"P{t.Priority}", TicketStatus.Label(t.Status), t.Summary, t.RequesterName, t.Department ?? "", t.Assignee ?? "—",
                    HelpdeskText.Ago(t.CreatedAt), TicketStatus.IsOpen(t.Status) ? HelpdeskText.Due(t.DueAt) : ""
                }) { Tag = t };
                if (t.Overdue) item.ForeColor = Palette.Red;
                if (t.Status == TicketStatus.New) item.Font = new Font(_ticketList.Font, FontStyle.Bold);
                _ticketList.Items.Add(item);
                if (t.Id == selectedId) item.Selected = true;
            }
            _ticketList.EndUpdate();

            var newCount = page.Counts.GetValueOrDefault(TicketStatus.New);
            var open = TicketStatus.Open.Sum(s => page.Counts.GetValueOrDefault(s));
            var overdue = page.Counts.GetValueOrDefault("overdue");
            _queueSummary.Text = $"{page.Total} shown · {open} open · {newCount} new · {overdue} overdue";
            TitleChanged?.Invoke(newCount > 0 ? $"Helpdesk ({newCount} new)" : "Helpdesk");

            if (selectedId != null && (!keepDetail || page.Items.All(i => i.Id != selectedId)))
            {
                // The selected ticket changed (or left this list): show its newest state
                await ShowTicketAsync(selectedId.Value);
            }
        }

        private async Task ShowTicketAsync(int id)
        {
            ShowTicket(await _helpdesk.TicketAsync(id));
        }

        private void ShowTicket(TicketDetail t)
        {
            _ticket = t;
            _actionBar.Controls.Clear();
            if (t == null)
            {
                _ticketTitle.Text = "Select a ticket";
                _ticketMeta.Text = "The queue is above. New tickets are in bold, late ones in red. The list updates by itself.";
                _detailsBox.Text = _conversationBox.Text = "";
                _fileList.Items.Clear();
                _historyList.Items.Clear();
                return;
            }

            _ticketTitle.Text = $"{t.Number}  {t.Summary}";
            _ticketMeta.Text = $"{TicketStatus.Label(t.Status)} · {Priorities.Label(t.Priority)} · {t.Type} · with {t.Assignee ?? "nobody yet"}" +
                               (TicketStatus.IsOpen(t.Status) ? $" · target {HelpdeskText.Due(t.DueAt)}" : "") +
                               (t.MinutesSpent > 0 ? $" · {t.MinutesSpent / 60.0:0.#} h logged" : "") +
                               (t.ReopenCount > 0 ? $" · reopened {t.ReopenCount}×" : "");
            _ticketMeta.ForeColor = t.Overdue ? Palette.Red : Palette.Muted;

            // The next steps this ticket allows, then what is always possible
            foreach (var action in t.AllowedActions)
            {
                var button = Action(TicketActions.Label(action), primary: action is TicketActions.Start or TicketActions.Resolve or TicketActions.Accept);
                button.Click += async (_, _) => await Guard(TicketActions.Label(action), () => ActAsync(action));
                _actionBar.Controls.Add(button);
            }
            foreach (var (text, work) in new (string, Func<Task>)[]
                     {
                         ("Comment / note…", CommentAsync), ("Log time…", LogTimeAsync), ("Edit details…", EditTicketAsync)
                     })
            {
                var button = Action(text);
                button.Click += async (_, _) => await Guard(text.TrimEnd('…'), work);
                _actionBar.Controls.Add(button);
            }

            var lines = new List<string>
            {
                $"Raised by     {t.RequesterName} ({t.RequesterUserName})",
                $"Department    {t.Department}",
                $"E-mail        {t.RequesterEmail ?? "—"}",
                $"Raised        {HelpdeskText.When(t.CreatedAt)}",
                $"Target        {HelpdeskText.When(t.DueAt)}" + (t.Overdue ? "   (overdue)" : ""),
                $"Accepted      {HelpdeskText.When(t.AcceptedAt)}",
                $"Started       {HelpdeskText.When(t.StartedAt)}",
                $"Resolved      {HelpdeskText.When(t.ResolvedAt)}" + (t.ResolvedBy != null ? $" by {t.ResolvedBy}" : ""),
                $"Closed        {HelpdeskText.When(t.ClosedAt)}",
                "",
                "DESCRIPTION",
                t.Description,
                ""
            };
            if (!string.IsNullOrEmpty(t.StatusReason)) lines.InsertRange(lines.Count, new[] { "REASON / WAITING FOR", t.StatusReason, "" });
            if (!string.IsNullOrEmpty(t.Resolution)) lines.InsertRange(lines.Count, new[] { "RESOLUTION", t.Resolution, "" });
            if (t.Fields.Count > 0)
            {
                lines.Add("FORM FIELDS");
                foreach (var field in _fields.Where(f => t.Fields.ContainsKey(f.Key)).OrderBy(f => f.SortOrder))
                    lines.Add($"{field.Label}{(field.Audience == FieldAudience.It ? " (IT)" : "")}: {t.Fields[field.Key]}");
                foreach (var key in t.Fields.Keys.Where(k => _fields.All(f => f.Key != k)))
                    lines.Add($"{key}: {t.Fields[key]}");
            }
            _detailsBox.Text = string.Join(Environment.NewLine, lines.Select(l => (l ?? "").Replace("\n", Environment.NewLine)));

            _conversationBox.Text = t.Comments.Count == 0
                ? "No messages yet."
                : string.Join(Environment.NewLine + Environment.NewLine, t.Comments.Select(c =>
                    $"── {c.AuthorName} ({(c.AuthorKind == "it" ? "IT" : "requester")}) · {HelpdeskText.When(c.CreatedAt)}" +
                    (c.Internal ? "  · INTERNAL, requester does not see it" : "") + (c.MinutesSpent > 0 ? $"  · {c.MinutesSpent} min" : "") +
                    Environment.NewLine + c.Body.Replace("\n", Environment.NewLine)));
            _detailTabs.TabPages[1].Text = $"Conversation ({t.Comments.Count})";

            _fileList.Items.Clear();
            foreach (var file in t.Attachments)
                _fileList.Items.Add(new ListViewItem(new[] { file.FileName, HelpdeskText.Size(file.SizeBytes), file.UploadedByName, file.Internal ? "IT only" : "Requester and IT", HelpdeskText.When(file.CreatedAt) }) { Tag = file });
            _detailTabs.TabPages[2].Text = $"Files ({t.Attachments.Count})";

            _historyList.Items.Clear();
            foreach (var entry in t.Timeline.AsEnumerable().Reverse())
                _historyList.Items.Add(new ListViewItem(new[] { HelpdeskText.When(entry.At), entry.ActorName, entry.Action, entry.Detail ?? "" }));
        }

        private async Task ActAsync(string action)
        {
            var t = _ticket;
            if (t == null) return;
            var request = new TicketActionRequest { Version = t.Version };

            switch (action)
            {
                case TicketActions.Assign:
                    var nobody = "(nobody)";
                    var choice = PromptDialog.Show(this, $"Assign {t.Number}", new PromptField { Label = "Give it to:", Choices = new[] { nobody }.Concat(_me.Members).ToArray() });
                    if (choice == null) return;
                    request.Assignee = choice[0] == nobody ? null : choice[0];
                    break;
                case TicketActions.Priority:
                    var priority = PromptDialog.Show(this, $"Priority of {t.Number}", new PromptField { Label = "Priority:", Choices = Priorities.All.Select(Priorities.Label).ToArray() });
                    if (priority == null) return;
                    request.Priority = Array.IndexOf(Priorities.All.Select(Priorities.Label).ToArray(), priority[0]) + 1;
                    break;
                case TicketActions.Take:
                case TicketActions.Start:
                    break;
                case TicketActions.Close:
                    if (MessageBox.Show(this, $"Close {t.Number}? The requester has not confirmed it yet.", "Close", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    break;
                default:
                    var (prompt, required) = action switch
                    {
                        TicketActions.Resolve => ("What was done (the requester reads this):", true),
                        TicketActions.Reject => ("Why it is rejected (the requester reads this):", true),
                        TicketActions.Hold => ("What it is waiting for (the requester reads this):", true),
                        TicketActions.Reopen => ("Why it is reopened:", true),
                        _ => ("Note for IT (optional):", false)
                    };
                    var note = HelpdeskDialogs.Note(this, $"{TicketActions.Label(action)} {t.Number}", prompt, required, askMinutes: action is TicketActions.Resolve or TicketActions.Accept);
                    if (note == null) return;
                    request.Note = note.Text;
                    request.MinutesSpent = note.Minutes;
                    break;
            }

            ShowTicket(await _helpdesk.ActAsync(t.Id, action, request));
            _log($"{t.Number}: {TicketActions.Label(action)}");
            await LoadTicketsAsync();
        }

        private async Task CommentAsync()
        {
            var t = _ticket;
            if (t == null) return;
            var note = HelpdeskDialogs.Note(this, $"Comment on {t.Number}", "Message:", required: false, askMinutes: true, askVisibility: true, askFiles: true);
            if (note == null) return;
            if (note.Text.Length == 0 && note.Files.Count == 0 && note.Minutes == 0) return;
            ShowTicket(await _helpdesk.CommentAsync(t.Id, note.Text, internalOnly: !note.Visible, note.Minutes, note.Files));
            _log($"{t.Number}: {(note.Visible ? "message to the requester" : "internal note")} added");
        }

        private async Task LogTimeAsync()
        {
            var t = _ticket;
            if (t == null) return;
            var note = HelpdeskDialogs.Note(this, $"Log time on {t.Number}", "What you did (optional):", required: false, askMinutes: true, minutesRequired: true);
            if (note == null) return;
            ShowTicket(await _helpdesk.CommentAsync(t.Id, note.Text, internalOnly: true, note.Minutes, null));
            _log($"{t.Number}: {note.Minutes} min logged");
        }

        private async Task EditTicketAsync()
        {
            var t = _ticket;
            if (t == null) return;
            var request = HelpdeskDialogs.EditTicket(this, t, _settings, _fields);
            if (request == null) return;
            ShowTicket(await _helpdesk.EditAsync(t.Id, request));
            _log($"{t.Number}: details edited");
            await LoadTicketsAsync();
        }

        private AttachmentDto SelectedFile => _fileList.SelectedItems.Count > 0 ? _fileList.SelectedItems[0].Tag as AttachmentDto : null;

        private async Task SaveFileAsync()
        {
            var file = SelectedFile;
            if (file == null)
            {
                MessageBox.Show(this, "Select a file in the list first.", "Save file", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var dialog = new SaveFileDialog { FileName = file.FileName, Title = $"Save {file.FileName}", Filter = $"{Path.GetExtension(file.FileName)}|*{Path.GetExtension(file.FileName)}|All files|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await _helpdesk.SaveAttachmentAsync(file.Id, dialog.FileName);
            _log($"Saved {file.FileName} to {dialog.FileName}");
        }

        private async Task PreviewFileAsync()
        {
            var file = SelectedFile;
            if (file == null) return;
            if (!file.ContentType.StartsWith("image/", StringComparison.Ordinal))
            {
                await SaveFileAsync();
                return;
            }

            var temporary = Path.Combine(Path.GetTempPath(), $"qa-helpdesk-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
            try
            {
                await _helpdesk.SaveAttachmentAsync(file.Id, temporary);
                HelpdeskDialogs.PreviewImage(this, file.FileName, temporary);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private void FillAssignees()
        {
            if (_me == null) return;
            var selected = _assigneeFilter.SelectedItem as string;
            while (_assigneeFilter.Items.Count > 3) _assigneeFilter.Items.RemoveAt(3);
            foreach (var member in _me.Members.Where(m => !string.Equals(m, _me.Username, StringComparison.OrdinalIgnoreCase))) _assigneeFilter.Items.Add(member);
            var index = selected == null ? 0 : _assigneeFilter.Items.IndexOf(selected);
            _assigneeFilter.SelectedIndex = Math.Max(index, 0);
        }

        // ------------------------------------------------------------------ to-do

        private TodoDto SelectedTodo => _todoList.SelectedItems.Count > 0 ? _todoList.SelectedItems[0].Tag as TodoDto : null;

        private async Task LoadTodosAsync()
        {
            if (_me == null) return;
            _todos = await _helpdesk.TodosAsync(_olderDone.Checked, _archived.Checked);
            var selectedId = SelectedTodo?.Id;

            _todoList.BeginUpdate();
            _todoList.Items.Clear();
            foreach (var t in _todos.Where(t => !_onlyMine.Checked || string.Equals(t.Owner, _me.Username, StringComparison.OrdinalIgnoreCase)))
            {
                var item = new ListViewItem(new[]
                {
                    t.Title, t.Owner ?? "—", Priorities.Label(t.Priority), t.DueOn?.ToString("yyyy-MM-dd") ?? "", t.TicketNumber ?? "",
                    $"{t.UpdatedBy} · {HelpdeskText.Ago(t.UpdatedAt)} ago", (t.Notes ?? "").Replace("\n", " ")
                }, _todoList.Groups[t.Status]) { Tag = t };
                if (t.Status == TodoStatus.Done) item.ForeColor = Palette.Muted;
                else if (t.Status == TodoStatus.Blocked) item.ForeColor = Palette.Red;
                else if (t.DueOn != null && t.DueOn < DateOnly.FromDateTime(DateTime.Today)) item.ForeColor = Palette.Amber;
                _todoList.Items.Add(item);
                if (t.Id == selectedId) item.Selected = true;
            }
            _todoList.EndUpdate();

            var open = _todos.Count(t => t.Status != TodoStatus.Done);
            _tabs.TabPages[1].Text = open > 0 ? $"To-do ({open})" : "To-do";
        }

        private async Task EditTodoAsync(TodoDto todo)
        {
            var request = HelpdeskDialogs.EditTodo(this, todo, _me);
            if (request == null) return;
            var saved = await _helpdesk.SaveTodoAsync(todo?.Id ?? 0, request);
            _log(todo == null ? $"To-do added: {saved.Title}" : $"To-do changed: {saved.Title}");
            await LoadTodosAsync();
        }

        private async Task SetTodoAsync(Action<TodoSaveRequest> change)
        {
            var todo = SelectedTodo;
            if (todo == null)
            {
                MessageBox.Show(this, "Select a to-do first.", "To-do", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var request = new TodoSaveRequest
            {
                Version = todo.Version, Title = todo.Title, Notes = todo.Notes, Owner = todo.Owner, Status = todo.Status, Priority = todo.Priority, DueOn = todo.DueOn,
                TicketId = todo.TicketId
            };
            change(request);
            // Starting something nobody owns makes it yours
            if (request.Status == TodoStatus.Doing && request.Owner == null) request.Owner = _me.Username;
            await _helpdesk.SaveTodoAsync(todo.Id, request);
            _log($"To-do \"{todo.Title}\": {TodoStatus.Label(request.Status)}{(request.Owner != todo.Owner ? $", now with {request.Owner ?? "nobody"}" : "")}");
            await LoadTodosAsync();
        }

        private async Task AssignTodoAsync()
        {
            if (SelectedTodo == null) return;
            var nobody = "(nobody)";
            var choice = PromptDialog.Show(this, "Give the to-do to", new PromptField { Label = "Who:", Choices = new[] { nobody }.Concat(_me.Members).ToArray() });
            if (choice == null) return;
            await SetTodoAsync(t => t.Owner = choice[0] == nobody ? null : choice[0]);
        }

        private async Task ArchiveTodoAsync(bool restore)
        {
            var todo = SelectedTodo;
            if (todo == null) return;
            await _helpdesk.ArchiveTodoAsync(todo.Id, todo.Version, restore);
            _log($"To-do \"{todo.Title}\" {(restore ? "restored" : "archived")}");
            await LoadTodosAsync();
        }

        private async Task TodoHistoryAsync()
        {
            var todo = SelectedTodo;
            if (todo == null) return;
            var history = await _helpdesk.TodoHistoryAsync(todo.Id);
            HelpdeskDialogs.ShowText(this, $"History of \"{todo.Title}\"",
                string.Join(Environment.NewLine, history.Select(h => $"{HelpdeskText.When(h.At)}  {h.ActorName,-14} {h.Action,-14} {h.Detail}")));
        }

        // ------------------------------------------------------------------ form fields and settings

        private FieldDefinitionDto SelectedField => _fieldList.SelectedItems.Count > 0 ? _fieldList.SelectedItems[0].Tag as FieldDefinitionDto : null;

        private void ShowFields()
        {
            var selectedId = SelectedField?.Id;
            _fieldList.BeginUpdate();
            _fieldList.Items.Clear();
            var number = 1;
            foreach (var f in _fields.OrderBy(f => f.SortOrder))
            {
                var item = new ListViewItem(new[]
                {
                    (number++).ToString(), f.Label, f.Key, f.Kind, f.Audience == FieldAudience.It ? "IT" : "Requester", f.Required ? "yes" : "", f.Active ? "yes" : "hidden",
                    f.AppliesTo.Count == 0 ? "all" : string.Join(", ", f.AppliesTo), string.Join(" / ", f.Options)
                }) { Tag = f };
                if (!f.Active) item.ForeColor = Palette.Muted;
                _fieldList.Items.Add(item);
                if (f.Id == selectedId) item.Selected = true;
            }
            _fieldList.EndUpdate();

            if (_settings != null)
                _settingsSummary.Text = $"Ticket types: {string.Join(", ", _settings.TicketTypes)}" + Environment.NewLine +
                                        $"Targets: {string.Join(" · ", _settings.SlaHours.Select((h, i) => $"P{i + 1} {h} h"))} · requesters may reopen for {_settings.ReopenDays} days · " +
                                        $"resolved tickets close after {(_settings.AutoCloseDays == 0 ? "never" : _settings.AutoCloseDays + " days")} · files up to {_settings.MaxAttachmentMB} MB";
        }

        private async Task EditFieldAsync(FieldDefinitionDto field)
        {
            if (!_me.IsLead) return;
            var edited = HelpdeskDialogs.EditField(this, field, _settings);
            if (edited == null) return;
            _fields = await _helpdesk.SaveFieldAsync(edited);
            ShowFields();
            _log(field == null ? $"Form field added: {edited.Label}" : $"Form field changed: {edited.Label}");
        }

        private async Task MoveFieldAsync(int direction)
        {
            var field = SelectedField;
            if (field == null || !_me.IsLead) return;
            _fields = await _helpdesk.MoveFieldAsync(field.Id, direction);
            ShowFields();
        }

        private async Task ToggleFieldAsync()
        {
            var field = SelectedField;
            if (field == null || !_me.IsLead) return;
            field.Active = !field.Active;
            _fields = await _helpdesk.SaveFieldAsync(field);
            ShowFields();
            _log($"Form field {field.Label}: {(field.Active ? "shown" : "hidden")}");
        }

        private async Task EditSettingsAsync()
        {
            if (!_me.IsLead) return;
            var edited = HelpdeskDialogs.EditSettings(this, _settings);
            if (edited == null) return;
            _settings = await _helpdesk.SaveSettingsAsync(edited);
            ShowFields();
            _log("Helpdesk settings saved");
        }

        // ------------------------------------------------------------------ audit

        private async Task LoadAuditAsync()
        {
            if (_me?.IsLead != true) return;
            var entries = await _helpdesk.AuditAsync(300);
            _auditList.BeginUpdate();
            _auditList.Items.Clear();
            foreach (var e in entries)
                _auditList.Items.Add(new ListViewItem(new[] { HelpdeskText.When(e.At), e.ActorName, e.Action, $"{e.EntityType} {(e.EntityType == "ticket" ? TicketNumbers.Format(int.TryParse(e.EntityId, out var id) ? id : 0) : e.EntityId)}", e.Detail ?? "" }));
            _auditList.EndUpdate();
        }

        private async Task VerifyAuditAsync()
        {
            _auditResult.Text = "Checking every event…";
            var result = await _helpdesk.VerifyAuditAsync();
            _auditResult.Text = (result.Intact ? "✓ " : "⚠ ") + result.Message;
            _auditResult.ForeColor = result.Intact ? Palette.Green : Palette.Red;
            _log($"Audit trail: {result.Message}");
        }

        // ------------------------------------------------------------------ helpers

        private async Task Guard(string what, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (ApiException ex) when (ex.IsUnauthorized)
            {
                _follow?.Cancel();
                if (await _reauthenticate()) await StartAsync();
            }
            catch (ApiException ex) when (ex.StatusCode == 409)
            {
                // Someone else changed it first: show their version, then let the user decide again
                if (!IsDisposed) MessageBox.Show(this, ex.Message, what, MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (_ticket != null) await Guard("Reloading the ticket", () => ShowTicketAsync(_ticket.Id));
                await Guard("Reloading the to-do list", LoadTodosAsync);
            }
            catch (Exception ex) when (ex is ApiException or IOException or UnauthorizedAccessException or ExternalException or ArgumentException)
            {
                _log($"{what} failed: {ex.Message}");
                if (!_started)
                {
                    _banner.Text = "⚠ " + ex.Message + "   (click here to try again)";
                    _banner.Visible = true;
                }
                else if (!IsDisposed)
                {
                    MessageBox.Show(this, ex.Message, what, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private static Label Caption(string text) => new() { Text = text, AutoSize = true, ForeColor = Palette.Muted, Margin = new Padding(8, 8, 2, 0) };

        private static ListView NewList(params (string Title, int Width)[] columns)
        {
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None };
            foreach (var (title, width) in columns) list.Columns.Add(title, list.LogicalToDeviceUnits(width));
            return list;
        }

        private static TextBox TextView() => new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Palette.Card,
            Padding = new Padding(8),
            Font = new Font(FontFamily.GenericMonospace, 9.5f)
        };

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
    }
}
