using System.Security.Claims;

namespace QualityAviationHelpdesk.Security
{
    // Who may do what. Every request is checked against these on its own; nothing is trusted because of the
    // network it comes from or because an earlier request was allowed.
    //
    //   requester   any active Q+ CMR user (web, password from the legacy database): raise tickets, follow their own
    //   executive   Q+ CMR users holding one of Portal:ExecutiveRoles (or listed in Portal:ExecutiveUsers):
    //               the read-only productivity dashboard; no ticket content beyond summaries, no changes at all
    //   it          the IT team, signed in to the Git client (gate pass): review, execute, edit, to-do
    //   it-lead     deploy-agent administrators and Helpdesk:ItLeads: also form fields, settings, audit checks
    public static class Access
    {
        public const string WebScheme = "Cookies";
        public const string GatePassScheme = "GatePass";

        public const string Requester = "requester";
        public const string Executive = "executive";
        public const string It = "it";
        public const string ItLead = "it-lead";

        // Policies
        public const string RequesterPolicy = "Requester";
        public const string ExecutivePolicy = "Executive";
        public const string ItPolicy = "IT";
        public const string ItLeadPolicy = "ITLead";
        public const string WebPolicy = "Web";

        // Claims
        public const string UserNameClaim = "qa:username";
        public const string DepartmentClaim = "qa:department";
        public const string StampClaim = "qa:stamp";
        public const string LegacyRoleClaim = "qa:legacy-role";
        public const string CheckedClaim = "qa:checked";
        public const string SignedInClaim = "qa:signed-in";

        public static string Id(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);
        public static string DisplayName(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Name);
        public static string UserName(this ClaimsPrincipal user) => user.FindFirstValue(UserNameClaim) ?? user.Id();
        public static string Email(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email);
        public static string Department(this ClaimsPrincipal user) => user.FindFirstValue(DepartmentClaim);
        public static bool IsExecutive(this ClaimsPrincipal user) => user.IsInRole(Executive);
        public static bool IsItLead(this ClaimsPrincipal user) => user.IsInRole(ItLead);

        public static string ClientAddress(this HttpContext context)
        {
            var address = context.Connection.RemoteIpAddress;
            if (address == null) return null;
            return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        }
    }

    public class PortalOptions
    {
        // Legacy role names that open the dashboard. Deliberately empty here: configuration binding adds to a list
        // that already has items, so defaults in code could never be narrowed. The defaults are in appsettings.json.
        public List<string> ExecutiveRoles { get; set; } = new();

        // Individual legacy user names or e-mail addresses that also open it
        public List<string> ExecutiveUsers { get; set; } = new();

        // Legacy role names allowed to raise tickets; empty = every active user
        public List<string> RequesterRoles { get; set; } = new();

        // How often a signed-in web user is checked against the legacy database again
        public int RecheckSeconds { get; set; } = 60;

        // Idle time after which a web session ends, and the longest a session lasts however active it is
        public int SessionHours { get; set; } = 10;
        public int MaxSessionHours { get; set; } = 12;
    }

    public class GatePassOptions
    {
        // The deploy agent on this server; it checks gate passes with the repository API's own keys
        public string AgentUrl { get; set; } = "https://127.0.0.1:5003";

        // SHA-256 thumbprint of the server certificate (the same value as the client's ApiSettings:CertificateThumbprint)
        public string AgentThumbprint { get; set; } = "";

        // Client logins that are IT leads in addition to the deploy agent's administrators
        public List<string> ItLeads { get; set; } = new();

        public int CacheSeconds { get; set; } = 60;
    }
}
