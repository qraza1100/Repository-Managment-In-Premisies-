using QualityAviationRepositoryShared;
using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Forms
{
    // Colours shared by the deployment screens
    public static class Palette
    {
        public static readonly Color Background = Color.FromArgb(246, 248, 250);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(222, 226, 232);
        public static readonly Color Text = Color.FromArgb(31, 41, 51);
        public static readonly Color Muted = Color.FromArgb(97, 110, 124);
        public static readonly Color Faint = Color.FromArgb(203, 210, 217);
        public static readonly Color Green = Color.FromArgb(34, 160, 107);
        public static readonly Color Amber = Color.FromArgb(217, 130, 43);
        public static readonly Color Red = Color.FromArgb(214, 69, 69);
        public static readonly Color Blue = Color.FromArgb(45, 127, 249);
        public static readonly Color Selected = Color.FromArgb(232, 241, 255);

        public static Color ForStage(string status) => status switch
        {
            DeployStage.Succeeded => Green,
            DeployStage.Warning => Amber,
            DeployStage.Failed => Red,
            DeployStage.Running => Blue,
            DeployStage.Skipped => Faint,
            _ => Color.FromArgb(160, 168, 180)
        };

        public static Color ForState(string state) => state switch
        {
            "running" => Green,
            "starting" => Blue,
            "unhealthy" => Amber,
            "crashed" => Red,
            _ => Muted
        };

        public static Color ForRun(string status) => status switch
        {
            DeployRun.Succeeded => Green,
            DeployRun.Failed => Red,
            DeployRun.Running or DeployRun.Queued => Blue,
            _ => Muted
        };
    }

    // The pipeline of one run as a row of connected nodes: each stage is a circle coloured by its outcome,
    // with its name and what it found underneath. The running stage turns.
    public class PipelineGraph : Control
    {
        private readonly System.Windows.Forms.Timer _spin = new() { Interval = 60 };
        private readonly ToolTip _tip = new();
        private DeployRun _run;
        private float _angle;
        private int _hover = -1;
        private List<RectangleF> _nodes = new();

        public event Action<DeployStage> StageClicked;

        public PipelineGraph()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Palette.Card;
            _spin.Tick += (_, _) => { _angle = (_angle + 18) % 360; Invalidate(); };
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DeployRun Run
        {
            get => _run;
            set
            {
                _run = value;
                _spin.Enabled = value?.Stages.Any(s => s.Status == DeployStage.Running) == true;
                Invalidate();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _spin.Dispose(); _tip.Dispose(); }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            if (_run == null || _run.Stages.Count == 0)
            {
                TextRenderer.DrawText(g, "No deployment has run for this application yet.", Font, ClientRectangle, Palette.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            var stages = _run.Stages;
            var diameter = LogicalToDeviceUnits(30);
            var margin = LogicalToDeviceUnits(18);
            var step = (Width - 2 * margin) / (float)stages.Count;
            var top = LogicalToDeviceUnits(14);
            _nodes = new List<RectangleF>();

            // Connectors first, so the nodes sit on top of them
            for (var i = 0; i < stages.Count - 1; i++)
            {
                var x1 = margin + step * i + step / 2 + diameter / 2f;
                var x2 = margin + step * (i + 1) + step / 2 - diameter / 2f;
                var done = stages[i].Status is DeployStage.Succeeded or DeployStage.Warning && stages[i + 1].Status != DeployStage.Pending && stages[i + 1].Status != DeployStage.Skipped;
                using var pen = new Pen(done ? Palette.Green : Palette.Faint, LogicalToDeviceUnits(2));
                if (!done) pen.DashStyle = DashStyle.Dash;
                g.DrawLine(pen, x1 + 3, top + diameter / 2f, x2 - 3, top + diameter / 2f);
            }

            using var smallFont = new Font(Font.FontFamily, Font.Size * 0.88f);
            using var titleFont = new Font(Font, FontStyle.Bold);

            for (var i = 0; i < stages.Count; i++)
            {
                var stage = stages[i];
                var center = margin + step * i + step / 2;
                var node = new RectangleF(center - diameter / 2f, top, diameter, diameter);
                _nodes.Add(new RectangleF(center - step / 2, 0, step, Height));
                var color = Palette.ForStage(stage.Status);

                if (i == _hover)
                    using (var halo = new SolidBrush(Color.FromArgb(40, color)))
                        g.FillEllipse(halo, RectangleF.Inflate(node, 5, 5));

                if (stage.Status is DeployStage.Pending or DeployStage.Skipped)
                {
                    using var fill = new SolidBrush(Palette.Card);
                    using var ring = new Pen(color, LogicalToDeviceUnits(2));
                    g.FillEllipse(fill, node);
                    g.DrawEllipse(ring, node);
                }
                else
                {
                    using var fill = new SolidBrush(color);
                    g.FillEllipse(fill, node);
                }

                DrawGlyph(g, stage.Status, node, color);

                var titleArea = new Rectangle((int)(center - step / 2), (int)node.Bottom + LogicalToDeviceUnits(6), (int)step, LogicalToDeviceUnits(18));
                TextRenderer.DrawText(g, stage.Title, titleFont, titleArea, stage.Status == DeployStage.Skipped ? Palette.Faint : Palette.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);

                var detail = stage.Status switch
                {
                    DeployStage.Running => "running " + QualityAviationCodeRepositoryConsoleClient.Console.Services.DeployText.Duration(stage.StartedAt, null),
                    DeployStage.Pending => "waiting",
                    DeployStage.Skipped => "skipped",
                    _ => QualityAviationCodeRepositoryConsoleClient.Console.Services.DeployText.Duration(stage.StartedAt, stage.FinishedAt)
                };
                var detailArea = new Rectangle(titleArea.X, titleArea.Bottom, titleArea.Width, LogicalToDeviceUnits(16));
                TextRenderer.DrawText(g, detail, smallFont, detailArea, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);

                if (!string.IsNullOrEmpty(stage.Summary) && Height > detailArea.Bottom + LogicalToDeviceUnits(14))
                {
                    var summaryArea = new Rectangle(titleArea.X + 4, detailArea.Bottom + 2, titleArea.Width - 8, Height - detailArea.Bottom - 4);
                    TextRenderer.DrawText(g, stage.Summary, smallFont, summaryArea, stage.Status == DeployStage.Failed ? Palette.Red : Palette.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                }
            }
        }

        private void DrawGlyph(Graphics g, string status, RectangleF node, Color color)
        {
            using var white = new Pen(Color.White, LogicalToDeviceUnits(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            var cx = node.X + node.Width / 2;
            var cy = node.Y + node.Height / 2;
            var r = node.Width * 0.22f;

            switch (status)
            {
                case DeployStage.Succeeded:
                    g.DrawLines(white, new[] { new PointF(cx - r, cy), new PointF(cx - r / 3, cy + r * 0.7f), new PointF(cx + r, cy - r * 0.6f) });
                    break;
                case DeployStage.Failed:
                    g.DrawLine(white, cx - r * 0.8f, cy - r * 0.8f, cx + r * 0.8f, cy + r * 0.8f);
                    g.DrawLine(white, cx + r * 0.8f, cy - r * 0.8f, cx - r * 0.8f, cy + r * 0.8f);
                    break;
                case DeployStage.Warning:
                    g.DrawLine(white, cx, cy - r, cx, cy + r * 0.25f);
                    g.FillEllipse(Brushes.White, cx - 1.6f, cy + r * 0.7f - 1.6f, 3.2f, 3.2f);
                    break;
                case DeployStage.Running:
                    using (var arc = new Pen(Color.White, LogicalToDeviceUnits(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(arc, RectangleF.Inflate(node, -node.Width * 0.24f, -node.Height * 0.24f), _angle, 260);
                    break;
                case DeployStage.Skipped:
                    using (var dash = new Pen(color, LogicalToDeviceUnits(2)))
                        g.DrawLine(dash, cx - r * 0.7f, cy, cx + r * 0.7f, cy);
                    break;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hover = _nodes.FindIndex(n => n.Contains(e.Location));
            if (hover == _hover) return;

            _hover = hover;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();

            if (hover >= 0 && _run != null && hover < _run.Stages.Count)
            {
                var stage = _run.Stages[hover];
                _tip.SetToolTip(this, $"{stage.Title}: {stage.Status}{(string.IsNullOrEmpty(stage.Summary) ? "" : Environment.NewLine + stage.Summary)}");
            }
            else
            {
                _tip.SetToolTip(this, null);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var index = _nodes.FindIndex(n => n.Contains(e.Location));
            if (index >= 0 && _run != null && index < _run.Stages.Count) StageClicked?.Invoke(_run.Stages[index]);
        }
    }

    // Requests per minute as an area, server errors as red bars underneath, and the p95 response time as a line
    // on its own scale: who uses the application and how well it answers, over the last hour or day.
    public class MetricsChart : Control
    {
        private List<MetricsPoint> _points = new();

        public MetricsChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Palette.Card;
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public List<MetricsPoint> Points
        {
            get => _points;
            set { _points = value ?? new(); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            var left = LogicalToDeviceUnits(46);
            var right = LogicalToDeviceUnits(52);
            var top = LogicalToDeviceUnits(26);
            var bottom = LogicalToDeviceUnits(24);
            var plot = new RectangleF(left, top, Math.Max(Width - left - right, 10), Math.Max(Height - top - bottom, 10));

            using var smallFont = new Font(Font.FontFamily, Font.Size * 0.88f);
            DrawLegend(g, smallFont, plot);

            if (_points.Count < 2 || _points.All(p => p.Requests == 0))
            {
                TextRenderer.DrawText(g, "No traffic in this period.", Font, Rectangle.Round(plot), Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            var maxRequests = Nice(_points.Max(p => p.Requests));
            var maxLatency = Nice(_points.Max(p => p.P95Ms));
            float X(int i) => plot.Left + plot.Width * i / (_points.Count - 1);
            float Y(double value, double max) => plot.Bottom - (float)(plot.Height * value / max);

            using (var grid = new Pen(Palette.Border))
            {
                for (var line = 0; line <= 4; line++)
                {
                    var y = plot.Top + plot.Height * line / 4;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    TextRenderer.DrawText(g, $"{maxRequests * (4 - line) / 4:0}", smallFont, new Point(2, (int)y - 8), Palette.Blue);
                    TextRenderer.DrawText(g, $"{maxLatency * (4 - line) / 4:0} ms", smallFont, new Point((int)plot.Right + 4, (int)y - 8), Palette.Amber);
                }
            }

            // Requests: an area with a clear edge
            var area = new List<PointF> { new(plot.Left, plot.Bottom) };
            area.AddRange(_points.Select((p, i) => new PointF(X(i), Y(p.Requests, maxRequests))));
            area.Add(new PointF(plot.Right, plot.Bottom));
            using (var fill = new LinearGradientBrush(plot, Color.FromArgb(90, Palette.Blue), Color.FromArgb(10, Palette.Blue), LinearGradientMode.Vertical))
                g.FillPolygon(fill, area.ToArray());
            using (var edge = new Pen(Palette.Blue, LogicalToDeviceUnits(2)))
                g.DrawLines(edge, area.Skip(1).Take(_points.Count).ToArray());

            // Errors: bars on the same scale as requests
            using (var errors = new SolidBrush(Color.FromArgb(200, Palette.Red)))
            {
                var barWidth = Math.Max(plot.Width / _points.Count - 1, 2);
                for (var i = 0; i < _points.Count; i++)
                {
                    if (_points[i].Errors == 0) continue;
                    var y = Y(_points[i].Errors, maxRequests);
                    g.FillRectangle(errors, X(i) - barWidth / 2, y, barWidth, plot.Bottom - y);
                }
            }

            // p95 response time on its own scale
            using (var latency = new Pen(Palette.Amber, LogicalToDeviceUnits(2)) { DashStyle = DashStyle.Dot })
                g.DrawLines(latency, _points.Select((p, i) => new PointF(X(i), Y(p.P95Ms, maxLatency))).ToArray());

            // Time along the bottom
            foreach (var i in new[] { 0, _points.Count / 2, _points.Count - 1 })
            {
                var label = _points[i].Minute.ToString("HH:mm");
                var size = TextRenderer.MeasureText(label, smallFont);
                TextRenderer.DrawText(g, label, smallFont, new Point((int)Math.Clamp(X(i) - size.Width / 2f, plot.Left - 10, plot.Right - size.Width + 10), (int)plot.Bottom + 4), Palette.Muted);
            }
        }

        private void DrawLegend(Graphics g, Font font, RectangleF plot)
        {
            var x = (int)plot.Left;
            foreach (var (label, color) in new[] { ("Requests per minute", Palette.Blue), ("Server errors", Palette.Red), ("p95 response time", Palette.Amber) })
            {
                using (var brush = new SolidBrush(color)) g.FillRectangle(brush, x, LogicalToDeviceUnits(8), LogicalToDeviceUnits(10), LogicalToDeviceUnits(10));
                TextRenderer.DrawText(g, label, font, new Point(x + LogicalToDeviceUnits(14), LogicalToDeviceUnits(5)), Palette.Text);
                x += LogicalToDeviceUnits(14) + TextRenderer.MeasureText(label, font).Width + LogicalToDeviceUnits(16);
            }
        }

        // A round number at or above the largest value, so the scale reads well
        private static double Nice(double value)
        {
            if (value <= 0) return 1;
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
                if (step * magnitude >= value) return step * magnitude;
            return 10 * magnitude;
        }
    }
}
