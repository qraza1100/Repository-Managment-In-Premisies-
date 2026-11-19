using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;

namespace QualityAviationHelpdesk.Services
{
    // The IT team as the portal sees it: every client login that has used the helpdesk. Used for the assignment
    // and to-do owner lists and for the dashboard's team names.
    public class ItMembers
    {
        private readonly IDbContextFactory<HelpdeskDb> _factory;
        private readonly IMemoryCache _cache;

        public ItMembers(IDbContextFactory<HelpdeskDb> factory, IMemoryCache cache)
        {
            _factory = factory;
            _cache = cache;
        }

        // Called on every IT request; touches the database at most every few minutes per person
        public async Task SeenAsync(string username, bool lead, CancellationToken cancellationToken)
        {
            var key = $"member-seen:{username.ToLowerInvariant()}:{lead}";
            if (_cache.TryGetValue(key, out _)) return;

            var now = AuditChain.Now();
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($@"
MERGE hd.ItMembers WITH (HOLDLOCK) AS t
USING (SELECT {username} AS Username) AS s ON t.Username = s.Username
WHEN MATCHED THEN UPDATE SET LastSeen = {now}, IsLead = {lead}
WHEN NOT MATCHED THEN INSERT (Username, IsLead, FirstSeen, LastSeen) VALUES ({username}, {lead}, {now}, {now});", cancellationToken);

            _cache.Set(key, true, TimeSpan.FromMinutes(5));
            _cache.Remove("members");
        }

        public async Task<List<string>> NamesAsync(CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue("members", out List<string> cached)) return cached;

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var names = await db.ItMembers.AsNoTracking().OrderBy(m => m.Username).Select(m => m.Username).ToListAsync(cancellationToken);
            _cache.Set("members", names, TimeSpan.FromMinutes(1));
            return names;
        }

        public async Task<string> CanonicalAsync(string username, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            var names = await NamesAsync(cancellationToken);
            return names.FirstOrDefault(n => string.Equals(n, username.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new ValidationException(new[] { $"\"{username}\" is not a member of the IT team (only people who have used the helpdesk from the client can be chosen)." });
        }
    }
}
