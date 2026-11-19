using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QualityAviationHelpdesk.Data;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Security;
using QualityAviationHelpdesk.Services;

namespace QualityAviationHelpdesk.Endpoints
{
    // The API behind the Helpdesk tab of the Git client (and its terminal menu). Every call carries the client's
    // gate pass; nothing here accepts a browser cookie, so a web page can never call it on someone's behalf.
    public static class ItApi
    {
        public static void Map(WebApplication app)
        {
            var it = app.MapGroup("/api/it").RequireAuthorization(Access.ItPolicy).RequireRateLimiting("api");
            var lead = it.MapGroup("").RequireAuthorization(Access.ItLeadPolicy);

            it.MapGet("/me", async (HttpContext http, Config config, ItMembers members, CancellationToken ct) => new ItMe
            {
                Username = http.User.UserName(),
                IsLead = http.User.IsItLead(),
                Members = await members.NamesAsync(ct),
                Settings = await config.SettingsAsync(ct),
                ServerTime = DateTime.UtcNow
            });

            // ------------------------------------------------------------ tickets

            it.MapGet("/tickets", (HttpContext http, TicketService tickets, string status, string assignee, string q, int? page, int? pageSize, CancellationToken ct) =>
                tickets.QueueAsync(status, assignee, q, page ?? 1, pageSize ?? 50, http.User.UserName(), ct));

            it.MapGet("/tickets/{id:int}", (int id, TicketService tickets, CancellationToken ct) => tickets.GetAsync(id, ct));

            it.MapPost("/tickets/{id:int}/actions/{action}", (HttpContext http, int id, string action, TicketActionRequest request, TicketService tickets, CancellationToken ct) =>
                tickets.ActAsync(id, action, request, Actor.From(http), ct)).RequireRateLimiting("write");

            it.MapPost("/tickets/{id:int}/edit", (HttpContext http, int id, TicketEditRequest request, TicketService tickets, CancellationToken ct) =>
                tickets.EditAsync(id, request, Actor.From(http), ct)).RequireRateLimiting("write");

            // multipart/form-data: body, internal (true/false), minutes, files
            it.MapPost("/tickets/{id:int}/comments", async (HttpContext http, int id, TicketService tickets, CancellationToken ct) =>
                {
                    var form = await http.Request.ReadFormAsync(ct);
                    var internalOnly = !string.Equals(form["internal"], "false", StringComparison.OrdinalIgnoreCase);
                    int.TryParse(form["minutes"], out var minutes);
                    return await tickets.CommentAsync(id, form["body"], internalOnly, minutes, form.Files.ToList(), Actor.From(http), ct);
                })
                .DisableAntiforgery()
                .WithMetadata(new RequestSizeLimitAttribute(60L * 1024 * 1024))
                .RequireRateLimiting("write");

            it.MapGet("/attachments/{id:guid}", async (Guid id, TicketService tickets, CancellationToken ct) =>
            {
                var (info, content) = await tickets.OpenAttachmentAsync(id, requesterId: null, ct);
                return Results.File(content, info.ContentType, info.FileName, enableRangeProcessing: false);
            });

            // ------------------------------------------------------------ form fields and settings (changes: leads only)

            it.MapGet("/fields", async (Config config, CancellationToken ct) => (await config.FieldsAsync(ct)).Select(Config.ToDto).ToList());

            lead.MapPost("/fields", async (HttpContext http, FieldDefinitionDto field, Config config, CancellationToken ct) =>
                (await config.SaveFieldAsync(field, Actor.From(http), ct)).Select(Config.ToDto).ToList()).RequireRateLimiting("write");

            lead.MapPost("/fields/{id:int}/move", async (HttpContext http, int id, int direction, Config config, CancellationToken ct) =>
                (await config.MoveFieldAsync(id, direction, Actor.From(http), ct)).Select(Config.ToDto).ToList()).RequireRateLimiting("write");

            it.MapGet("/settings", (Config config, CancellationToken ct) => config.SettingsAsync(ct));

            lead.MapPost("/settings", (HttpContext http, HelpdeskSettings settings, Config config, CancellationToken ct) =>
                config.SaveSettingsAsync(settings, Actor.From(http), ct)).RequireRateLimiting("write");

            // ------------------------------------------------------------ to-do

            it.MapGet("/todos", (TodoService todos, bool? done, bool? archived, CancellationToken ct) => todos.ListAsync(done ?? false, archived ?? false, ct));

            it.MapPost("/todos", (HttpContext http, TodoSaveRequest request, TodoService todos, CancellationToken ct) =>
                todos.SaveAsync(0, request, Actor.From(http), ct)).RequireRateLimiting("write");

            it.MapPost("/todos/{id:int}", (HttpContext http, int id, TodoSaveRequest request, TodoService todos, CancellationToken ct) =>
                todos.SaveAsync(id, request, Actor.From(http), ct)).RequireRateLimiting("write");

            it.MapPost("/todos/{id:int}/archive", (HttpContext http, int id, bool? restore, TodoSaveRequest request, TodoService todos, CancellationToken ct) =>
                todos.ArchiveAsync(id, restore != true, request?.Version, Actor.From(http), ct)).RequireRateLimiting("write");

            it.MapGet("/todos/{id:int}/history", (int id, TodoService todos, CancellationToken ct) => todos.HistoryAsync(id, ct));

            // ------------------------------------------------------------ staying in sync

            // Long poll: answers as soon as anything changes, or after 25 seconds with nothing
            it.MapGet("/changes", async (long? after, ChangeFeed feed, CancellationToken ct) =>
            {
                if (after is null or <= 0) return new ChangeBatch { Last = feed.Last };
                await feed.WaitAsync(after.Value, TimeSpan.FromSeconds(25), ct);
                return feed.Since(after.Value);
            }).RequireRateLimiting("poll");

            // ------------------------------------------------------------ audit (leads)

            lead.MapGet("/audit/verify", (AuditVerifier verifier, CancellationToken ct) => verifier.VerifyAsync(ct));

            lead.MapGet("/audit", async (IDbContextFactory<HelpdeskDb> factory, int? take, CancellationToken ct) =>
            {
                var count = Math.Clamp(take ?? 200, 1, 1000);
                await using var db = await factory.CreateDbContextAsync(ct);
                return await db.AuditEvents.AsNoTracking().OrderByDescending(e => e.Id).Take(count).Select(e => new TimelineEntry
                {
                    Id = e.Id, EntityType = e.EntityType, EntityId = e.EntityId, At = e.At, ActorKind = e.ActorKind, ActorName = e.ActorName,
                    Action = e.Action, Detail = e.Detail
                }).ToListAsync(ct);
            });
        }
    }
}
