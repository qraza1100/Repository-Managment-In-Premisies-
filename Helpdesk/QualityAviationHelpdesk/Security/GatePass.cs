using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using QualityAviationHelpdesk.Services;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace QualityAviationHelpdesk.Security
{
    public sealed record GatePassIdentity(string Username, bool IsAgentAdmin);

    public class AgentUnavailableException : Exception
    {
        public AgentUnavailableException(string message, Exception inner = null) : base(message, inner) { }
    }

    // Asks the deploy agent who a gate pass belongs to. The portal never sees the repository API's keys or its user
    // store; the agent checks the pass exactly as the API does (signature, expiry, and that the password it was
    // issued for is still the current one) and answers with the user name.
    public class GatePassIntrospector
    {
        private readonly HttpClient _http;
        private readonly GatePassOptions _options;
        private readonly ILogger<GatePassIntrospector> _logger;

        public GatePassIntrospector(IOptions<GatePassOptions> options, ILogger<GatePassIntrospector> logger)
        {
            _options = options.Value;
            _logger = logger;

            var pinned = (_options.AgentThumbprint ?? "").Replace(":", "").Replace(" ", "").Trim();
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                UseProxy = false,
                SslOptions =
                {
                    // The agent's certificate is self-signed; the pinned thumbprint is the whole trust decision.
                    // Without one nothing is trusted, so the IT API stays closed instead of trusting any certificate.
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                        pinned.Length > 0 && certificate != null &&
                        string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), pinned, StringComparison.OrdinalIgnoreCase)
                }
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10), BaseAddress = new Uri(_options.AgentUrl.TrimEnd('/') + "/") };

            if (pinned.Length == 0)
                _logger.LogError("GatePass:AgentThumbprint is not set, so no IT client can sign in. Set it to the server certificate's SHA-256 thumbprint.");
        }

        // null: the agent says the gate pass is not valid. Throws when the agent cannot be asked.
        public async Task<GatePassIdentity> IntrospectAsync(string gatePass, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/deploy/overview");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gatePass);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new AgentUnavailableException("The deploy agent could not be asked to check the login.", ex);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
                if (!response.IsSuccessStatusCode)
                    throw new AgentUnavailableException($"The deploy agent answered {(int)response.StatusCode} while checking a login.");

                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
                string user = null;
                var admin = false;
                foreach (var property in json.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("user") || property.NameEquals("User")) user = property.Value.GetString();
                    else if (property.NameEquals("isAdmin") || property.NameEquals("IsAdmin")) admin = property.Value.ValueKind == JsonValueKind.True;
                }

                return string.IsNullOrWhiteSpace(user) ? null : new GatePassIdentity(user.Trim(), admin);
            }
        }
    }

    // The IT client's sign-in: "Authorization: Bearer <gate pass>", the same pass it uses for the repository API.
    public class GatePassHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        // Passes the portal has not seen yet are checked with the agent at most this often per address, so a flood of
        // made-up passes cannot be turned against the agent
        private static readonly PartitionedRateLimiter<string> Lookups = PartitionedRateLimiter.Create<string, string>(address =>
            RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

        private readonly GatePassIntrospector _introspector;
        private readonly IMemoryCache _cache;
        private readonly GatePassOptions _options;
        private readonly ItMembers _members;

        public GatePassHandler(IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions, ILoggerFactory logger, UrlEncoder encoder,
            GatePassIntrospector introspector, IMemoryCache cache, IOptions<GatePassOptions> options, ItMembers members)
            : base(schemeOptions, logger, encoder)
        {
            _introspector = introspector;
            _cache = cache;
            _options = options.Value;
            _members = members;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();

            var gatePass = header["Bearer ".Length..].Trim();
            if (gatePass.Length is < 16 or > 4096) return AuthenticateResult.Fail("Malformed login.");

            // Keyed by a hash, so the passes themselves are never kept in memory longer than the request
            var key = "gatepass:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gatePass)));
            if (!_cache.TryGetValue(key, out GatePassIdentity identity))
            {
                using var lease = Lookups.AttemptAcquire(Context.ClientAddress() ?? "?");
                if (!lease.IsAcquired) return AuthenticateResult.Fail("Too many login checks from this address. Wait a minute.");

                try
                {
                    identity = await _introspector.IntrospectAsync(gatePass, Context.RequestAborted);
                }
                catch (AgentUnavailableException ex)
                {
                    Logger.LogWarning(ex, "Gate pass check failed");
                    Context.Items["auth-unavailable"] = true;
                    return AuthenticateResult.Fail(ex.Message);
                }

                // A refusal is remembered briefly too, so a wrong pass cannot be used to flood the agent
                _cache.Set(key, identity, TimeSpan.FromSeconds(identity == null ? 15 : Math.Clamp(_options.CacheSeconds, 5, 300)));
            }

            if (identity == null) return AuthenticateResult.Fail("The login is not valid or has expired. Log in again in the client.");

            var lead = identity.IsAgentAdmin || _options.ItLeads.Contains(identity.Username, StringComparer.OrdinalIgnoreCase);
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, identity.Username),
                new(ClaimTypes.Name, identity.Username),
                new(Access.UserNameClaim, identity.Username),
                new(ClaimTypes.Role, Access.It)
            };
            if (lead) claims.Add(new Claim(ClaimTypes.Role, Access.ItLead));

            await _members.SeenAsync(identity.Username, lead, Context.RequestAborted);

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Access.GatePassScheme));
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Access.GatePassScheme));
        }

        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            var unavailable = Context.Items.ContainsKey("auth-unavailable");
            Response.StatusCode = unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status401Unauthorized;
            await Response.WriteAsJsonAsync(new
            {
                message = unavailable
                    ? "The helpdesk cannot check your login right now (the deploy agent did not answer). Try again in a minute."
                    : "Your login is not valid or has expired. Log in again."
            });
        }

        protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            await Response.WriteAsJsonAsync(new { message = "Only IT leads may do this." });
        }
    }
}
