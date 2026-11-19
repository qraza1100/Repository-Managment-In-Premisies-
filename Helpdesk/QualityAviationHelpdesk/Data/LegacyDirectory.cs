using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;

namespace QualityAviationHelpdesk.Data
{
    // The Q+ CMR (.NET 5) database: the one place that decides who works here, what they are called, which
    // department they belong to and which roles they hold. The portal only ever reads it.
    //
    //   AspNetUsers          accounts, ASP.NET Identity password hashes, LockedOut, UserTypeId
    //   AspNetRoles          role names (Administrator, Corporate Supervisor, Git User, ...)
    //   AspNetUserRoles      who holds which role
    //   AppTraacsUserTypes   the user's department (UserTypeName)
    public class LegacyDb : DbContext
    {
        public LegacyDb(DbContextOptions<LegacyDb> options) : base(options) { }

        public DbSet<LegacyUser> Users => Set<LegacyUser>();
        public DbSet<LegacyRole> Roles => Set<LegacyRole>();
        public DbSet<LegacyUserRole> UserRoles => Set<LegacyUserRole>();
        public DbSet<LegacyUserType> UserTypes => Set<LegacyUserType>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<LegacyUser>().ToTable("AspNetUsers", "dbo");
            model.Entity<LegacyRole>().ToTable("AspNetRoles", "dbo");
            model.Entity<LegacyUserRole>(e =>
            {
                e.ToTable("AspNetUserRoles", "dbo");
                e.HasKey(r => new { r.UserId, r.RoleId });
            });
            model.Entity<LegacyUserType>(e =>
            {
                e.ToTable("AppTraacsUserTypes", "dbo");
                e.HasKey(t => t.UserTypeId);
            });
        }

        // Belt and braces: the SQL login has SELECT rights only, and this context refuses to even try
        public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
            throw new InvalidOperationException("The legacy Q+ CMR database is read-only for the helpdesk.");

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The legacy Q+ CMR database is read-only for the helpdesk.");
    }

    public class LegacyUser
    {
        public string Id { get; set; }
        public string UserName { get; set; }
        public string NormalizedUserName { get; set; }
        public string Email { get; set; }
        public string NormalizedEmail { get; set; }
        public string FullName { get; set; }
        public string PasswordHash { get; set; }
        public string SecurityStamp { get; set; }
        public bool LockedOut { get; set; }
        public DateTimeOffset? LockoutEnd { get; set; }
        public int? UserTypeId { get; set; }
    }

    public class LegacyRole
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class LegacyUserRole
    {
        public string UserId { get; set; }
        public string RoleId { get; set; }
    }

    public class LegacyUserType
    {
        public int UserTypeId { get; set; }
        public string UserTypeName { get; set; }
    }

    // What the portal knows about a person, as of the last read of the legacy database
    public sealed record LegacyProfile(
        string Id,
        string UserName,
        string FullName,
        string Email,
        string Department,
        IReadOnlyList<string> Roles,
        // A digest of the legacy SecurityStamp: it changes when the password is reset or the account is edited
        // there, which ends every portal session of that person
        string StampDigest,
        bool CanSignIn);

    public interface ILegacyDirectory
    {
        Task<LegacyProfile> VerifyPasswordAsync(string login, string password, CancellationToken cancellationToken);
        Task<LegacyProfile> GetProfileAsync(string userId, bool fresh, CancellationToken cancellationToken);
        Task<bool> ExistsAsync(string login, CancellationToken cancellationToken);
    }

    public class LegacyDirectory : ILegacyDirectory
    {
        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
        private readonly IDbContextFactory<LegacyDb> _factory;
        private readonly IMemoryCache _cache;
        private readonly PasswordHasher<LegacyUser> _hasher = new();
        // Checked when the account does not exist, so a wrong user name costs as long as a wrong password
        private readonly string _decoyHash;

        public LegacyDirectory(IDbContextFactory<LegacyDb> factory, IMemoryCache cache)
        {
            _factory = factory;
            _cache = cache;
            _decoyHash = _hasher.HashPassword(new LegacyUser(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)));
        }

        // The user name or e-mail address and password a person types into the sign-in page.
        // Returns null for every kind of refusal (unknown, wrong password, locked out) so the reason is not revealed.
        public async Task<LegacyProfile> VerifyPasswordAsync(string login, string password, CancellationToken cancellationToken)
        {
            var normalized = (login ?? "").Trim().ToUpperInvariant();
            if (normalized.Length == 0 || normalized.Length > 256 || string.IsNullOrEmpty(password) || password.Length > 512)
                return null;

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var candidates = await db.Users.AsNoTracking()
                .Where(u => u.NormalizedUserName == normalized || u.NormalizedEmail == normalized)
                .OrderBy(u => u.Id)
                .Take(2)
                .ToListAsync(cancellationToken);

            // Two accounts answering to the same text (one's user name is the other's e-mail) are refused rather than guessed
            var user = candidates.Count == 1 ? candidates[0] : null;
            var hash = string.IsNullOrEmpty(user?.PasswordHash) ? _decoyHash : user.PasswordHash;

            PasswordVerificationResult result;
            try
            {
                result = _hasher.VerifyHashedPassword(user ?? new LegacyUser(), hash, password);
            }
            catch (FormatException)
            {
                result = PasswordVerificationResult.Failed;
            }

            if (user == null || result == PasswordVerificationResult.Failed) return null;

            // SuccessRehashNeeded is still a success; the legacy application upgrades its own hashes
            var profile = await LoadAsync(db, user, cancellationToken);
            _cache.Set(CacheKey(user.Id), profile, CacheFor);
            return profile.CanSignIn ? profile : null;
        }

        // Whether a user name or e-mail address belongs to an account (for the audit trail of failed sign-ins)
        public async Task<bool> ExistsAsync(string login, CancellationToken cancellationToken)
        {
            var normalized = (login ?? "").Trim().ToUpperInvariant();
            if (normalized.Length == 0 || normalized.Length > 256) return false;
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            return await db.Users.AnyAsync(u => u.NormalizedUserName == normalized || u.NormalizedEmail == normalized, cancellationToken);
        }

        // fresh: skip the cache, for decisions that must reflect the database right now
        public async Task<LegacyProfile> GetProfileAsync(string userId, bool fresh, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            if (!fresh && _cache.TryGetValue(CacheKey(userId), out LegacyProfile cached)) return cached;

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            var profile = user == null ? null : await LoadAsync(db, user, cancellationToken);

            _cache.Set(CacheKey(userId), profile, CacheFor);
            return profile;
        }

        private static async Task<LegacyProfile> LoadAsync(LegacyDb db, LegacyUser user, CancellationToken cancellationToken)
        {
            var roles = await (from link in db.UserRoles
                               join role in db.Roles on link.RoleId equals role.Id
                               where link.UserId == user.Id
                               select role.Name)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            string department = null;
            if (user.UserTypeId != null)
                department = await db.UserTypes.AsNoTracking()
                    .Where(t => t.UserTypeId == user.UserTypeId)
                    .Select(t => t.UserTypeName)
                    .FirstOrDefaultAsync(cancellationToken);

            var canSignIn = !user.LockedOut && (user.LockoutEnd == null || user.LockoutEnd <= DateTimeOffset.UtcNow);

            return new LegacyProfile(
                user.Id,
                user.UserName ?? user.Email ?? user.Id,
                string.IsNullOrWhiteSpace(user.FullName) ? user.UserName : user.FullName.Trim(),
                user.Email,
                string.IsNullOrWhiteSpace(department) ? "General" : department.Trim(),
                roles.Where(r => !string.IsNullOrWhiteSpace(r)).OrderBy(r => r).ToList(),
                Digest(user.SecurityStamp),
                canSignIn);
        }

        public static string Digest(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")))[..24];

        private static string CacheKey(string userId) => "legacy-user:" + userId;
    }
}
