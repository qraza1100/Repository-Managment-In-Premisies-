#nullable disable
using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using QualityAviationHelpdesk.Contracts;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    // The Helpdesk in the terminal: the same work as the Helpdesk tab of the Windows application
    // (queue, ticket steps, notes, files, editing, the to-do list, form fields, settings, audit).
    public class HelpdeskConsole
    {
        private readonly IHelpdeskClient _helpdesk;
        private ItMe _me;
        private List<FieldDefinitionDto> _fields = new();
        private string _status = "open";
        private string _assignee;
        private string _search;

        public HelpdeskConsole(IHelpdeskClient helpdesk)
        {
            _helpdesk = helpdesk;
        }

        public async Task RunAsync()
        {
            _me = await _helpdesk.MeAsync();
            _fields = await _helpdesk.FieldsAsync();

            while (true)
            {
                var page = await _helpdesk.TicketsAsync(_status, _assignee, _search, 1, 40);
                PrintQueue(page);

                var choice = Prompt("➤ Ticket number, or F/T/S/A/R, 0 to go back: ").ToUpperInvariant();
                if (choice is "0" or "") return;

                try
                {
                    if (int.TryParse(choice, out var row) && row >= 1 && row <= page.Items.Count) await TicketMenuAsync(page.Items[row - 1].Id);
                    else if (TicketNumbers.TryParse(choice, out var id) && choice.StartsWith("QA", StringComparison.OrdinalIgnoreCase)) await TicketMenuAsync(id);
                    else if (choice == "F") ChooseFilter();
                    else if (choice == "T") await TodoMenuAsync();
                    else if (choice == "S") await FieldsMenuAsync();
                    else if (choice == "A" && _me.IsLead) await AuditAsync();
                    else if (choice != "R") Write("❌ Invalid option", ConsoleColor.Red);
                }
                catch (ApiException ex) when (!ex.IsUnauthorized)
                {
                    Write($"❌ {ex.Message}", ConsoleColor.Red);
                }
            }
        }

        // ------------------------------------------------------------------ queue

        private void PrintQueue(TicketPage page)
        {
            var open = TicketStatus.Open.Sum(s => page.Counts.GetValueOrDefault(s));
            Header("🎫 HELPDESK", $"{_me.Username}{(_me.IsLead ? " (IT lead)" : "")} · {_helpdesk.Address} · {open} open · " +
                                 $"{page.Counts.GetValueOrDefault(TicketStatus.New)} new · {page.Counts.GetValueOrDefault("overdue")} overdue");
            Write($"   Showing: {_status}{(_assignee != null ? $", with {_assignee}" : "")}{(string.IsNullOrEmpty(_search) ? "" : $", matching \"{_search}\"")} · {page.Total} ticket(s)", ConsoleColor.DarkGray);

            if (page.Items.Count == 0)
            {
                Write("   Nothing here.", ConsoleColor.DarkGray);
            }
            else
            {
                Write($"  {"#",-4}{"Ticket",-9}{"P",-4}{"Status",-13}{"With",-12}{"Raised",-8}{"Target",-11}Summary", ConsoleColor.DarkGray);
                for (var i = 0; i < page.Items.Count; i++)
                {
                    var t = page.Items[i];
                    var color = t.Overdue ? ConsoleColor.Red : t.Status == TicketStatus.New ? ConsoleColor.Cyan : ConsoleColor.Gray;
                    Write($"  {i + 1,-4}{t.Number,-9}{"P" + t.Priority,-4}{Cut(TicketStatus.Label(t.Status), 12),-13}{Cut(t.Assignee ?? "—", 11),-12}{HelpdeskText.Ago(t.CreatedAt),-8}" +
                          $"{(TicketStatus.IsOpen(t.Status) ? HelpdeskText.Due(t.DueAt) : ""),-11}{Cut(t.Summary, 60)}  ({Cut(t.RequesterName, 20)}, {t.Department})", color);
                }
            }

            System.Console.WriteLine();
            System.Console.WriteLine("  F  Filter / search   T  To-do list   S  Form fields & settings" + (_me.IsLead ? "   A  Audit trail" : "") + "   R  Refresh   0  Back");
        }

        private void ChooseFilter()
        {
            Write("  Status: open, overdue, new, accepted, in-progress, on-hold, resolved, closed, rejected, cancelled, all", ConsoleColor.DarkGray);
            var status = Ask("Status", _status).ToLowerInvariant();
            if (status == "open" || status == "overdue" || status == "all" || TicketStatus.All.Contains(status)) _status = status;
            else Write("❌ Unknown status, kept " + _status, ConsoleColor.Red);

            Write($"  With: me, none, anyone, or one of {string.Join(", ", _me.Members)}", ConsoleColor.DarkGray);
            var assignee = Ask("With", _assignee ?? "anyone");
            _assignee = assignee.ToLowerInvariant() == "anyone" ? null : assignee;
            _search = Ask("Search (QA-number, words; - to clear)", _search);
            if (_search == "-") _search = null;
        }

        // ------------------------------------------------------------------ one ticket

        private async Task TicketMenuAsync(int id)
        {
            while (true)
            {
                var t = await _helpdesk.TicketAsync(id);
                PrintTicket(t);

                var keys = new Dictionary<string, string>
                {
                    ["A"] = TicketActions.Accept, ["J"] = TicketActions.Reject, ["S"] = TicketActions.Start, ["H"] = TicketActions.Hold, ["V"] = TicketActions.Resolve,
                    ["C"] = TicketActions.Close, ["O"] = TicketActions.Reopen, ["G"] = TicketActions.Assign, ["K"] = TicketActions.Take, ["P"] = TicketActions.Priority
                };
                var offered = keys.Where(k => t.AllowedActions.Contains(k.Value)).ToList();
                System.Console.WriteLine();
                System.Console.WriteLine("  " + string.Join("   ", offered.Select(k => $"{k.Key}  {TicketActions.Label(k.Value)}")));
                System.Console.WriteLine("  N  Note / message   L  Log time   E  Edit details   F  Save a file   0  Back");

                var choice = Prompt("➤ ").ToUpperInvariant();
                if (choice is "0" or "") return;

                try
                {
                    if (keys.TryGetValue(choice, out var action) && t.AllowedActions.Contains(action)) await ActAsync(t, action);
                    else if (choice == "N") await NoteAsync(t);
                    else if (choice == "L") await LogTimeAsync(t);
                    else if (choice == "E") await EditAsync(t);
                    else if (choice == "F") await SaveFileAsync(t);
                    else Write("❌ Invalid option", ConsoleColor.Red);
                }
                catch (ApiException ex) when (!ex.IsUnauthorized)
                {
                    Write($"❌ {ex.Message}", ConsoleColor.Red);
                }
            }
        }

        private void PrintTicket(TicketDetail t)
        {
            Header($"{t.Number}  {t.Summary}", $"{TicketStatus.Label(t.Status)} · {Priorities.Label(t.Priority)} · {t.Type} · with {t.Assignee ?? "nobody yet"}" +
                                               (TicketStatus.IsOpen(t.Status) ? $" · target {HelpdeskText.Due(t.DueAt)}" : "") + (t.MinutesSpent > 0 ? $" · {t.MinutesSpent} min logged" : ""));
            Write($"  Raised by {t.RequesterName} ({t.RequesterUserName}) · {t.Department} · {t.RequesterEmail ?? "no e-mail"} · {HelpdeskText.When(t.CreatedAt)}", ConsoleColor.DarkGray);
            System.Console.WriteLine();
            foreach (var line in t.Description.Split('\n')) System.Console.WriteLine("  " + line.TrimEnd('\r'));
            if (!string.IsNullOrEmpty(t.StatusReason)) Write($"\n  Reason / waiting for: {t.StatusReason}", ConsoleColor.Yellow);
            if (!string.IsNullOrEmpty(t.Resolution)) Write($"\n  Resolution: {t.Resolution}", ConsoleColor.Green);

            if (t.Fields.Count > 0)
            {
                System.Console.WriteLine();
                foreach (var field in _fields.Where(f => t.Fields.ContainsKey(f.Key)).OrderBy(f => f.SortOrder))
                    System.Console.WriteLine($"  {field.Label}{(field.Audience == FieldAudience.It ? " (IT)" : "")}: {t.Fields[field.Key]}");
            }

            if (t.Attachments.Count > 0)
            {
                Write("\n  Files:", ConsoleColor.DarkGray);
                for (var i = 0; i < t.Attachments.Count; i++)
                {
                    var file = t.Attachments[i];
                    System.Console.WriteLine($"   {i + 1}. {file.FileName} ({HelpdeskText.Size(file.SizeBytes)}, {file.UploadedByName}{(file.Internal ? ", IT only" : "")})");
                }
            }

            var recent = t.Comments.TakeLast(8).ToList();
            if (recent.Count > 0)
            {
                Write($"\n  Conversation (last {recent.Count} of {t.Comments.Count}):", ConsoleColor.DarkGray);
                foreach (var c in recent)
                {
                    Write($"  ── {c.AuthorName} · {HelpdeskText.When(c.CreatedAt)}{(c.Internal ? " · internal" : "")}{(c.MinutesSpent > 0 ? $" · {c.MinutesSpent} min" : "")}",
                        c.AuthorKind == "it" ? ConsoleColor.Cyan : ConsoleColor.White);
                    foreach (var line in c.Body.Split('\n')) System.Console.WriteLine("     " + line.TrimEnd('\r'));
                }
            }
        }

        private async Task ActAsync(TicketDetail t, string action)
        {
            var request = new TicketActionRequest { Version = t.Version };
            switch (action)
            {
                case TicketActions.Assign:
                    Write($"  IT team: {string.Join(", ", _me.Members)}", ConsoleColor.DarkGray);
                    var who = Ask("Give it to (- for nobody)", t.Assignee);
                    request.Assignee = who is null or "-" ? null : who;
                    break;
                case TicketActions.Priority:
                    if (!int.TryParse(Ask("Priority 1-4 (1 = critical)", t.Priority.ToString()), out var priority) || priority is < 1 or > 4)
                    {
                        Write("❌ Priority is 1 to 4", ConsoleColor.Red);
                        return;
                    }
                    request.Priority = priority;
                    break;
                case TicketActions.Start:
                case TicketActions.Take:
                    break;
                case TicketActions.Close:
                    if (Prompt($"  Close {t.Number} without the requester's confirmation? (y/n): ").ToLowerInvariant() != "y") return;
                    break;
                default:
                    var required = Workflow(action);
                    request.Note = ReadText(required ? $"  {NoteLabel(action)} (empty line ends):" : "  Note for IT, optional (empty line ends):");
                    if (required && string.IsNullOrWhiteSpace(request.Note))
                    {
                        Write("❌ A note is required for this step.", ConsoleColor.Red);
                        return;
                    }
                    if (action is TicketActions.Resolve or TicketActions.Accept) request.MinutesSpent = ReadMinutes();
                    break;
            }

            await _helpdesk.ActAsync(t.Id, action, request);
            Write($"✅ {t.Number}: {TicketActions.Label(action)}", ConsoleColor.Green);
        }

        private static bool Workflow(string action) => action is TicketActions.Resolve or TicketActions.Reject or TicketActions.Hold or TicketActions.Reopen;

        private static string NoteLabel(string action) => action switch
        {
            TicketActions.Resolve => "What was done (the requester reads this)",
            TicketActions.Reject => "Why it is rejected (the requester reads this)",
            TicketActions.Hold => "What it is waiting for (the requester reads this)",
            TicketActions.Reopen => "Why it is reopened",
            _ => "Note"
        };

        private async Task NoteAsync(TicketDetail t)
        {
            var text = ReadText("  Message (empty line ends):");
            var visible = Prompt("  Show it to the requester? (y = yes, otherwise only IT sees it): ").ToLowerInvariant() == "y";
            var files = Prompt("  Files to attach (paths separated by ;, empty for none): ")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Trim('"')).ToList();
            var minutes = ReadMinutes();
            if (string.IsNullOrWhiteSpace(text) && files.Count == 0 && minutes == 0) return;

            await _helpdesk.CommentAsync(t.Id, text, internalOnly: !visible, minutes, files);
            Write($"✅ {(visible ? "Message sent to the requester" : "Internal note added")}", ConsoleColor.Green);
        }

        private async Task LogTimeAsync(TicketDetail t)
        {
            var minutes = ReadMinutes();
            if (minutes == 0) return;
            var text = Prompt("  What you did (optional): ");
            await _helpdesk.CommentAsync(t.Id, text, internalOnly: true, minutes, null);
            Write($"✅ {minutes} min logged on {t.Number}", ConsoleColor.Green);
        }

        private async Task EditAsync(TicketDetail t)
        {
            var settings = _me.Settings;
            Write("  Press Enter to keep a value. Ticket types: " + string.Join(", ", settings.TicketTypes), ConsoleColor.DarkGray);
            var request = new TicketEditRequest
            {
                Version = t.Version,
                Type = Ask("Type", t.Type),
                Summary = Ask("Summary", t.Summary),
                Priority = int.TryParse(Ask("Priority 1-4", t.Priority.ToString()), out var p) ? p : t.Priority,
                Description = t.Description
            };
            if (Prompt("  Change the description? (y/n): ").ToLowerInvariant() == "y")
                request.Description = ReadText("  New description (empty line ends):");

            foreach (var field in _fields.Where(f => f.Active).OrderBy(f => f.SortOrder))
            {
                t.Fields.TryGetValue(field.Key, out var current);
                var hint = field.Kind switch
                {
                    FieldKinds.Select => $" one of: {string.Join(" / ", field.Options)}",
                    FieldKinds.MultiSelect => $" any of (separate with |): {string.Join(" / ", field.Options)}",
                    FieldKinds.Checkbox => " (yes/no)",
                    FieldKinds.Date => " (yyyy-mm-dd)",
                    _ => ""
                };
                var answer = Ask($"{field.Label}{(field.Audience == FieldAudience.It ? " [IT]" : "")}{hint} (- to clear)", current);
                request.Fields[field.Key] = answer == "-" ? "" : field.Kind == FieldKinds.Checkbox ? (answer?.ToLowerInvariant() is "yes" or "y" or "true" ? "true" : "") : answer ?? "";
            }

            await _helpdesk.EditAsync(t.Id, request);
            Write($"✅ {t.Number} saved", ConsoleColor.Green);
        }

        private async Task SaveFileAsync(TicketDetail t)
        {
            if (t.Attachments.Count == 0)
            {
                Write("  This ticket has no files.", ConsoleColor.DarkGray);
                return;
            }
            if (!int.TryParse(Prompt("  File number: "), out var number) || number < 1 || number > t.Attachments.Count) return;
            var file = t.Attachments[number - 1];
            var folder = Prompt($"  Folder to save {file.FileName} in: ");
            if (!Directory.Exists(folder))
            {
                Write("❌ Folder not found.", ConsoleColor.Red);
                return;
            }
            var target = Path.Combine(folder, file.FileName);
            await _helpdesk.SaveAttachmentAsync(file.Id, target);
            Write($"✅ Saved to {target}. Files from requesters are not opened automatically; open it only if you trust it.", ConsoleColor.Green);
        }

        // ------------------------------------------------------------------ to-do

        private async Task TodoMenuAsync()
        {
            var showDone = false;
            while (true)
            {
                var todos = await _helpdesk.TodosAsync(showDone, archived: false);
                Header("✅ IT TO-DO", $"{todos.Count(t => t.Status != TodoStatus.Done)} open · {todos.Count(t => t.Status == TodoStatus.Doing)} being done · {todos.Count(t => t.Status == TodoStatus.Blocked)} blocked");
                var ordered = TodoStatus.All.SelectMany(s => todos.Where(t => t.Status == s)).ToList();
                string group = null;
                for (var i = 0; i < ordered.Count; i++)
                {
                    var t = ordered[i];
                    if (t.Status != group)
                    {
                        group = t.Status;
                        Write($"  {TodoStatus.Label(group).ToUpperInvariant()}", ConsoleColor.DarkGray);
                    }
                    var color = t.Status switch { TodoStatus.Done => ConsoleColor.DarkGray, TodoStatus.Blocked => ConsoleColor.Red, TodoStatus.Doing => ConsoleColor.Cyan, _ => ConsoleColor.Gray };
                    Write($"  {i + 1,-4}{Cut(t.Title, 50),-52}{Cut(t.Owner ?? "—", 12),-13}P{t.Priority}  {t.DueOn?.ToString("yyyy-MM-dd") ?? "",-11}{t.TicketNumber ?? "",-9}{t.UpdatedBy} {HelpdeskText.Ago(t.UpdatedAt)}", color);
                }
                if (ordered.Count == 0) Write("   The list is empty.", ConsoleColor.DarkGray);

                System.Console.WriteLine();
                System.Console.WriteLine($"  N  New to-do   D  {(showDone ? "Hide" : "Show")} older done items   R  Refresh   0  Back");
                var choice = Prompt("➤ Number, or N/D/R/0: ").ToUpperInvariant();
                if (choice is "0" or "") return;

                try
                {
                    if (choice == "N") await EditTodoAsync(null);
                    else if (choice == "D") showDone = !showDone;
                    else if (int.TryParse(choice, out var row) && row >= 1 && row <= ordered.Count) await TodoItemMenuAsync(ordered[row - 1]);
                    else if (choice != "R") Write("❌ Invalid option", ConsoleColor.Red);
                }
                catch (ApiException ex) when (!ex.IsUnauthorized)
                {
                    Write($"❌ {ex.Message}", ConsoleColor.Red);
                }
            }
        }

        private async Task TodoItemMenuAsync(TodoDto todo)
        {
            Write($"\n  {todo.Title}", ConsoleColor.White);
            if (!string.IsNullOrEmpty(todo.Notes)) System.Console.WriteLine("  " + todo.Notes.Replace("\n", "\n  "));
            Write($"  {TodoStatus.Label(todo.Status)} · {todo.Owner ?? "nobody"} · {Priorities.Label(todo.Priority)} · added by {todo.CreatedBy} {HelpdeskText.Ago(todo.CreatedAt)} ago", ConsoleColor.DarkGray);
            System.Console.WriteLine("  1 To do   2 Doing   3 Blocked   4 Done   G Give to…   E Edit   A Archive   H History   0 Back");

            var choice = Prompt("➤ ").ToUpperInvariant();
            var request = new TodoSaveRequest
            {
                Version = todo.Version, Title = todo.Title, Notes = todo.Notes, Owner = todo.Owner, Status = todo.Status, Priority = todo.Priority, DueOn = todo.DueOn, TicketId = todo.TicketId
            };

            switch (choice)
            {
                case "1" or "2" or "3" or "4":
                    request.Status = TodoStatus.All[int.Parse(choice) - 1];
                    if (request.Status == TodoStatus.Doing && request.Owner == null) request.Owner = _me.Username;
                    break;
                case "G":
                    Write($"  IT team: {string.Join(", ", _me.Members)}", ConsoleColor.DarkGray);
                    var who = Ask("Give it to (- for nobody)", todo.Owner);
                    request.Owner = who is null or "-" ? null : who;
                    break;
                case "E":
                    await EditTodoAsync(todo);
                    return;
                case "A":
                    if (Prompt("  Archive it? It stays in the record. (y/n): ").ToLowerInvariant() != "y") return;
                    await _helpdesk.ArchiveTodoAsync(todo.Id, todo.Version, restore: false);
                    Write("✅ Archived", ConsoleColor.Green);
                    return;
                case "H":
                    foreach (var h in await _helpdesk.TodoHistoryAsync(todo.Id))
                        System.Console.WriteLine($"  {HelpdeskText.When(h.At)}  {h.ActorName,-14} {h.Action,-14} {h.Detail}");
                    Pause();
                    return;
                default:
                    return;
            }

            await _helpdesk.SaveTodoAsync(todo.Id, request);
            Write("✅ Saved", ConsoleColor.Green);
        }

        private async Task EditTodoAsync(TodoDto todo)
        {
            var request = new TodoSaveRequest
            {
                Version = todo?.Version,
                Title = Ask("What", todo?.Title),
                Notes = Ask("Notes", todo?.Notes),
                Owner = Ask($"Who ({string.Join(", ", _me.Members)}; - for nobody)", todo?.Owner ?? _me.Username),
                Status = todo?.Status ?? TodoStatus.Todo,
                Priority = int.TryParse(Ask("Priority 1-4", (todo?.Priority ?? 3).ToString()), out var p) ? p : 3,
                DueOn = DateOnly.TryParse(Ask("Due (yyyy-mm-dd, - for none)", todo?.DueOn?.ToString("yyyy-MM-dd")), out var due) ? due : null,
                TicketId = TicketNumbers.TryParse(Ask("Ticket (QA-number, - for none)", todo?.TicketNumber), out var ticket) ? ticket : null
            };
            if (request.Owner == "-") request.Owner = null;
            await _helpdesk.SaveTodoAsync(todo?.Id ?? 0, request);
            Write("✅ Saved", ConsoleColor.Green);
        }

        // ------------------------------------------------------------------ form fields and settings

        private async Task FieldsMenuAsync()
        {
            while (true)
            {
                _fields = await _helpdesk.FieldsAsync();
                var settings = await _helpdesk.SettingsAsync();
                Header("🧩 FORM FIELDS & SETTINGS", _me.IsLead ? "You can change these (IT lead)" : "Only IT leads can change these");
                var ordered = _fields.OrderBy(f => f.SortOrder).ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var f = ordered[i];
                    Write($"  {i + 1,-4}{Cut(f.Label, 30),-32}{f.Key,-18}{f.Kind,-12}{(f.Audience == FieldAudience.It ? "IT" : "requester"),-11}{(f.Required ? "required" : ""),-10}" +
                          $"{(f.Active ? "" : "hidden"),-8}{(f.AppliesTo.Count == 0 ? "all types" : string.Join(", ", f.AppliesTo))}", f.Active ? ConsoleColor.Gray : ConsoleColor.DarkGray);
                }
                System.Console.WriteLine();
                Write($"  Types: {string.Join(", ", settings.TicketTypes)}", ConsoleColor.DarkGray);
                Write($"  Targets: {string.Join(" · ", settings.SlaHours.Select((h, i) => $"P{i + 1} {h} h"))} · reopen {settings.ReopenDays} d · auto-close {settings.AutoCloseDays} d · files {settings.MaxAttachmentMB} MB", ConsoleColor.DarkGray);
                System.Console.WriteLine();
                if (!_me.IsLead)
                {
                    Pause();
                    return;
                }

                System.Console.WriteLine("  N  New field   number  Edit that field   U<n>/D<n>  Move up/down   S  Settings   0  Back");
                var choice = Prompt("➤ ").ToUpperInvariant();
                if (choice is "0" or "") return;

                try
                {
                    if (choice == "N") await EditFieldAsync(null, settings);
                    else if (choice == "S") await EditSettingsAsync(settings);
                    else if (choice.Length > 1 && (choice[0] == 'U' || choice[0] == 'D') && int.TryParse(choice[1..], out var move) && move >= 1 && move <= ordered.Count)
                        _fields = await _helpdesk.MoveFieldAsync(ordered[move - 1].Id, choice[0] == 'U' ? -1 : 1);
                    else if (int.TryParse(choice, out var row) && row >= 1 && row <= ordered.Count) await EditFieldAsync(ordered[row - 1], settings);
                    else Write("❌ Invalid option", ConsoleColor.Red);
                }
                catch (ApiException ex) when (!ex.IsUnauthorized)
                {
                    Write($"❌ {ex.Message}", ConsoleColor.Red);
                }
            }
        }

        private async Task EditFieldAsync(FieldDefinitionDto field, HelpdeskSettings settings)
        {
            var isNew = field == null;
            field ??= new FieldDefinitionDto { Kind = FieldKinds.Text, Audience = FieldAudience.Requester, MaxLength = 500, Active = true };
            var edited = new FieldDefinitionDto { Id = field.Id, Version = field.Version, SortOrder = field.SortOrder };

            edited.Label = Ask("Label", field.Label);
            edited.Key = isNew ? Ask("Key (lower-case letters, digits, _)", field.Key) : field.Key;
            edited.Kind = Ask($"Kind ({string.Join(", ", FieldKinds.All)})", field.Kind);
            edited.Options = FieldKinds.HasOptions(edited.Kind)
                ? (Ask("Options (separate with |)", string.Join("|", field.Options)) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : new();
            edited.Audience = Ask("Filled in by (requester / it)", field.Audience)?.ToLowerInvariant() == "it" ? FieldAudience.It : FieldAudience.Requester;
            Write($"  Types: {string.Join(", ", settings.TicketTypes)}", ConsoleColor.DarkGray);
            var applies = Ask("Only for these types (separate with |; - for all)", string.Join("|", field.AppliesTo));
            edited.AppliesTo = applies is null or "-" ? new() : applies.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            edited.HelpText = Ask("Help text", field.HelpText);
            edited.MaxLength = int.TryParse(Ask("Max. length", field.MaxLength.ToString()), out var max) ? max : field.MaxLength;
            edited.Required = Ask("Required (y/n)", field.Required ? "y" : "n") == "y";
            edited.Active = Ask("Shown (y/n)", field.Active ? "y" : "n") == "y";

            _fields = await _helpdesk.SaveFieldAsync(edited);
            Write($"✅ Field \"{edited.Label}\" saved; the web form shows it now", ConsoleColor.Green);
        }

        private async Task EditSettingsAsync(HelpdeskSettings settings)
        {
            var edited = new HelpdeskSettings
            {
                TicketTypes = Ask("Ticket types (separate with |)", string.Join("|", settings.TicketTypes)).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                SlaHours = Ask("Targets in hours P1,P2,P3,P4", string.Join(",", settings.SlaHours)).Split(',').Select(h => int.TryParse(h.Trim(), out var v) ? v : 0).ToArray(),
                ReopenDays = int.TryParse(Ask("Requester may reopen for (days)", settings.ReopenDays.ToString()), out var reopen) ? reopen : settings.ReopenDays,
                AutoCloseDays = int.TryParse(Ask("Close resolved tickets after (days, 0 = never)", settings.AutoCloseDays.ToString()), out var close) ? close : settings.AutoCloseDays,
                MaxAttachmentMB = int.TryParse(Ask("Largest file (MB)", settings.MaxAttachmentMB.ToString()), out var mb) ? mb : settings.MaxAttachmentMB
            };
            _me.Settings = await _helpdesk.SaveSettingsAsync(edited);
            Write("✅ Settings saved", ConsoleColor.Green);
        }

        // ------------------------------------------------------------------ audit

        private async Task AuditAsync()
        {
            Header("🔏 AUDIT TRAIL", "Every change, chained by SHA-256");
            foreach (var e in (await _helpdesk.AuditAsync(40)).AsEnumerable().Reverse())
                System.Console.WriteLine($"  {HelpdeskText.When(e.At)}  {Cut(e.ActorName, 14),-15}{Cut(e.Action, 16),-17}{e.EntityType} {e.EntityId,-8} {Cut(e.Detail ?? "", 70)}");

            if (Prompt("\n  Verify the whole chain now? (y/n): ").ToLowerInvariant() == "y")
            {
                var result = await _helpdesk.VerifyAuditAsync();
                Write((result.Intact ? "✅ " : "⚠️  ") + result.Message, result.Intact ? ConsoleColor.Green : ConsoleColor.Red);
                Pause();
            }
        }

        // ------------------------------------------------------------------ console helpers

        private static int ReadMinutes()
        {
            var answer = Prompt("  Time spent in minutes (empty = none): ");
            return int.TryParse(answer, out var minutes) && minutes is > 0 and <= 1440 ? minutes : 0;
        }

        private static string ReadText(string label)
        {
            System.Console.WriteLine(label);
            var lines = new List<string>();
            while (true)
            {
                var line = System.Console.ReadLine() ?? throw new EndOfStreamException();
                if (line.Length == 0) break;
                lines.Add(line);
            }
            return string.Join("\n", lines).Trim();
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

        private static void Pause() => Prompt("  Press Enter to continue...");

        private static string Cut(string text, int length) => string.IsNullOrEmpty(text) ? "" : text.Length <= length ? text : text[..(length - 1)] + "…";
    }
}
