using Microsoft.EntityFrameworkCore;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;

namespace QualityAviationHelpdesk.Services
{
    // Walks the whole audit trail and recomputes every hash. A row that was changed, removed or slipped in after the
    // fact breaks the chain at that point. Runs every quarter of an hour, and on demand for IT leads.
    public class AuditVerifier : BackgroundService
    {
        private readonly IDbContextFactory<HelpdeskDb> _factory;
        private readonly ILogger<AuditVerifier> _logger;
        private readonly SemaphoreSlim _one = new(1, 1);
        private AuditVerification _last;

        public AuditVerifier(IDbContextFactory<HelpdeskDb> factory, ILogger<AuditVerifier> logger)
        {
            _factory = factory;
            _logger = logger;
        }

        public AuditVerification Last => _last;

        public async Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken)
        {
            await _one.WaitAsync(cancellationToken);
            try
            {
                await using var db = await _factory.CreateDbContextAsync(cancellationToken);
                var expectedPrev = AuditChain.Genesis;
                long count = 0, after = 0;

                while (true)
                {
                    var page = await db.AuditEvents.AsNoTracking().Where(e => e.Id > after).OrderBy(e => e.Id).Take(5000).ToListAsync(cancellationToken);
                    if (page.Count == 0) break;

                    foreach (var entry in page)
                    {
                        entry.At = DateTime.SpecifyKind(entry.At, DateTimeKind.Utc);
                        if (entry.PrevHash != expectedPrev || AuditChain.Hash(entry) != entry.Hash)
                        {
                            var broken = new AuditVerification
                            {
                                Intact = false,
                                Events = count,
                                BrokenAt = entry.Id,
                                CheckedAt = DateTime.UtcNow,
                                Message = entry.PrevHash != expectedPrev
                                    ? $"The audit trail is broken before event {entry.Id}: an earlier event was removed or changed."
                                    : $"Audit event {entry.Id} was changed after it was written."
                            };
                            _logger.LogCritical("Audit chain verification failed: {Message}", broken.Message);
                            return _last = broken;
                        }

                        expectedPrev = entry.Hash;
                        count++;
                    }
                    after = page[^1].Id;
                }

                return _last = new AuditVerification
                {
                    Intact = true,
                    Events = count,
                    CheckedAt = DateTime.UtcNow,
                    Message = count == 0 ? "No audit events yet." : $"All {count:N0} audit events are intact."
                };
            }
            finally
            {
                _one.Release();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ContinueWith(_ => { });
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await VerifyAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "The audit trail could not be verified this time");
                }
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken).ContinueWith(_ => { });
            }
        }
    }
}
