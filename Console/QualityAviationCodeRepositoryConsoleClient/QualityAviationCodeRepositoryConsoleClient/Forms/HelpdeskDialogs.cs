#nullable disable
using QualityAviationHelpdesk.Contracts;
using System.Globalization;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Forms
{
    // The Helpdesk tab's dialogs. Each one builds its rows from what it edits (a ticket's form fields come from the
    // field definitions, so a field added at run time appears here at once) and sizes itself for any display scaling.
    internal static class HelpdeskDialogs
    {
        public sealed record NoteResult(string Text, int Minutes, bool Visible, List<string> Files);

        // A multi-line note, optionally with time spent, "show to the requester" and files
        public static NoteResult Note(IWin32Window owner, string title, string prompt, bool required, bool askMinutes = false, bool askVisibility = false,
            bool askFiles = false, bool visibleByDefault = false, bool minutesRequired = false)
        {
            using var form = new DialogBuilder(title);
            var text = form.Add(prompt, new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, MaxLength = 4000 }, height: 110);
            NumericUpDown minutes = null;
            CheckBox visible = null;
            ListBox files = null;
            if (askMinutes) minutes = form.Add("Time spent (minutes):", new NumericUpDown { Minimum = 0, Maximum = 1440, Increment = 15, Width = 90 }, fill: false);
            if (askVisibility) visible = form.Add("", new CheckBox { Text = "Show this to the requester (otherwise only IT sees it)", AutoSize = true, Checked = visibleByDefault }, fill: false);
            if (askFiles)
            {
                files = form.Add("Files:", new ListBox { HorizontalScrollbar = true }, height: 60);
                var buttons = form.Add("", new FlowLayoutPanel { AutoSize = true, WrapContents = false }, fill: false);
                var add = new Button { Text = "Add files…", AutoSize = true };
                var remove = new Button { Text = "Remove", AutoSize = true };
                add.Click += (_, _) =>
                {
                    using var dialog = new OpenFileDialog { Multiselect = true, Title = "Attach files", Filter = "Allowed files|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.pdf;*.docx;*.xlsx;*.pptx;*.zip;*.doc;*.xls;*.msg;*.txt;*.log;*.csv;*.eml" };
                    if (dialog.ShowDialog(form.Form) == DialogResult.OK)
                        foreach (var name in dialog.FileNames.Where(n => !files.Items.Contains(n))) files.Items.Add(name);
                };
                remove.Click += (_, _) => { if (files.SelectedItem != null) files.Items.Remove(files.SelectedItem); };
                buttons.Controls.Add(add);
                buttons.Controls.Add(remove);
            }

            var ok = form.Show(owner, () =>
            {
                if (required && string.IsNullOrWhiteSpace(text.Text)) return $"{prompt.TrimEnd(':')} is required.";
                if (minutesRequired && minutes?.Value == 0) return "Enter the time spent.";
                if (files?.Items.Count > 5) return "Attach at most 5 files at a time.";
                return null;
            });
            if (!ok) return null;

            return new NoteResult(text.Text.Trim(), (int)(minutes?.Value ?? 0), visible?.Checked ?? false, files?.Items.Cast<string>().ToList() ?? new());
        }

        // The ticket as IT may correct it: type, summary, description, priority and every active form field
        public static TicketEditRequest EditTicket(IWin32Window owner, TicketDetail ticket, HelpdeskSettings settings, List<FieldDefinitionDto> fields)
        {
            using var form = new DialogBuilder($"Edit {ticket.Number}", scroll: true);
            var type = form.Add("Type:", Combo(settings.TicketTypes, ticket.Type));
            var priority = form.Add("Priority:", Combo(Priorities.All.Select(Priorities.Label), Priorities.Label(ticket.Priority)));
            var summary = form.Add("Summary:", new TextBox { Text = ticket.Summary, MaxLength = 200 });
            var description = form.Add("Description:", new TextBox { Text = ticket.Description, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, MaxLength = 8000 }, height: 120);

            var inputs = new List<(FieldDefinitionDto Field, Control Input)>();
            foreach (var field in fields.Where(f => f.Active).OrderBy(f => f.SortOrder))
            {
                ticket.Fields.TryGetValue(field.Key, out var value);
                var label = field.Label + (field.Required ? " *" : "") + ":";
                var hint = (field.Audience == FieldAudience.It ? "IT only" : "requester") + (field.AppliesTo.Count > 0 ? $" · for {string.Join(", ", field.AppliesTo)}" : "");
                Control input = field.Kind switch
                {
                    FieldKinds.TextArea => new TextBox { Text = value ?? "", Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, MaxLength = field.MaxLength },
                    FieldKinds.Select => Combo(new[] { "" }.Concat(field.Options), value ?? ""),
                    FieldKinds.MultiSelect => MultiChoice(field.Options, value),
                    FieldKinds.Checkbox => new CheckBox { Text = field.Label, AutoSize = true, Checked = value == "Yes" },
                    FieldKinds.Date => DateChoice(value),
                    _ => new TextBox { Text = value ?? "", MaxLength = field.MaxLength }
                };
                form.Add(label, input, height: field.Kind is FieldKinds.TextArea or FieldKinds.MultiSelect ? 70 : 0, hint: hint,
                    fill: field.Kind is not (FieldKinds.Checkbox or FieldKinds.Date));
                inputs.Add((field, input));
            }

            var ok = form.Show(owner, () =>
            {
                if (summary.Text.Trim().Length < 5) return "The summary needs at least 5 characters.";
                if (description.Text.Trim().Length < 10) return "The description needs at least 10 characters.";
                return null;
            });
            if (!ok) return null;

            var request = new TicketEditRequest
            {
                Version = ticket.Version,
                Type = type.Text,
                Summary = summary.Text.Trim(),
                Description = description.Text.Trim(),
                Priority = priority.SelectedIndex + 1
            };
            foreach (var (field, input) in inputs)
            {
                request.Fields[field.Key] = input switch
                {
                    CheckBox check => check.Checked ? "true" : "",
                    CheckedListBox list => string.Join("|", list.CheckedItems.Cast<string>()),
                    DateTimePicker date => date.Checked ? date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
                    _ => input.Text.Trim()
                };
            }
            return request;
        }

        public static FieldDefinitionDto EditField(IWin32Window owner, FieldDefinitionDto field, HelpdeskSettings settings)
        {
            var isNew = field == null;
            field ??= new FieldDefinitionDto { Kind = FieldKinds.Text, Audience = FieldAudience.Requester, MaxLength = 500, Active = true };

            using var form = new DialogBuilder(isNew ? "Add a form field" : $"Edit field \"{field.Label}\"");
            var label = form.Add("Label:", new TextBox { Text = field.Label ?? "", MaxLength = 120 });
            var key = form.Add("Key:", new TextBox { Text = field.Key ?? "", MaxLength = 60, ReadOnly = !isNew },
                hint: isNew ? "lower-case letters, digits and _, e.g. contact_phone. Cannot be changed later." : "cannot be changed");
            var kinds = new[] { (FieldKinds.Text, "Text, one line"), (FieldKinds.TextArea, "Text, several lines"), (FieldKinds.Number, "Number"), (FieldKinds.Date, "Date"),
                                (FieldKinds.Select, "Choice (one)"), (FieldKinds.MultiSelect, "Choice (several)"), (FieldKinds.Checkbox, "Tick box") };
            var kind = form.Add("Kind:", Combo(kinds.Select(k => k.Item2), kinds.FirstOrDefault(k => k.Item1 == field.Kind).Item2 ?? kinds[0].Item2));
            var options = form.Add("Options:", new TextBox { Text = string.Join(Environment.NewLine, field.Options), Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true }, height: 80,
                hint: "for choices: one option per line");
            var audience = form.Add("Filled in by:", Combo(new[] { "The requester (web form)", "IT (client only)" }, field.Audience == FieldAudience.It ? "IT (client only)" : "The requester (web form)"));
            var applies = form.Add("Ticket types:", MultiChoice(settings.TicketTypes, string.Join("|", field.AppliesTo)), height: 90, hint: "none ticked = every type");
            var help = form.Add("Help text:", new TextBox { Text = field.HelpText ?? "", MaxLength = 300 });
            var maxLength = form.Add("Max. length:", new NumericUpDown { Minimum = 1, Maximum = 4000, Value = Math.Clamp(field.MaxLength, 1, 4000), Width = 90 }, fill: false);
            var required = form.Add("", new CheckBox { Text = "Required", AutoSize = true, Checked = field.Required }, fill: false);
            var active = form.Add("", new CheckBox { Text = "Shown (untick to retire the field; its values are kept)", AutoSize = true, Checked = field.Active }, fill: false);

            if (isNew)
                label.TextChanged += (_, _) =>
                {
                    var slug = new string(label.Text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '_').ToArray()).Trim('_');
                    while (slug.Contains("__")) slug = slug.Replace("__", "_");
                    if (slug.Length > 0 && !char.IsLetter(slug[0])) slug = "f_" + slug;
                    key.Text = slug.Length > 60 ? slug[..60] : slug;
                };

            var ok = form.Show(owner, () =>
            {
                if (string.IsNullOrWhiteSpace(label.Text)) return "The label is required.";
                if (string.IsNullOrWhiteSpace(key.Text)) return "The key is required.";
                var chosenKind = kinds[kind.SelectedIndex].Item1;
                if (FieldKinds.HasOptions(chosenKind) && SplitLines(options.Text).Count == 0) return "A choice field needs at least one option.";
                return null;
            });
            if (!ok) return null;

            var chosen = kinds[kind.SelectedIndex].Item1;
            return new FieldDefinitionDto
            {
                Id = field.Id,
                Version = field.Version,
                Key = key.Text.Trim(),
                Label = label.Text.Trim(),
                Kind = chosen,
                Options = FieldKinds.HasOptions(chosen) ? SplitLines(options.Text) : new(),
                Audience = audience.SelectedIndex == 1 ? FieldAudience.It : FieldAudience.Requester,
                AppliesTo = ((CheckedListBox)applies).CheckedItems.Cast<string>().ToList(),
                HelpText = help.Text.Trim(),
                MaxLength = (int)maxLength.Value,
                Required = required.Checked,
                Active = active.Checked,
                SortOrder = field.SortOrder
            };
        }

        public static TodoSaveRequest EditTodo(IWin32Window owner, TodoDto todo, ItMe me)
        {
            var isNew = todo == null;
            todo ??= new TodoDto { Status = TodoStatus.Todo, Priority = 3, Owner = me.Username };
            var nobody = "(nobody yet)";

            using var form = new DialogBuilder(isNew ? "New to-do" : "Edit to-do");
            var title = form.Add("What:", new TextBox { Text = todo.Title ?? "", MaxLength = 200 });
            var notes = form.Add("Notes:", new TextBox { Text = todo.Notes ?? "", Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, MaxLength = 4000 }, height: 90);
            var owner2 = form.Add("Who:", Combo(new[] { nobody }.Concat(me.Members.Union(new[] { me.Username }, StringComparer.OrdinalIgnoreCase)), todo.Owner ?? nobody));
            var status = form.Add("Status:", Combo(TodoStatus.All.Select(TodoStatus.Label), TodoStatus.Label(todo.Status)));
            var priority = form.Add("Priority:", Combo(Priorities.All.Select(Priorities.Label), Priorities.Label(todo.Priority)));
            var due = form.Add("Due:", DateChoice(todo.DueOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), fill: false);
            var ticket = form.Add("Ticket:", new TextBox { Text = todo.TicketNumber ?? "", Width = 120 }, fill: false, hint: "optional, e.g. QA-1001");

            var ok = form.Show(owner, () =>
            {
                if (string.IsNullOrWhiteSpace(title.Text)) return "Say what needs doing.";
                if (ticket.Text.Trim().Length > 0 && !TicketNumbers.TryParse(ticket.Text, out _)) return "The ticket is a number like QA-1001.";
                return null;
            });
            if (!ok) return null;

            int? ticketId = TicketNumbers.TryParse(ticket.Text, out var parsed) ? parsed : null;
            var picker = (DateTimePicker)due;
            return new TodoSaveRequest
            {
                Version = todo.Version,
                Title = title.Text.Trim(),
                Notes = notes.Text.Trim(),
                Owner = owner2.Text == nobody ? null : owner2.Text,
                Status = TodoStatus.All[status.SelectedIndex],
                Priority = priority.SelectedIndex + 1,
                DueOn = picker.Checked ? DateOnly.FromDateTime(picker.Value) : null,
                TicketId = ticketId
            };
        }

        public static HelpdeskSettings EditSettings(IWin32Window owner, HelpdeskSettings settings)
        {
            using var form = new DialogBuilder("Helpdesk settings");
            var types = form.Add("Ticket types:", new TextBox { Text = string.Join(Environment.NewLine, settings.TicketTypes), Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true }, height: 120,
                hint: "one per line; shown on the web form in this order");
            var targets = new NumericUpDown[4];
            for (var i = 0; i < 4; i++)
                targets[i] = form.Add($"{Priorities.Label(i + 1)} target (hours):", new NumericUpDown { Minimum = 1, Maximum = 2000, Value = Math.Clamp(settings.SlaHours[i], 1, 2000), Width = 90 }, fill: false);
            var reopen = form.Add("Requester may reopen for (days):", new NumericUpDown { Minimum = 0, Maximum = 90, Value = Math.Clamp(settings.ReopenDays, 0, 90), Width = 90 }, fill: false);
            var autoClose = form.Add("Close resolved tickets after (days):", new NumericUpDown { Minimum = 0, Maximum = 90, Value = Math.Clamp(settings.AutoCloseDays, 0, 90), Width = 90 }, fill: false, hint: "0 = never");
            var maxMb = form.Add("Largest file (MB):", new NumericUpDown { Minimum = 1, Maximum = 50, Value = Math.Clamp(settings.MaxAttachmentMB, 1, 50), Width = 90 }, fill: false);

            var ok = form.Show(owner, () => SplitLines(types.Text).Count == 0 ? "Give at least one ticket type." : null);
            if (!ok) return null;

            return new HelpdeskSettings
            {
                TicketTypes = SplitLines(types.Text),
                SlaHours = targets.Select(t => (int)t.Value).ToArray(),
                ReopenDays = (int)reopen.Value,
                AutoCloseDays = (int)autoClose.Value,
                MaxAttachmentMB = (int)maxMb.Value
            };
        }

        public static void ShowText(IWin32Window owner, string title, string text)
        {
            using var form = new DialogBuilder(title);
            form.Add("", new TextBox { Text = text, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = SystemColors.Window }, height: 320);
            form.Show(owner, () => null, cancel: false);
        }

        public static void PreviewImage(IWin32Window owner, string title, string file)
        {
            using var stream = new MemoryStream(File.ReadAllBytes(file));
            using var image = Image.FromStream(stream);
            using var form = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                MinimizeBox = false
            };
            form.ClientSize = form.LogicalToDeviceUnits(new Size(Math.Clamp(image.Width, 320, 1200), Math.Clamp(image.Height, 240, 800)));
            form.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = image, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(30, 34, 40) });
            form.ShowDialog(owner);
        }

        private static ComboBox Combo(IEnumerable<string> items, string selected)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.AddRange(items.Cast<object>().ToArray());
            var index = combo.Items.IndexOf(selected ?? "");
            if (index < 0 && !string.IsNullOrEmpty(selected))
            {
                // A value no longer in the list (renamed type, removed option) stays choosable
                combo.Items.Add(selected);
                index = combo.Items.Count - 1;
            }
            combo.SelectedIndex = combo.Items.Count == 0 ? -1 : Math.Max(index, 0);
            return combo;
        }

        private static CheckedListBox MultiChoice(IEnumerable<string> options, string value)
        {
            var chosen = (value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var list = new CheckedListBox { CheckOnClick = true, IntegralHeight = false };
            foreach (var option in options) list.Items.Add(option, chosen.Contains(option, StringComparer.OrdinalIgnoreCase));
            return list;
        }

        private static DateTimePicker DateChoice(string value)
        {
            var picker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", ShowCheckBox = true, Width = 140 };
            if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                picker.Value = date;
                picker.Checked = true;
            }
            else
            {
                picker.Checked = false;
            }
            return picker;
        }

        private static List<string> SplitLines(string text) =>
            (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Length > 0).Distinct().ToList();

        // Label + input rows, OK/Cancel, validation that keeps the dialog open with a message
        private sealed class DialogBuilder : IDisposable
        {
            private readonly TableLayoutPanel _table;
            private readonly int _inputWidth;

            public Form Form { get; }

            public DialogBuilder(string title, bool scroll = false)
            {
                Form = new Form
                {
                    Text = title,
                    FormBorderStyle = scroll ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterParent,
                    MinimizeBox = false,
                    MaximizeBox = scroll,
                    ShowInTaskbar = false,
                    Padding = new Padding(12)
                };
                _inputWidth = Form.LogicalToDeviceUnits(420);
                _table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = scroll ? DockStyle.Top : DockStyle.Fill };
                _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

                if (scroll)
                {
                    var holder = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
                    holder.Controls.Add(_table);
                    Form.Controls.Add(holder);
                    Form.ClientSize = Form.LogicalToDeviceUnits(new Size(680, 640));
                }
                else
                {
                    Form.AutoSize = true;
                    Form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                    Form.Controls.Add(_table);
                }
            }

            public T Add<T>(string label, T input, int height = 0, string hint = null, bool fill = true) where T : Control
            {
                if (fill) input.Width = _inputWidth;
                if (height > 0) input.Height = Form.LogicalToDeviceUnits(height);
                input.Margin = new Padding(3, 4, 3, hint == null ? 4 : 0);

                _table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 7, 12, 0) });
                _table.Controls.Add(input);
                if (hint != null)
                {
                    _table.Controls.Add(new Label());
                    _table.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 6) });
                }
                return input;
            }

            public bool Show(IWin32Window owner, Func<string> validate, bool cancel = true)
            {
                var ok = new Button { Text = cancel ? "OK" : "Close", DialogResult = DialogResult.OK, AutoSize = true };
                var no = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 8, 0, 0) };
                if (cancel) buttons.Controls.Add(no);
                buttons.Controls.Add(ok);
                Form.Controls.Add(buttons);
                Form.AcceptButton = cancel ? null : ok; // Enter adds lines in the text boxes
                Form.CancelButton = cancel ? no : ok;

                Form.FormClosing += (_, e) =>
                {
                    if (Form.DialogResult != DialogResult.OK) return;
                    var problem = validate();
                    if (problem == null) return;
                    MessageBox.Show(Form, problem, Form.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    e.Cancel = true;
                };

                return Form.ShowDialog(owner) == DialogResult.OK;
            }

            public void Dispose() => Form.Dispose();
        }
    }
}
