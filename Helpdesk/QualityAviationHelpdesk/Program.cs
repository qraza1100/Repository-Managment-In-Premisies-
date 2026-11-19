using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using QualityAviationHelpdesk.Data;
using QualityAviationHelpdesk.Endpoints;
using QualityAviationHelpdesk.Security;
using QualityAviationHelpdesk.Services;
using System.Threading.RateLimiting;

// Q+ Helpdesk: tickets from every Q+ CMR user, worked on by IT from the Git client, watched by management.
// Deployed and run by the QA deploy agent, which publishes it over HTTPS with the server certificate and passes
// each request on to this process on 127.0.0.1 (see Helpdesk\README.md).

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
var configuration = builder.Configuration;
var development = builder.Environment.IsDevelopment();

var helpdeskConnection = configuration.GetConnectionString("Helpdesk");
var legacyConnection = configuration.GetConnectionString("Legacy");
if (string.IsNullOrWhiteSpace(helpdeskConnection) || string.IsNullOrWhiteSpace(legacyConnection))
{
    Console.Error.WriteLine("ConnectionStrings:Helpdesk and ConnectionStrings:Legacy must be set. On the server, set them in the deploy agent's " +
                            "Environment for this application (ConnectionStrings__Helpdesk, ConnectionStrings__Legacy), never in the repository.");
    return 1;
}

// A folder that outlives releases: attachments and the keys that protect sign-in cookies
var dataDirectory = Environment.GetEnvironmentVariable("QA_DATA_DIR") ?? configuration["Helpdesk:DataPath"];
if (string.IsNullOrWhiteSpace(dataDirectory)) dataDirectory = Path.Combine(AppContext.BaseDirectory, "App_Data");
Directory.CreateDirectory(dataDirectory);

var portal = configuration.GetSection("Portal").Get<PortalOptions>() ?? new PortalOptions();
builder.Services.Configure<PortalOptions>(configuration.GetSection("Portal"));
builder.Services.Configure<GatePassOptions>(configuration.GetSection("GatePass"));

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 60L * 1024 * 1024;
});
builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = 60L * 1024 * 1024);

// ------------------------------------------------------------------ data

// Compatibility level 120 keeps the generated SQL valid on SQL Server 2014 and later
builder.Services.AddPooledDbContextFactory<HelpdeskDb>(options => options.UseSqlServer(helpdeskConnection, sql =>
{
    sql.UseCompatibilityLevel(120);
    sql.CommandTimeout(30);
}));
builder.Services.AddPooledDbContextFactory<LegacyDb>(options => options
    .UseSqlServer(legacyConnection, sql =>
    {
        sql.UseCompatibilityLevel(120);
        sql.CommandTimeout(15);
    })
    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ILegacyDirectory, LegacyDirectory>();
builder.Services.AddSingleton<ChangeFeed>();
builder.Services.AddHostedService(services => services.GetRequiredService<ChangeFeed>());
builder.Services.AddSingleton<AuditVerifier>();
builder.Services.AddHostedService(services => services.GetRequiredService<AuditVerifier>());
builder.Services.AddSingleton<Writes>();
builder.Services.AddSingleton<Config>();
builder.Services.AddSingleton<ItMembers>();
builder.Services.AddSingleton(new AttachmentStore(dataDirectory));
builder.Services.AddSingleton<TicketService>();
builder.Services.AddSingleton<TodoService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<AutoClose>();
builder.Services.AddHostedService(services => services.GetRequiredService<AutoClose>());
builder.Services.AddSingleton<WebSignIn>();
builder.Services.AddSingleton<GatePassIntrospector>();

// Shared by every copy of the portal (a new release runs beside the live one for a moment), so a sign-in
// cookie stays valid across releases and restarts. Encrypted at rest with the service account's Windows key.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("QAHelpdesk")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();

// ------------------------------------------------------------------ who may do what

builder.Services.AddAuthentication(Access.WebScheme)
    .AddCookie(Access.WebScheme, cookie =>
    {
        cookie.Cookie.Name = development ? "QAHelpdesk" : "__Host-QAHelpdesk";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Strict;
        cookie.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        cookie.Cookie.Path = "/";
        cookie.LoginPath = "/login";
        cookie.AccessDeniedPath = "/denied";
        cookie.ExpireTimeSpan = TimeSpan.FromHours(Math.Clamp(portal.SessionHours, 1, 24));
        cookie.SlidingExpiration = true;
        cookie.Events.OnValidatePrincipal = context => context.HttpContext.RequestServices.GetRequiredService<WebSignIn>().ValidatePrincipalAsync(context);
        // Calls from scripts get a status code, not the sign-in page
        cookie.Events.OnRedirectToLogin = context => Redirect(context, StatusCodes.Status401Unauthorized);
        cookie.Events.OnRedirectToAccessDenied = context => Redirect(context, StatusCodes.Status403Forbidden);
    })
    .AddScheme<AuthenticationSchemeOptions, GatePassHandler>(Access.GatePassScheme, null);

builder.Services.AddAuthorization(authorization =>
{
    authorization.AddPolicy(Access.RequesterPolicy, p => p.AddAuthenticationSchemes(Access.WebScheme).RequireRole(Access.Requester));
    authorization.AddPolicy(Access.ExecutivePolicy, p => p.AddAuthenticationSchemes(Access.WebScheme).RequireRole(Access.Executive));
    authorization.AddPolicy(Access.WebPolicy, p => p.AddAuthenticationSchemes(Access.WebScheme).RequireRole(Access.Requester, Access.Executive));
    authorization.AddPolicy(Access.ItPolicy, p => p.AddAuthenticationSchemes(Access.GatePassScheme).RequireRole(Access.It));
    authorization.AddPolicy(Access.ItLeadPolicy, p => p.AddAuthenticationSchemes(Access.GatePassScheme).RequireRole(Access.ItLead));

    // Deny by default: an address without its own rule still needs a signed-in web user
    authorization.FallbackPolicy = new AuthorizationPolicyBuilder(Access.WebScheme).RequireAuthenticatedUser().Build();
});

builder.Services.AddRateLimiter(limits =>
{
    limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limits.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(new { message = "Too many requests. Wait a moment and try again." }, cancellationToken);
    };

    // Everything, per address: a ceiling no single PC should reach
    limits.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter("ip:" + (http.ClientAddress() ?? "?"), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1200, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));

    // The sign-in page per address (showing it counts too); each account also locks for 15 minutes after 5 wrong passwords
    limits.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(http.ClientAddress() ?? "?", _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
    }));
    // Changes per person (pages that also show things count only their POSTs)
    limits.AddPolicy("write", http => HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
        ? RateLimitPartition.GetNoLimiter("read")
        : RateLimitPartition.GetTokenBucketLimiter(Caller(http), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 40, TokensPerPeriod = 40, ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
    limits.AddPolicy("api", http => RateLimitPartition.GetFixedWindowLimiter(Caller(http), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
    }));
    // Long polls and live streams held open at once by one person (several tabs, the client)
    limits.AddPolicy("poll", http => RateLimitPartition.GetConcurrencyLimiter(Caller(http), _ => new ConcurrencyLimiterOptions
    {
        PermitLimit = 8, QueueLimit = 0
    }));
});

builder.Services.AddAntiforgery(antiforgery =>
{
    antiforgery.Cookie.Name = development ? "QAHelpdesk-AF" : "__Host-QAHelpdesk-AF";
    antiforgery.Cookie.SameSite = SameSiteMode.Strict;
    antiforgery.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

builder.Services.AddRazorPages(pages =>
{
    pages.Conventions.AuthorizePage("/Index", Access.WebPolicy);
    pages.Conventions.AuthorizeFolder("/Tickets", Access.RequesterPolicy);
    pages.Conventions.AuthorizePage("/Dashboard", Access.ExecutivePolicy);
    pages.Conventions.AllowAnonymousToPage("/Login");
    pages.Conventions.AllowAnonymousToPage("/Denied");
    pages.Conventions.AllowAnonymousToPage("/Error");
});
// Not over HTTPS (the default): pages echo what people type next to anti-forgery tokens, which compression can leak
builder.Services.AddResponseCompression();
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

// ------------------------------------------------------------------ start-up checks

app.Services.GetRequiredService<GatePassIntrospector>();
if (configuration.GetValue("Database:AutoMigrate", true))
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await SchemaMigrator.ApplyAsync(helpdeskConnection, app.Logger);
            break;
        }
        catch (Exception ex) when (attempt < 3)
        {
            app.Logger.LogWarning(ex, "The helpdesk database is not ready yet (attempt {Attempt} of 3)", attempt);
            await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
        }
        catch (Exception ex)
        {
            // Better not to start: the deploy agent then keeps the previous release live
            app.Logger.LogCritical(ex, "The helpdesk database schema could not be brought up to date");
            return 2;
        }
    }
}

// ------------------------------------------------------------------ the request pipeline

app.Use(WebEndpoints.HandleApiErrors);
if (!development)
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
// Friendly error pages for people; API callers keep their JSON or bare status codes
app.UseWhen(http => !IsApi(http.Request.Path), pages => pages.UseStatusCodePagesWithReExecute("/Error", "?code={0}"));

app.Use(async (http, next) =>
{
    var headers = http.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    headers["Cross-Origin-Resource-Policy"] = "same-origin";
    // No inline script or style anywhere in the portal, so nothing injected into a page could run
    headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
                                    "font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    var path = http.Request.Path;
    if (!path.StartsWithSegments("/css") && !path.StartsWithSegments("/js") && !path.StartsWithSegments("/img"))
        headers.CacheControl = "no-store";
    await next(http);
});

app.UseResponseCompression();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

WebEndpoints.Map(app);
ItApi.Map(app);
app.MapRazorPages();

app.Run();
return 0;

static bool IsApi(PathString path) =>
    path.StartsWithSegments("/api") || path.StartsWithSegments("/events") || path.StartsWithSegments("/files") || path.StartsWithSegments("/health");

static Task Redirect(RedirectContext<CookieAuthenticationOptions> context, int status)
{
    if (IsApi(context.Request.Path))
        context.Response.StatusCode = status;
    else
        context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
}

static string Caller(HttpContext http) =>
    http.User.Identity?.IsAuthenticated == true ? "user:" + http.User.UserName() : "ip:" + (http.ClientAddress() ?? "?");
