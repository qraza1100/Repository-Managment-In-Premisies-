using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QualityAviationHelpdesk.Data;
using QualityAviationHelpdesk.Services;
using System.Globalization;
using System.Security.Claims;

namespace QualityAviationHelpdesk.Security
{
    // Web sign-in for every Q+ CMR user, with the password they already have there.
    //
    // Zero trust in practice: a session is a claim that has to keep proving itself. Every RecheckSeconds the
    // person is looked up again in the legacy database; locked out, removed, password reset (SecurityStamp
    // changed) or roles taken away, and the session ends or shrinks on the next request.
    public class WebSignIn
    {
        private const int MaxFailures = 5;
        private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

        private readonly ILegacyDirectory _directory;
        private readonly IDbContextFactory<HelpdeskDb> _factory;
        private readonly Writes _writes;
        private readonly PortalOptions _options;
        private readonly ILogger<WebSignIn> _logger;

        public WebSignIn(ILegacyDirectory directory, IDbContextFactory<HelpdeskDb> factory, Writes writes, IOptions<PortalOptions> options, ILogger<WebSignIn> logger)
        {
            _directory = directory;
            _factory = factory;
            _writes = writes;
            _options = options.Value;
            _logger = logger;
        }

        public sealed record Result(ClaimsPrincipal Principal, string Error);

        public async Task<Result> SignInAsync(string login, string password, HttpContext http)
        {
            login = (login ?? "").Trim();
            var key = "web:" + login.ToUpperInvariant();
            if (key.Length > 300) key = key[..300];
            var cancellationToken = http.RequestAborted;

            var lockedUntil = await LockedUntilAsync(key, cancellationToken);
            if (lockedUntil != null)
                return new Result(null, $"Too many failed attempts for this account. Try again after {lockedUntil.Value.ToLocalTime():HH:mm}.");

            var profile = await _directory.VerifyPasswordAsync(login, password, cancellationToken);
            if (profile == null)
            {
                await RecordFailureAsync(key, cancellationToken);
                // What was typed is recorded only when it names a real account: an unknown "user name" may well be a
                // password typed into the wrong box
                var known = await _directory.ExistsAsync(login, cancellationToken);
                await AuditAsync("sign-in-refused", known ? login : "(unknown account)", null, http,
                    known ? "Wrong password, or the account is locked in Q+ CMR" : "No Q+ CMR account has that user name or e-mail");
                return new Result(null, "The user name or password is not correct, or this account is not allowed to sign in.");
            }

            var principal = BuildPrincipal(profile);
            if (principal == null)
            {
                await AuditAsync("sign-in-refused", profile.UserName, profile, http, "No helpdesk access for this account's roles");
                return new Result(null, "Your account has no access to the helpdesk. Ask IT to give you a role for it in Q+ CMR.");
            }

            await ClearFailuresAsync(key, cancellationToken);
            await AuditAsync("signed-in", profile.UserName, profile, http, null);
            return new Result(principal, null);
        }

        // null when the person may not use the portal at all. signedInAt: when the session began (kept across re-checks)
        public ClaimsPrincipal BuildPrincipal(LegacyProfile profile, DateTime? signedInAt = null)
        {
            if (profile == null || !profile.CanSignIn) return null;

            var executive = profile.Roles.Any(r => _options.ExecutiveRoles.Contains(r, StringComparer.OrdinalIgnoreCase)) ||
                            _options.ExecutiveUsers.Any(u => string.Equals(u, profile.UserName, StringComparison.OrdinalIgnoreCase) ||
                                                             string.Equals(u, profile.Email, StringComparison.OrdinalIgnoreCase));
            var requester = _options.RequesterRoles.Count == 0 ||
                            profile.Roles.Any(r => _options.RequesterRoles.Contains(r, StringComparer.OrdinalIgnoreCase));
            if (!executive && !requester) return null;

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, profile.Id),
                new(ClaimTypes.Name, profile.FullName ?? profile.UserName),
                new(Access.UserNameClaim, profile.UserName),
                new(Access.DepartmentClaim, profile.Department ?? ""),
                new(Access.StampClaim, profile.StampDigest),
                new(Access.CheckedClaim, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)),
                new(Access.SignedInClaim, (signedInAt ?? DateTime.UtcNow).Ticks.ToString(CultureInfo.InvariantCulture))
            };
            if (!string.IsNullOrEmpty(profile.Email)) claims.Add(new Claim(ClaimTypes.Email, profile.Email));
            if (requester) claims.Add(new Claim(ClaimTypes.Role, Access.Requester));
            if (executive) claims.Add(new Claim(ClaimTypes.Role, Access.Executive));
            claims.AddRange(profile.Roles.Select(r => new Claim(Access.LegacyRoleClaim, r)));

            return new ClaimsPrincipal(new ClaimsIdentity(claims, Access.WebScheme, ClaimTypes.Name, ClaimTypes.Role));
        }

        // Cookie middleware hook: runs on every request that carries a session cookie
        public async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
        {
            var user = context.Principal;
            if (user == null) return;

            var checkedAt = Time(user, Access.CheckedClaim);
            var signedInAt = Time(user, Access.SignedInClaim);

            // However active, a session ends after MaxSessionHours and the person signs in again
            if (DateTime.UtcNow - signedInAt > TimeSpan.FromHours(Math.Clamp(_options.MaxSessionHours, 1, 72)))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(Access.WebScheme);
                return;
            }
            if (DateTime.UtcNow - checkedAt < TimeSpan.FromSeconds(Math.Clamp(_options.RecheckSeconds, 10, 900))) return;

            LegacyProfile profile;
            try
            {
                profile = await _directory.GetProfileAsync(user.Id(), fresh: true, context.HttpContext.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The directory cannot be asked: fail closed rather than keep trusting an unverified session
                _logger.LogWarning(ex, "The legacy user directory could not be reached to re-check a session");
                context.RejectPrincipal();
                return;
            }

            var refreshed = profile != null && profile.StampDigest == user.FindFirstValue(Access.StampClaim) ? BuildPrincipal(profile, signedInAt) : null;
            if (refreshed == null)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(Access.WebScheme);
                return;
            }

            context.ReplacePrincipal(refreshed);
            context.ShouldRenew = true;
        }

        private static DateTime Time(ClaimsPrincipal user, string claim) =>
            long.TryParse(user.FindFirstValue(claim), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks > 0
                ? new DateTime(ticks, DateTimeKind.Utc)
                : DateTime.MinValue;

        private async Task<DateTime?> LockedUntilAsync(string key, CancellationToken cancellationToken)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var entry = await db.LoginThrottle.AsNoTracking().FirstOrDefaultAsync(l => l.Key == key, cancellationToken);
            return entry?.LockedUntil > DateTime.UtcNow ? DateTime.SpecifyKind(entry.LockedUntil.Value, DateTimeKind.Utc) : null;
        }

        // One statement, so failures from several sign-in attempts at once are all counted
        private async Task RecordFailureAsync(string key, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var windowStart = now - FailureWindow;
            var lockUntil = now + LockFor;
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($@"
MERGE hd.LoginThrottle WITH (HOLDLOCK) AS t
USING (SELECT {key} AS [Key]) AS s ON t.[Key] = s.[Key]
WHEN MATCHED THEN UPDATE SET
    Failures    = CASE WHEN t.WindowStart < {windowStart} THEN 1 ELSE t.Failures + 1 END,
    WindowStart = CASE WHEN t.WindowStart < {windowStart} THEN {now} ELSE t.WindowStart END,
    LockedUntil = CASE WHEN t.WindowStart < {windowStart} THEN NULL
                       WHEN t.Failures + 1 >= {MaxFailures} THEN {lockUntil}
                       ELSE t.LockedUntil END
WHEN NOT MATCHED THEN
    INSERT ([Key], Failures, WindowStart, LockedUntil) VALUES ({key}, 1, {now}, NULL);", cancellationToken);
        }

        private async Task ClearFailuresAsync(string key, CancellationToken cancellationToken)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            await db.LoginThrottle.Where(l => l.Key == key).ExecuteDeleteAsync(cancellationToken);
        }

        private Task AuditAsync(string action, string login, LegacyProfile profile, HttpContext http, string detail)
        {
            var actor = profile == null
                ? new Actor(ActorKinds.Anonymous, "-", Clip(login, 100), http.ClientAddress())
                : new Actor(ActorKinds.Requester, profile.Id, profile.FullName, http.ClientAddress());
            return _writes.RunAsync(write =>
            {
                write.Audit("account", Clip(profile?.UserName ?? login, 60), action, actor, detail, visibleToRequester: false);
                return Task.FromResult(0);
            }, CancellationToken.None);
        }

        private static string Clip(string text, int length)
        {
            text = string.IsNullOrWhiteSpace(text) ? "-" : text.Trim();
            return text.Length <= length ? text : text[..length];
        }
    }
}
