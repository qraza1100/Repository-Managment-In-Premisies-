namespace QualityAviationCodeRepositoryConsoleClient.Console.Forms
{
    // One small dialog for every question the Windows application asks (login, repo name, message...).
    public class PromptField
    {
        public string Label { get; set; }
        public string Value { get; set; } = "";
        public bool IsPassword { get; set; }
        public bool Optional { get; set; }
        public string[] Choices { get; set; }
    }

    public static class PromptDialog
    {
        // Returns the entered values in field order, or null when the user cancels
        public static string[] Show(IWin32Window owner, string title, params PromptField[] fields)
        {
            // The dialog sizes itself around its contents, so it fits at any display scaling
            using var form = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12)
            };

            var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var inputWidth = form.LogicalToDeviceUnits(320);
            var inputs = new List<Control>();
            foreach (var field in fields)
            {
                Control input;
                if (field.Choices != null)
                {
                    var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                    combo.Items.AddRange(field.Choices);
                    if (combo.Items.Count > 0) combo.SelectedIndex = 0;
                    input = combo;
                }
                else
                {
                    input = new TextBox { Text = field.Value, UseSystemPasswordChar = field.IsPassword };
                }
                input.Width = inputWidth;
                input.Margin = new Padding(3, 4, 3, 4);

                layout.Controls.Add(new Label { Text = field.Label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 12, 0) });
                layout.Controls.Add(input);
                inputs.Add(input);
            }

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, Margin = new Padding(3, 10, 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            layout.Controls.Add(buttons);
            layout.SetColumnSpan(buttons, 2);

            form.Controls.Add(layout);
            form.AcceptButton = ok;
            form.CancelButton = cancel;

            // Keep the dialog open until every required field has a value
            form.FormClosing += (_, e) =>
            {
                if (form.DialogResult != DialogResult.OK) return;

                for (var i = 0; i < fields.Length; i++)
                {
                    if (fields[i].Optional || !string.IsNullOrWhiteSpace(inputs[i].Text)) continue;

                    MessageBox.Show(form, $"{fields[i].Label.TrimEnd(':')} is required.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    inputs[i].Focus();
                    e.Cancel = true;
                    return;
                }
            };

            return form.ShowDialog(owner) == DialogResult.OK ? inputs.Select(i => i.Text).ToArray() : null;
        }
    }
}
