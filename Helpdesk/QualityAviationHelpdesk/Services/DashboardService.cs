using Microsoft.EntityFrameworkCore;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;

namespace QualityAviationHelpdesk.Services
{
    public sealed record Slice(string Label, int Value, string Cls);

    public sealed class TeamRow
    {
        public string Name { get; set; }
        public int Resolved { get; set; }
        public double? AvgResolveHours { get; set; }
        public double? SlaMetPercent { get; set; }
        public int OpenAssigned { get; set; }
        public int TodosDone { get; set; }
        public double HoursLogged { get; set; }
    }

    public sealed class Completion
    {
        public string Number { get; set; }
        public string Summary { get; set; }
        public string Department { get; set; }
        public string ResolvedBy { get; set; }
        public DateTime ResolvedAt { get; set; }
        public double Hours { get; set; }
        public bool SlaMet { get; set; }
        public int Priority { get; set; }
    }

    public sealed class DashboardData
    {
        public int Days { get; set; }
        public DateTime GeneratedAt { get; set; }
        public int Raised { get; set; }
        public int Resolved { get; set; }
        public int Open { get; set; }
        public int Overdue { get; set; }
        public double? AvgResolveHours { get; set; }
        public double? AvgFirstResponseHours { get; set; }
        public double? SlaMetPercent { get; set; }
        public double? ReopenPercent { get; set; }
        public int TodosDone { get; set; }
        public double HoursLogged { get; set; }
        public List<string> Labels { get; set; } = new();
        public List<int> RaisedSeries { get; set; } = new();
        public List<int> ResolvedSeries { get; set; } = new();
        public List<Slice> OpenByPriority { get; set; } = new();
        public List<Slice> ByStatus { get; set; } = new();
        public List<Slice> ByDepartment { get; set; } = new();
        public List<Slice> ByType { get; set; } = new();
        public List<TeamRow> Team { get; set; } = new();
        public List<Completion> Recent { get; set; } = new();
        public List<TicketListItem> Latest { get; set; } = new();
        public AuditVerification Audit { get; set; }
    }

    // The read-only productivity view for management. Numbers are worked out by the database (grouped, never
    // row by row), and the result is reused until something changes, so many viewers cost one calculation.
    public class DashboardService
    {
        private static readonly int[] Ranges = { 7, 30, 90, 365 };
        private readonly IDbContextFactory<HelpdeskDb> _factory;
        private readonly ChangeFeed _feed;
        private readonly AuditVerifier _audit;
        private readonly ItMembers _members;
        private readonly SemaphoreSlim _one = new(1, 1);
        private readonly Dictionary<int, (long Stamp, DateTime At, DashboardData Data)> _cache = new();

        public DashboardService(IDbContextFactory<HelpdeskDb> factory, ChangeFeed feed, AuditVerifier audit, ItMembers members)
        {
            _factory = factory;
            _feed = feed;
            _audit = audit;
            _members = members;
        }

        public static int NormalizeRange(int days) => Ranges.Contains(days) ? days : 30;

        public async Task<DashboardData> GetAsync(int days, CancellationToken cancellationToken)
        {
            days = NormalizeRange(days);
            var stamp = _feed.Last;

            await _one.WaitAsync(cancellationToken);
            try
            {
                if (_cache.TryGetValue(days, out var hit) && hit.Stamp == stamp && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(2))
                {
                    hit.Data.Audit = _audit.Last;
                    return hit.Data;
                }

                var data = await BuildAsync(days, cancellationToken);
                _cache[days] = (stamp, DateTime.UtcNow, data);
                return data;
            }
            finally
            {
                _one.Release();
            }
        }

        private async Task<DashboardData> BuildAsync(int days, CancellationToken cancellationToken)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var tickets = db.Tickets.AsNoTracking();
            var now = DateTime.UtcNow;

            // Buckets start at local midnight, so "a day" on the chart is a working day here, not a UTC day
            var bucketHours = days <= 31 ? 24 : days <= 180 ? 24 * 7 : 24 * 30;
            var start = DateTime.Today.AddDays(-days + 1).ToUniversalTime();
            var since = now.AddDays(-days);

            var data = new DashboardData { Days = days, GeneratedAt = now, Audit = _audit.Last };
            data.Raised = await tickets.CountAsync(t => t.CreatedAt >= since, cancellationToken);
            data.Open = await tickets.CountAsync(t => TicketStatus.Open.Contains(t.Status), cancellationToken);
            data.Overdue = await tickets.CountAsync(t => TicketStatus.Open.Contains(t.Status) && t.DueAt < now, cancellationToken);

            var resolved = tickets.Where(t => t.ResolvedAt >= since && t.ResolvedAt != null);
            var totals = await resolved.GroupBy(t => 1).Select(g => new
            {
                Count = g.Count(),
                AvgMinutes = g.Average(t => (double)EF.Functions.DateDiffMinute(t.CreatedAt, t.ResolvedAt.Value)),
                Met = g.Count(t => t.DueAt == null || t.ResolvedAt <= t.DueAt),
                Reopened = g.Count(t => t.ReopenCount > 0)
            }).FirstOrDefaultAsync(cancellationToken);
            if (totals != null)
            {
                data.Resolved = totals.Count;
                data.AvgResolveHours = Math.Round(totals.AvgMinutes / 60, 1);
                data.SlaMetPercent = Math.Round(100.0 * totals.Met / totals.Count, 1);
                data.ReopenPercent = Math.Round(100.0 * totals.Reopened / totals.Count, 1);
            }

            var firstResponse = await tickets.Where(t => t.AcceptedAt >= since && t.AcceptedAt != null)
                .Select(t => (double?)EF.Functions.DateDiffMinute(t.CreatedAt, t.AcceptedAt.Value)).AverageAsync(cancellationToken);
            if (firstResponse != null) data.AvgFirstResponseHours = Math.Round(firstResponse.Value / 60, 1);

            // Raised and resolved over time
            var raisedBuckets = await tickets.Where(t => t.CreatedAt >= start)
                .GroupBy(t => EF.Functions.DateDiffHour(start, t.CreatedAt) / bucketHours)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
            var resolvedBuckets = await tickets.Where(t => t.ResolvedAt >= start && t.ResolvedAt != null)
                .GroupBy(t => EF.Functions.DateDiffHour(start, t.ResolvedAt.Value) / bucketHours)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
            var bucketCount = (int)Math.Ceiling((now - start).TotalHours / bucketHours);
            for (var i = 0; i < bucketCount; i++)
            {
                var from = start.AddHours(i * bucketHours).ToLocalTime();
                data.Labels.Add(bucketHours == 24 ? from.ToString("dd MMM") : bucketHours == 168 ? "w/c " + from.ToString("dd MMM") : from.ToString("MMM yy"));
                data.RaisedSeries.Add(raisedBuckets.GetValueOrDefault(i));
                data.ResolvedSeries.Add(resolvedBuckets.GetValueOrDefault(i));
            }

            var byPriority = await tickets.Where(t => TicketStatus.Open.Contains(t.Status)).GroupBy(t => t.Priority)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => (int)g.Key, g => g.Count, cancellationToken);
            data.OpenByPriority = Priorities.All.Select(p => new Slice(Priorities.Label(p), byPriority.GetValueOrDefault(p), "c" + p)).ToList();

            var byStatus = await tickets.Where(t => t.CreatedAt >= since).GroupBy(t => t.Status)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
            data.ByStatus = TicketStatus.All.Where(byStatus.ContainsKey).Select(s => new Slice(TicketStatus.Label(s), byStatus[s], StatusClass(s))).ToList();

            data.ByDepartment = (await tickets.Where(t => t.CreatedAt >= since).GroupBy(t => t.RequesterDepartment)
                    .Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(g => g.Count).Take(8).ToListAsync(cancellationToken))
                .Select(g => new Slice(g.Key ?? "General", g.Count, "c7")).ToList();

            data.ByType = (await tickets.Where(t => t.CreatedAt >= since).GroupBy(t => t.Type)
                    .Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(g => g.Count).Take(8).ToListAsync(cancellationToken))
                .Select(g => new Slice(g.Key, g.Count, "c6")).ToList();

            // The team: who completed what
            var team = new Dictionary<string, TeamRow>(StringComparer.OrdinalIgnoreCase);
            TeamRow Row(string name) => team.TryGetValue(name, out var row) ? row : team[name] = new TeamRow { Name = name };
            foreach (var name in await _members.NamesAsync(cancellationToken)) Row(name);

            foreach (var r in await resolved.Where(t => t.ResolvedBy != null).GroupBy(t => t.ResolvedBy).Select(g => new
                     {
                         g.Key,
                         Count = g.Count(),
                         AvgMinutes = g.Average(t => (double)EF.Functions.DateDiffMinute(t.CreatedAt, t.ResolvedAt.Value)),
                         Met = g.Count(t => t.DueAt == null || t.ResolvedAt <= t.DueAt)
                     }).ToListAsync(cancellationToken))
            {
                var row = Row(r.Key);
                row.Resolved = r.Count;
                row.AvgResolveHours = Math.Round(r.AvgMinutes / 60, 1);
                row.SlaMetPercent = Math.Round(100.0 * r.Met / r.Count, 0);
            }

            foreach (var r in await tickets.Where(t => TicketStatus.Open.Contains(t.Status) && t.Assignee != null).GroupBy(t => t.Assignee)
                         .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
                Row(r.Key).OpenAssigned = r.Count;

            foreach (var r in await db.Todos.AsNoTracking().Where(t => t.CompletedAt >= since && t.CompletedBy != null).GroupBy(t => t.CompletedBy)
                         .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
            {
                Row(r.Key).TodosDone = r.Count;
                data.TodosDone += r.Count;
            }

            foreach (var r in await db.Comments.AsNoTracking().Where(c => c.CreatedAt >= since && c.AuthorKind == ActorKinds.It && c.MinutesSpent > 0)
                         .GroupBy(c => c.AuthorId).Select(g => new { g.Key, Minutes = g.Sum(c => c.MinutesSpent) }).ToListAsync(cancellationToken))
            {
                Row(r.Key).HoursLogged = Math.Round(r.Minutes / 60.0, 1);
                data.HoursLogged += r.Minutes / 60.0;
            }
            data.HoursLogged = Math.Round(data.HoursLogged, 1);

            data.Team = team.Values.OrderByDescending(r => r.Resolved).ThenByDescending(r => r.TodosDone).ThenBy(r => r.Name).ToList();

            data.Recent = (await resolved.Where(t => t.ResolvedBy != null).OrderByDescending(t => t.ResolvedAt).Take(8).Select(t => new
                {
                    t.Id, t.Summary, t.RequesterDepartment, t.ResolvedBy, ResolvedAt = t.ResolvedAt.Value, t.CreatedAt, t.DueAt, t.Priority
                }).ToListAsync(cancellationToken))
                .Select(t => new Completion
                {
                    Number = TicketNumbers.Format(t.Id),
                    Summary = t.Summary,
                    Department = t.RequesterDepartment,
                    ResolvedBy = t.ResolvedBy,
                    ResolvedAt = t.ResolvedAt,
                    Hours = Math.Round((t.ResolvedAt - t.CreatedAt).TotalHours, 1),
                    SlaMet = t.DueAt == null || t.ResolvedAt <= t.DueAt,
                    Priority = t.Priority
                }).ToList();

            data.Latest = (await tickets.OrderByDescending(t => t.Id).Take(12).Select(t => new TicketListItem
            {
                Id = t.Id, Type = t.Type, Summary = t.Summary, Priority = t.Priority, Status = t.Status, RequesterName = t.RequesterName,
                Department = t.RequesterDepartment, Assignee = t.Assignee, CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt, DueAt = t.DueAt
            }).ToListAsync(cancellationToken)).Select(t =>
            {
                t.Number = TicketNumbers.Format(t.Id);
                t.Overdue = TicketStatus.IsOpen(t.Status) && t.DueAt < now;
                return t;
            }).ToList();

            return data;
        }

        public static string StatusClass(string status) => status switch
        {
            TicketStatus.New => "c3",
            TicketStatus.Accepted => "c6",
            TicketStatus.InProgress => "c7",
            TicketStatus.OnHold => "c2",
            TicketStatus.Resolved => "c5",
            TicketStatus.Closed => "c4",
            _ => "c1"
        };
    }
}
