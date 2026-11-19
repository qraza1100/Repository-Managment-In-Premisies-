using Microsoft.EntityFrameworkCore;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;

namespace QualityAviationHelpdesk.Services
{
    // Keeps every IT client and browser in step with the database.
    //
    // One loop per running copy of the portal reads new rows of hd.Changes (immediately after a local write, otherwise
    // every second, which also picks up writes made by another copy) and keeps the newest ones in memory. Long-polling
    // clients and server-sent-event streams wait on that memory, so a thousand open pages cost one query a second.
    public class ChangeFeed : BackgroundService
    {
        private const int Keep = 2000;
        private static readonly TimeSpan PruneAfter = TimeSpan.FromDays(3);

        private readonly IDbContextFactory<HelpdeskDb> _factory;
        private readonly ILogger<ChangeFeed> _logger;
        private readonly object _gate = new();
        private readonly LinkedList<ChangeDto> _recent = new();
        private TaskCompletionSource _changed = NewSignal();
        private readonly SemaphoreSlim _poke = new(0);
        private long _last;
        private bool _started;
        private DateTime _lastPrune = DateTime.MinValue;

        public ChangeFeed(IDbContextFactory<HelpdeskDb> factory, ILogger<ChangeFeed> logger)
        {
            _factory = factory;
            _logger = logger;
        }

        public long Last => Interlocked.Read(ref _last);

        // A write on this copy just committed: look now instead of at the next tick
        public void Poke()
        {
            if (_poke.CurrentCount == 0) _poke.Release();
        }

        // Waits until something newer than `after` exists or the timeout passes
        public async Task<long> WaitAsync(long after, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task signal;
            lock (_gate)
            {
                if (_last > after) return _last;
                signal = _changed.Task;
            }

            await Task.WhenAny(signal, Task.Delay(timeout, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            return Last;
        }

        public ChangeBatch Since(long after, int max = 500)
        {
            lock (_gate)
            {
                var batch = new ChangeBatch { Last = _last };
                if (after >= _last) return batch;

                // Older than what is kept in memory: the caller has missed too much and reloads everything
                if (_recent.First == null || _recent.First.Value.Seq > after + 1)
                {
                    batch.Reset = true;
                    return batch;
                }

                foreach (var change in _recent)
                {
                    if (change.Seq <= after) continue;
                    if (batch.Changes.Count >= max)
                    {
                        batch.Reset = true;
                        batch.Changes.Clear();
                        break;
                    }
                    batch.Changes.Add(change);
                }
                return batch;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var db = await _factory.CreateDbContextAsync(stoppingToken);
                    List<ChangeDto> fresh;
                    if (!_started)
                    {
                        // Start from the present: only the recent past is kept, history is in the tables themselves
                        fresh = await db.Changes.OrderByDescending(c => c.Seq).Take(Keep)
                            .Select(c => new ChangeDto { Seq = c.Seq, Topic = c.Topic, EntityId = c.EntityId }).ToListAsync(stoppingToken);
                        fresh.Reverse();
                        _started = true;
                    }
                    else
                    {
                        var after = Last;
                        fresh = await db.Changes.Where(c => c.Seq > after).OrderBy(c => c.Seq).Take(Keep)
                            .Select(c => new ChangeDto { Seq = c.Seq, Topic = c.Topic, EntityId = c.EntityId }).ToListAsync(stoppingToken);
                    }
                    Publish(fresh);

                    if (DateTime.UtcNow - _lastPrune > TimeSpan.FromHours(1))
                    {
                        _lastPrune = DateTime.UtcNow;
                        var cutoff = DateTime.UtcNow - PruneAfter;
                        await db.Changes.Where(c => c.At < cutoff).ExecuteDeleteAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reading the change feed failed; trying again shortly");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { });
                }

                await _poke.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken).ContinueWith(_ => { });
            }
        }

        private void Publish(List<ChangeDto> changes)
        {
            if (changes.Count == 0) return;
            TaskCompletionSource release;
            lock (_gate)
            {
                foreach (var change in changes)
                {
                    if (change.Seq <= _last) continue;
                    _recent.AddLast(change);
                    _last = change.Seq;
                }
                while (_recent.Count > Keep) _recent.RemoveFirst();

                release = _changed;
                _changed = NewSignal();
            }
            release.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
