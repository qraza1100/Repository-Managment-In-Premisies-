using Microsoft.EntityFrameworkCore;
using QualityAviationHelpdesk.Data;
using QualityAviationHelpdesk.Security;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace QualityAviationHelpdesk.Services
{
    public static class ActorKinds
    {
        public const string Requester = "requester";
        public const string It = "it";
        public const string System = "system";
        public const string Anonymous = "anonymous";
    }

    // Who did something, as recorded in the audit trail
    public sealed record Actor(string Kind, string Id, string Name, string Ip)
    {
        public static readonly Actor System = new(ActorKinds.System, "system", "Helpdesk", null);

        public static Actor From(HttpContext http)
        {
            var user = http.User;
            var kind = user.IsInRole(Access.It) ? ActorKinds.It : ActorKinds.Requester;
            return new Actor(kind, user.Id(), user.DisplayName(), http.ClientAddress());
        }
    }

    // The write path every change goes through: one transaction holding the change itself, its audit events
    // (chained by hash) and its entries in the change feed. Either all of it is stored, or none of it.
    public class Writes
    {
        private readonly IDbContextFactory<HelpdeskDb> _factory;
        private readonly ChangeFeed _feed;

        public Writes(IDbContextFactory<HelpdeskDb> factory, ChangeFeed feed)
        {
            _factory = factory;
            _feed = feed;
        }

        public async Task<T> RunAsync<T>(Func<WriteContext, Task<T>> work, CancellationToken cancellationToken)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var write = new WriteContext(db);

            var result = await work(write);
            await db.SaveChangesAsync(cancellationToken);

            if (write.Events.Count > 0 || write.ChangedTopics.Count > 0)
            {
                // The audit chain and the change feed are each one sequence for the whole database, so their ends are
                // extended by one writer at a time. That also makes change numbers follow commit order, so a reader of
                // the feed can never see change 11 before change 10 exists. Taken only after the change itself is
                // saved, so the lock is held for milliseconds.
                await db.Database.ExecuteSqlRawAsync(
                    "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'hd-audit', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 20000; " +
                    "IF @r < 0 THROW 51000, 'The audit trail is busy. Try again.', 1;", cancellationToken);
            }

            if (write.Events.Count > 0)
            {
                var last = await db.AuditEvents.OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync(cancellationToken) ?? AuditChain.Genesis;
                foreach (var entry in write.Events)
                {
                    entry.PrevHash = last;
                    entry.Hash = AuditChain.Hash(entry);
                    last = entry.Hash;
                    db.AuditEvents.Add(entry);
                    // One row per insert: identity values then follow the chain order exactly
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            if (write.ChangedTopics.Count > 0)
            {
                var now = AuditChain.Now();
                foreach (var (topic, id) in write.ChangedTopics.Distinct())
                    db.Changes.Add(new Change { Topic = topic, EntityId = id, At = now });
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            if (write.ChangedTopics.Count > 0) _feed.Poke();
            return result;
        }
    }

    public class WriteContext
    {
        internal readonly List<AuditEvent> Events = new();
        internal readonly List<(string Topic, string Id)> ChangedTopics = new();

        public WriteContext(HelpdeskDb db) => Db = db;

        public HelpdeskDb Db { get; }

        public void Audit(string entityType, string entityId, string action, Actor actor, string detail, bool visibleToRequester)
        {
            Events.Add(new AuditEvent
            {
                EntityType = entityType,
                EntityId = entityId,
                Action = action,
                ActorKind = actor.Kind,
                ActorId = Clip(actor.Id, 450) ?? "-",
                ActorName = Clip(actor.Name, 256) ?? "-",
                Detail = Clip(detail, 4000),
                VisibleToRequester = visibleToRequester,
                Ip = Clip(actor.Ip, 64),
                At = AuditChain.Now()
            });
        }

        public void Changed(string topic, object id) => ChangedTopics.Add((topic, Convert.ToString(id, CultureInfo.InvariantCulture)));

        private static string Clip(string text, int length) => text == null ? null : text.Length <= length ? text : text[..length];
    }

    public static class AuditChain
    {
        public static readonly string Genesis = new('0', 64);

        // The database keeps milliseconds; hashing the same precision makes a stored row hash to the same value
        public static DateTime Now()
        {
            var ticks = DateTime.UtcNow.Ticks;
            return new DateTime(ticks - ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        }

        public static string Hash(AuditEvent e)
        {
            var text = string.Join('\u001f',
                e.PrevHash, e.EntityType, e.EntityId, e.Action, e.ActorKind, e.ActorId, e.ActorName, e.Detail ?? "",
                e.VisibleToRequester ? "1" : "0", e.Ip ?? "", e.At.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }
    }
}
