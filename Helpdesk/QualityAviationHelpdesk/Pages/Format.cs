using QualityAviationHelpdesk.Contracts;

namespace QualityAviationHelpdesk.Pages
{
    // How times, sizes and states read on the web pages (the server's local time zone is the office's)
    public static class Format
    {
        public static string When(DateTime? utc) => utc == null ? "—" : utc.Value.ToLocalTime().ToString("dd MMM yyyy, HH:mm");

        public static string Ago(DateTime? utc)
        {
            if (utc == null) return "—";
            var span = DateTime.UtcNow - utc.Value;
            if (span.TotalSeconds < 0) span = TimeSpan.Zero;
            return span.TotalMinutes < 1 ? "just now"
                : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago"
                : span.TotalHours < 24 ? $"{(int)span.TotalHours} h ago"
                : span.TotalDays < 30 ? $"{(int)span.TotalDays} d ago"
                : utc.Value.ToLocalTime().ToString("dd MMM yyyy");
        }

        public static string Due(DateTime? utc)
        {
            if (utc == null) return "—";
            var span = utc.Value - DateTime.UtcNow;
            var late = span < TimeSpan.Zero;
            span = span.Duration();
            var amount = span.TotalHours < 1 ? $"{Math.Max(1, (int)span.TotalMinutes)} min" : span.TotalHours < 48 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalDays} days";
            return late ? $"{amount} overdue" : $"in {amount}";
        }

        public static string Size(long bytes) =>
            bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";

        public static string StatusClass(string status) => "pill pill-" + status;

        public static string Initials(string name)
        {
            var parts = (name ?? "?").Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? "?" : parts.Length == 1 ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant() : $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}";
        }

        public static string Action(TimelineEntry entry) => entry.Action switch
        {
            "created" => "raised the ticket",
            "accept" => "accepted it",
            "reject" => "rejected it",
            "start" => "started work",
            "hold" => "put it on hold",
            "resolve" => "resolved it",
            "close" => "closed it",
            "confirm" => "confirmed it is solved",
            "cancel" => "withdrew it",
            "reopen" => "reopened it",
            "assign" or "take" => "changed who works on it",
            "priority" => "changed the priority",
            "details-updated" => "updated the details",
            "auto-closed" => "closed it automatically",
            _ => entry.Action
        };
    }
}
