using Microsoft.EntityFrameworkCore;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;
using System.Globalization;

namespace QualityAviationHelpdesk.Services
{
    // A resolved ticket the requester neither confirms nor reopens is closed after Settings.AutoCloseDays.
    // Safe with several copies of the portal running: each ticket is closed in its own write, and a copy that
    // finds it already changed simply moves on.
    public class AutoClose : BackgroundService
    {
        private readonly IDbContextFactory<HelpdeskDb> _factory;
        private readonly Writes _writes;
        private readonly Config _config;
        private readonly ILogger<AutoClose> _logger;

        public AutoClose(IDbContextFactory<HelpdeskDb> factory, Writes writes, Config config, ILogger<AutoClose> logger)
        {
            _factory = factory;
            _writes = writes;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ContinueWith(_ => { });
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Closing old resolved tickets failed this time");
                }
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken).ContinueWith(_ => { });
            }
        }

        public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
        {
            var settings = await _config.SettingsAsync(cancellationToken);
            if (settings.AutoCloseDays <= 0) return 0;

            var cutoff = DateTime.UtcNow.AddDays(-settings.AutoCloseDays);
            List<int> due;
            await using (var db = await _factory.CreateDbContextAsync(cancellationToken))
                due = await db.Tickets.Where(t => t.Status == TicketStatus.Resolved && t.ResolvedAt < cutoff).Select(t => t.Id).Take(500).ToListAsync(cancellationToken);

            var closed = 0;
            foreach (var id in due)
            {
                try
                {
                    closed += await _writes.RunAsync(async write =>
                    {
                        var ticket = await write.Db.Tickets.FirstOrDefaultAsync(t => t.Id == id && t.Status == TicketStatus.Resolved && t.ResolvedAt < cutoff, cancellationToken);
                        if (ticket == null) return 0;

                        var now = AuditChain.Now();
                        ticket.Status = TicketStatus.Closed;
                        ticket.ClosedAt = now;
                        ticket.UpdatedAt = now;
                        write.Audit("ticket", id.ToString(CultureInfo.InvariantCulture), "auto-closed", Actor.System,
                            $"Closed automatically: resolved {settings.AutoCloseDays} days ago without a reply", true);
                        write.Changed(ChangeTopics.Ticket, id);
                        return 1;
                    }, cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Changed by someone (or another copy of the portal) in the meantime: leave it to them
                }
            }

            if (closed > 0) _logger.LogInformation("Closed {Count} resolved tickets nobody replied to", closed);
            return closed;
        }
    }
}
