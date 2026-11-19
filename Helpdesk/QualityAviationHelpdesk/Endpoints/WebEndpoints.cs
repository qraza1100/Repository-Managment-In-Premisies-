using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;
using QualityAviationHelpdesk.Security;
using QualityAviationHelpdesk.Services;
using System.Globalization;

namespace QualityAviationHelpdesk.Endpoints
{
    // The few non-page addresses the web portal needs: live updates, file downloads, dashboard data, health
    public static class WebEndpoints
    {
        // A live stream is ended after this long and the browser reconnects by itself, so the session behind it is
        // checked again (zero trust: no connection stays trusted just because it was once allowed)
        private static readonly TimeSpan StreamLifetime = TimeSpan.FromMinutes(5);

        public static void Map(WebApplication app)
        {
            app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "qa-helpdesk", version = Environment.GetEnvironmentVariable("QA_VERSION") ?? "dev" }))
                .AllowAnonymous().DisableRateLimiting();

            app.MapGet("/files/{id:guid}", async (HttpContext http, Guid id, bool? inline, TicketService tickets, CancellationToken ct) =>
            {
                var (info, content) = await tickets.OpenAttachmentAsync(id, http.User.Id(), ct);
                // Nothing a file contains may run as part of this site
                http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox";
                if (inline == true && AttachmentStore.IsImage(info.ContentType))
                {
                    http.Response.Headers.ContentDisposition = "inline";
                    return Results.Stream(content, info.ContentType);
                }
                return Results.File(content, info.ContentType, info.FileName);
            }).RequireAuthorization(Access.RequesterPolicy);

            app.MapGet("/api/web/dashboard", (int? days, DashboardService dashboard, CancellationToken ct) => dashboard.GetAsync(days ?? 30, ct))
                .RequireAuthorization(Access.ExecutivePolicy);

            // Server-sent events: tells an open page that what it shows has changed, without saying what (the page
            // asks again with the viewer's own rights). scope: mine | ticket (with id) | dashboard
            app.MapGet("/events", async (HttpContext http, string scope, int? id, ChangeFeed feed, IDbContextFactory<HelpdeskDb> factory, CancellationToken ct) =>
            {
                var user = http.User;
                var userId = user.Id();
                Func<ChangeDto, bool> relevant;
                HashSet<string> mine = null;
                var mineLoaded = DateTime.MinValue;

                async Task LoadMineAsync()
                {
                    await using var db = await factory.CreateDbContextAsync(ct);
                    var ids = await db.Tickets.AsNoTracking().Where(t => t.RequesterId == userId).OrderByDescending(t => t.Id).Take(500).Select(t => t.Id).ToListAsync(ct);
                    mine = ids.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToHashSet();
                    mineLoaded = DateTime.UtcNow;
                }

                switch (scope)
                {
                    case "dashboard" when user.IsExecutive():
                        relevant = c => c.Topic is ChangeTopics.Ticket or ChangeTopics.Todo;
                        break;
                    case "ticket" when id != null:
                        await using (var db = await factory.CreateDbContextAsync(ct))
                            if (!await db.Tickets.AnyAsync(t => t.Id == id && t.RequesterId == userId, ct)) return Results.NotFound();
                        var key = id.Value.ToString(CultureInfo.InvariantCulture);
                        relevant = c => c.Topic == ChangeTopics.Ticket && c.EntityId == key;
                        break;
                    case "mine":
                        await LoadMineAsync();
                        relevant = c => c.Topic == ChangeTopics.Ticket && mine.Contains(c.EntityId);
                        break;
                    default:
                        return Results.NotFound();
                }

                http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                http.Response.ContentType = "text/event-stream";
                http.Response.Headers.CacheControl = "no-cache, no-store";
                http.Response.Headers["X-Accel-Buffering"] = "no";

                var after = feed.Last;
                var ends = DateTime.UtcNow + StreamLifetime;
                await http.Response.WriteAsync("retry: 5000\n\n", ct);
                await http.Response.Body.FlushAsync(ct);

                try
                {
                    while (DateTime.UtcNow < ends)
                    {
                        var last = await feed.WaitAsync(after, TimeSpan.FromSeconds(20), ct);
                        if (last > after)
                        {
                            var batch = feed.Since(after);
                            after = batch.Last;
                            if (scope == "mine" && DateTime.UtcNow - mineLoaded > TimeSpan.FromMinutes(1)) await LoadMineAsync();
                            if (!batch.Reset && !batch.Changes.Any(relevant)) continue;
                            await http.Response.WriteAsync("event: changed\ndata: {}\n\n", ct);
                        }
                        else
                        {
                            await http.Response.WriteAsync(": ping\n\n", ct);
                        }
                        await http.Response.Body.FlushAsync(ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The page was closed
                }
                return Results.Empty;
            }).RequireAuthorization(Access.WebPolicy).RequireRateLimiting("poll");
        }

        // API errors as { message } with a status the caller can act on; anything unexpected is logged and hidden
        public static async Task HandleApiErrors(HttpContext http, RequestDelegate next)
        {
            try
            {
                await next(http);
            }
            catch (Exception ex) when (!http.Response.HasStarted && IsApi(http.Request.Path))
            {
                if (ex is OperationCanceledException && http.RequestAborted.IsCancellationRequested) return;

                var (status, message) = ex switch
                {
                    ValidationException v => (400, v.Message),
                    WorkflowException w => (409, w.Message),
                    ConflictException c => (409, c.Message),
                    DbUpdateConcurrencyException => (409, "Someone else changed this in the meantime. Reload to see their change, then try again."),
                    NotFoundException n => (404, n.Message),
                    AgentUnavailableException a => (503, a.Message),
                    SqlException or TimeoutException => (503, "The helpdesk database is not answering right now. Try again in a minute."),
                    BadHttpRequestException bad => (bad.StatusCode, bad.Message),
                    _ => (500, "The helpdesk could not complete the request. Details are in its log.")
                };

                if (status >= 500)
                    http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Helpdesk").LogError(ex, "{Method} {Path} failed", http.Request.Method, http.Request.Path);

                http.Response.Clear();
                http.Response.StatusCode = status;
                await http.Response.WriteAsJsonAsync(ex is ValidationException ve ? new { message, errors = ve.Errors } : (object)new { message });
            }
        }

        private static bool IsApi(PathString path) =>
            path.StartsWithSegments("/api") || path.StartsWithSegments("/files") || path.StartsWithSegments("/events");
    }
}
