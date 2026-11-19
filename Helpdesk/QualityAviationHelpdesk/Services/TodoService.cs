using Microsoft.EntityFrameworkCore;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;
using System.Globalization;

namespace QualityAviationHelpdesk.Services
{
    // The IT team's shared to-do board: who is doing what, what is blocked, what got done. Nothing is deleted;
    // finished items stay as Done and can be archived, and every change is in the audit trail.
    public class TodoService
    {
        private readonly IDbContextFactory<HelpdeskDb> _factory;
        private readonly Writes _writes;
        private readonly ItMembers _members;

        public TodoService(IDbContextFactory<HelpdeskDb> factory, Writes writes, ItMembers members)
        {
            _factory = factory;
            _writes = writes;
            _members = members;
        }

        public async Task<List<TodoDto>> ListAsync(bool includeDone, bool archived, CancellationToken cancellationToken)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var query = db.Todos.AsNoTracking().Where(t => t.Archived == archived);
            if (!includeDone && !archived)
            {
                // Done items stay on the board for a week, so the team sees what was finished lately
                var since = DateTime.UtcNow.AddDays(-7);
                query = query.Where(t => t.Status != TodoStatus.Done || t.CompletedAt >= since);
            }

            var items = await query.OrderBy(t => t.Status == TodoStatus.Done).ThenBy(t => t.Priority).ThenBy(t => t.DueOn == null).ThenBy(t => t.DueOn).ThenBy(t => t.Id)
                .Take(1000).ToListAsync(cancellationToken);
            return items.Select(ToDto).ToList();
        }

        public async Task<TodoDto> GetAsync(int id, CancellationToken cancellationToken)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var item = await db.Todos.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken) ?? throw new NotFoundException("That to-do no longer exists.");
            return ToDto(item);
        }

        // id 0 adds a new item
        public async Task<TodoDto> SaveAsync(int id, TodoSaveRequest request, Actor actor, CancellationToken cancellationToken)
        {
            var errors = new List<string>();
            var title = (request.Title ?? "").Trim();
            var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
            if (title.Length is 0 or > 200) errors.Add("The title is required (at most 200 characters).");
            if (notes?.Length > 4000) errors.Add("Notes can be at most 4000 characters.");
            if (!TodoStatus.All.Contains(request.Status)) errors.Add("The status is To do, Doing, Blocked or Done.");
            if (request.Priority is < 1 or > 4) errors.Add("Choose a priority from P1 to P4.");
            if (errors.Count > 0) throw new ValidationException(errors);

            var owner = await _members.CanonicalAsync(request.Owner, cancellationToken);

            var savedId = await _writes.RunAsync(async write =>
            {
                if (request.TicketId != null && !await write.Db.Tickets.AnyAsync(t => t.Id == request.TicketId, cancellationToken))
                    throw new ValidationException(new[] { $"Ticket {TicketNumbers.Format(request.TicketId.Value)} does not exist." });

                var now = AuditChain.Now();
                TodoItem item;
                var changes = new List<string>();
                if (id == 0)
                {
                    item = new TodoItem { CreatedBy = actor.Name, CreatedAt = now, Status = TodoStatus.Todo };
                    write.Db.Todos.Add(item);
                }
                else
                {
                    item = await write.Db.Todos.FirstOrDefaultAsync(t => t.Id == id, cancellationToken) ?? throw new NotFoundException("That to-do no longer exists.");
                    Versions.Expect(write.Db, item, request.Version);
                    if (item.Archived) throw new ValidationException(new[] { "An archived to-do cannot be changed. Restore it first." });
                }

                void Track(string what, string before, string after)
                {
                    if (id != 0 && before != after) changes.Add($"{what}: {before ?? "—"} → {after ?? "—"}");
                }

                Track("Title", item.Title, title);
                Track("Owner", item.Owner, owner);
                Track("Status", id == 0 ? null : TodoStatus.Label(item.Status), TodoStatus.Label(request.Status));
                Track("Priority", id == 0 ? null : Priorities.Label(item.Priority), Priorities.Label(request.Priority));
                Track("Due", item.DueOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), request.DueOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Track("Ticket", item.TicketId == null ? null : TicketNumbers.Format(item.TicketId.Value), request.TicketId == null ? null : TicketNumbers.Format(request.TicketId.Value));
                if (id != 0 && item.Notes != notes) changes.Add("Notes changed");

                if (request.Status == TodoStatus.Done && item.Status != TodoStatus.Done)
                {
                    item.CompletedAt = now;
                    item.CompletedBy = owner ?? actor.Name;
                }
                else if (request.Status != TodoStatus.Done)
                {
                    item.CompletedAt = null;
                    item.CompletedBy = null;
                }

                item.Title = title;
                item.Notes = notes;
                item.Owner = owner;
                item.Status = request.Status;
                item.Priority = (byte)request.Priority;
                item.DueOn = request.DueOn;
                item.TicketId = request.TicketId;
                item.UpdatedBy = actor.Name;
                item.UpdatedAt = now;

                if (id != 0 && changes.Count == 0) return item.Id;
                await write.Db.SaveChangesAsync(cancellationToken);

                write.Audit("todo", item.Id.ToString(CultureInfo.InvariantCulture), id == 0 ? "todo-added" : "todo-changed", actor,
                    id == 0 ? $"{title} ({Priorities.Label(request.Priority)}{(owner != null ? $", for {owner}" : "")})" : string.Join("; ", changes), false);
                write.Changed(ChangeTopics.Todo, item.Id);
                return item.Id;
            }, cancellationToken);

            return await GetAsync(savedId, cancellationToken);
        }

        public async Task<TodoDto> ArchiveAsync(int id, bool archive, string version, Actor actor, CancellationToken cancellationToken)
        {
            await _writes.RunAsync(async write =>
            {
                var item = await write.Db.Todos.FirstOrDefaultAsync(t => t.Id == id, cancellationToken) ?? throw new NotFoundException("That to-do no longer exists.");
                Versions.Expect(write.Db, item, version);
                if (item.Archived == archive) return 0;

                item.Archived = archive;
                item.UpdatedBy = actor.Name;
                item.UpdatedAt = AuditChain.Now();
                write.Audit("todo", id.ToString(CultureInfo.InvariantCulture), archive ? "todo-archived" : "todo-restored", actor, item.Title, false);
                write.Changed(ChangeTopics.Todo, id);
                return 0;
            }, cancellationToken);

            return await GetAsync(id, cancellationToken);
        }

        public async Task<List<TimelineEntry>> HistoryAsync(int id, CancellationToken cancellationToken)
        {
            var key = id.ToString(CultureInfo.InvariantCulture);
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            return await db.AuditEvents.AsNoTracking().Where(e => e.EntityType == "todo" && e.EntityId == key).OrderBy(e => e.Id)
                .Select(e => new TimelineEntry { Id = e.Id, EntityType = e.EntityType, EntityId = e.EntityId, At = e.At, ActorKind = e.ActorKind, ActorName = e.ActorName, Action = e.Action, Detail = e.Detail })
                .ToListAsync(cancellationToken);
        }

        private static TodoDto ToDto(TodoItem t) => new()
        {
            Id = t.Id,
            Version = Versions.Of(t.RowVersion),
            Title = t.Title,
            Notes = t.Notes,
            Owner = t.Owner,
            Status = t.Status,
            Priority = t.Priority,
            DueOn = t.DueOn,
            TicketId = t.TicketId,
            TicketNumber = t.TicketId == null ? null : TicketNumbers.Format(t.TicketId.Value),
            Archived = t.Archived,
            CreatedBy = t.CreatedBy,
            CreatedAt = t.CreatedAt,
            UpdatedBy = t.UpdatedBy,
            UpdatedAt = t.UpdatedAt,
            CompletedBy = t.CompletedBy,
            CompletedAt = t.CompletedAt
        };
    }
}
