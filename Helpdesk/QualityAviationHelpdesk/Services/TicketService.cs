using Microsoft.EntityFrameworkCore;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;
using QualityAviationHelpdesk.Security;
using System.Globalization;
using System.Security.Claims;

namespace QualityAviationHelpdesk.Services
{
    // Tickets: raised on the web by Q+ CMR users, reviewed and worked on by IT in the client.
    // Every read checks who is asking: a requester only ever sees their own tickets, and never IT's internal notes,
    // internal files or IT-only fields.
    public class TicketService
    {
        private const int MaxSummary = 200;
        private const int MaxDescription = 8000;
        private const int MaxComment = 4000;
        private static readonly string[] RequesterOnly = { FieldAudience.Requester };
        private static readonly string[] Everyone = { FieldAudience.Requester, FieldAudience.It };

        private readonly IDbContextFactory<HelpdeskDb> _factory;
        private readonly Writes _writes;
        private readonly Config _config;
        private readonly ILegacyDirectory _directory;
        private readonly AttachmentStore _files;
        private readonly ItMembers _members;

        public TicketService(IDbContextFactory<HelpdeskDb> factory, Writes writes, Config config, ILegacyDirectory directory, AttachmentStore files, ItMembers members)
        {
            _factory = factory;
            _writes = writes;
            _config = config;
            _directory = directory;
            _files = files;
            _members = members;
        }

        public sealed class NewTicket
        {
            public string Type { get; set; }
            public string Summary { get; set; }
            public string Description { get; set; }
            public int Priority { get; set; } = 3;
            public Dictionary<string, string> Fields { get; set; } = new();
        }

        // ================================================================== requester (web)

        public async Task<int> CreateAsync(ClaimsPrincipal user, NewTicket input, IReadOnlyList<IFormFile> files, Actor actor, CancellationToken cancellationToken)
        {
            var settings = await _config.SettingsAsync(cancellationToken);
            var definitions = await _config.FieldsAsync(cancellationToken);
            var errors = new List<string>();

            var type = CheckBasics(settings, input.Type, input.Summary, input.Description, input.Priority, errors);
            var values = FieldRules.Validate(definitions, type, RequesterOnly, input.Fields, errors);
            CheckFiles(files, settings, errors);
            if (errors.Count > 0) throw new ValidationException(errors);

            // Name, e-mail and department come from Q+ CMR user management, read now, not from the form
            var profile = await _directory.GetProfileAsync(user.Id(), fresh: true, cancellationToken);
            if (profile is not { CanSignIn: true }) throw new ValidationException(new[] { "Your Q+ CMR account is not active." });

            var stored = await StoreAsync(files, settings, cancellationToken);
            try
            {
                return await _writes.RunAsync(async write =>
                {
                    var now = AuditChain.Now();
                    var ticket = new Ticket
                    {
                        Type = type,
                        Summary = input.Summary.Trim(),
                        Description = input.Description.Trim(),
                        Priority = (byte)input.Priority,
                        Status = TicketStatus.New,
                        RequesterId = profile.Id,
                        RequesterUserName = profile.UserName,
                        RequesterName = profile.FullName,
                        RequesterEmail = profile.Email,
                        RequesterDepartment = profile.Department,
                        CreatedAt = now,
                        UpdatedAt = now,
                        DueAt = now.AddHours(settings.SlaHours[input.Priority - 1]),
                        FieldValues = values.Select(v => new TicketFieldValue { FieldKey = v.Key, Value = v.Value }).ToList()
                    };
                    write.Db.Tickets.Add(ticket);
                    await write.Db.SaveChangesAsync(cancellationToken);

                    AddAttachments(write.Db, ticket.Id, null, stored, internalOnly: false, actor);
                    write.Audit("ticket", Id(ticket), "created", actor,
                        $"{Priorities.Label(input.Priority)} · {type} · {ticket.Summary}" + (stored.Count > 0 ? $" · {stored.Count} file(s)" : ""), true);
                    write.Changed(ChangeTopics.Ticket, ticket.Id);
                    return ticket.Id;
                }, cancellationToken);
            }
            catch
            {
                _files.Discard(stored);
                throw;
            }
        }

        public async Task<(List<TicketListItem> Items, int Total, Dictionary<string, int> Counts)> MineAsync(string userId, string filter, int page, int pageSize,
            CancellationToken cancellationToken)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var mine = db.Tickets.AsNoTracking().Where(t => t.RequesterId == userId);

            var counts = await mine.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
            var query = filter switch
            {
                "resolved" => mine.Where(t => t.Status == TicketStatus.Resolved),
                "closed" => mine.Where(t => TicketStatus.Final.Contains(t.Status)),
                "all" => mine,
                _ => mine.Where(t => TicketStatus.Open.Contains(t.Status))
            };

            var total = await query.CountAsync(cancellationToken);
            var items = await query.OrderByDescending(t => t.UpdatedAt)
                .Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize)
                .Select(ListItem()).ToListAsync(cancellationToken);
            return (Finish(items), total, counts);
        }

        // null when it does not exist or is not theirs: the two are not told apart
        public async Task<TicketDetail> GetForRequesterAsync(string userId, int id, CancellationToken cancellationToken)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var ticket = await db.Tickets.AsNoTracking().Include(t => t.FieldValues).FirstOrDefaultAsync(t => t.Id == id && t.RequesterId == userId, cancellationToken);
            return ticket == null ? null : await BuildAsync(db, ticket, forIt: false, cancellationToken);
        }

        public async Task RequesterActAsync(ClaimsPrincipal user, int id, string action, string note, string version, Actor actor, CancellationToken cancellationToken)
        {
            if (action is not (TicketActions.Cancel or TicketActions.Confirm or TicketActions.Reopen))
                throw new WorkflowException("That is not something a requester can do.");
            var settings = await _config.SettingsAsync(cancellationToken);
            note = Clip(note, MaxComment);

            await _writes.RunAsync(async write =>
            {
                var ticket = await write.Db.Tickets.FirstOrDefaultAsync(t => t.Id == id && t.RequesterId == user.Id(), cancellationToken)
                             ?? throw new NotFoundException("Ticket not found.");
                Versions.Expect(write.Db, ticket, version);

                if (action == TicketActions.Reopen && !CanReopen(ticket, settings))
                    throw new WorkflowException($"A resolved ticket can be reopened for {settings.ReopenDays} days. Please raise a new ticket instead.");

                var from = ticket.Status;
                ticket.Status = Workflow.Apply(ticket.Status, action, it: false, note);
                var now = AuditChain.Now();
                ticket.UpdatedAt = now;

                switch (action)
                {
                    case TicketActions.Cancel:
                        ticket.ClosedAt = now;
                        ticket.StatusReason = note ?? "Withdrawn by the requester";
                        break;
                    case TicketActions.Confirm:
                        ticket.ClosedAt = now;
                        break;
                    case TicketActions.Reopen:
                        Reopen(ticket, note);
                        break;
                }

                if (note != null) AddComment(write.Db, ticket.Id, actor, note, internalOnly: false, 0, now);
                write.Audit("ticket", Id(ticket), action, actor, $"{TicketStatus.Label(from)} → {TicketStatus.Label(ticket.Status)}" + (note != null ? $": {note}" : ""), true);
                write.Changed(ChangeTopics.Ticket, ticket.Id);
                return 0;
            }, cancellationToken);
        }

        public async Task RequesterCommentAsync(ClaimsPrincipal user, int id, string body, IReadOnlyList<IFormFile> files, Actor actor, CancellationToken cancellationToken)
        {
            var settings = await _config.SettingsAsync(cancellationToken);
            body = Clip(body, MaxComment + 1);
            var errors = new List<string>();
            if (body == null && (files == null || files.Count == 0)) errors.Add("Write a message or attach a file.");
            if (body?.Length > MaxComment) errors.Add($"A message can be at most {MaxComment} characters.");
            CheckFiles(files, settings, errors);
            if (errors.Count > 0) throw new ValidationException(errors);

            await using (var db = await _factory.CreateDbContextAsync(cancellationToken))
            {
                var status = await db.Tickets.Where(t => t.Id == id && t.RequesterId == user.Id()).Select(t => t.Status).FirstOrDefaultAsync(cancellationToken)
                             ?? throw new NotFoundException("Ticket not found.");
                if (TicketStatus.Final.Contains(status))
                    throw new ValidationException(new[] { "This ticket is closed, so it takes no more messages. Raise a new ticket if you need more help." });
            }

            var stored = await StoreAsync(files, settings, cancellationToken);
            try
            {
                await _writes.RunAsync(async write =>
                {
                    var ticket = await write.Db.Tickets.FirstAsync(t => t.Id == id, cancellationToken);
                    var now = AuditChain.Now();
                    ticket.UpdatedAt = now;
                    var comment = AddComment(write.Db, id, actor, body ?? "(file attached)", internalOnly: false, 0, now);
                    await write.Db.SaveChangesAsync(cancellationToken);
                    AddAttachments(write.Db, id, comment.Id, stored, internalOnly: false, actor);

                    write.Audit("ticket", Id(ticket), "commented", actor, Preview(body) + (stored.Count > 0 ? $" ({stored.Count} file(s))" : ""), true);
                    write.Changed(ChangeTopics.Ticket, id);
                    return 0;
                }, cancellationToken);
            }
            catch
            {
                _files.Discard(stored);
                throw;
            }
        }

        // ================================================================== IT (client)

        public async Task<TicketPage> QueueAsync(string status, string assignee, string search, int page, int pageSize, string me, CancellationToken cancellationToken)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var all = db.Tickets.AsNoTracking();
            var now = DateTime.UtcNow;

            var query = status switch
            {
                null or "" or "open" => all.Where(t => TicketStatus.Open.Contains(t.Status)),
                "overdue" => all.Where(t => TicketStatus.Open.Contains(t.Status) && t.DueAt < now),
                "all" => all,
                _ when TicketStatus.All.Contains(status) => all.Where(t => t.Status == status),
                _ => throw new ValidationException(new[] { $"Unknown status filter \"{status}\"." })
            };

            query = assignee switch
            {
                null or "" => query,
                "me" => query.Where(t => t.Assignee == me),
                "none" => query.Where(t => t.Assignee == null),
                _ => query.Where(t => t.Assignee == assignee)
            };

            search = (search ?? "").Trim();
            if (search.Length > 0)
            {
                if (TicketNumbers.TryParse(search, out var number))
                    query = query.Where(t => t.Id == number);
                else
                {
                    if (search.Length > 100) search = search[..100];
                    query = query.Where(t => t.Summary.Contains(search) || t.RequesterName.Contains(search) || t.RequesterDepartment.Contains(search) ||
                                             t.Type.Contains(search) || t.RequesterUserName.Contains(search));
                }
            }

            var counts = await all.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
            counts["overdue"] = await all.CountAsync(t => TicketStatus.Open.Contains(t.Status) && t.DueAt < now, cancellationToken);

            var ordered = status is null or "" or "open" or "overdue" || TicketStatus.IsOpen(status)
                ? query.OrderBy(t => t.Priority).ThenBy(t => t.DueAt).ThenBy(t => t.Id)
                : query.OrderByDescending(t => t.UpdatedAt);

            pageSize = Math.Clamp(pageSize, 10, 200);
            page = Math.Max(page, 1);
            return new TicketPage
            {
                Total = await query.CountAsync(cancellationToken),
                Page = page,
                PageSize = pageSize,
                Counts = counts,
                Items = Finish(await ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(ListItem()).ToListAsync(cancellationToken))
            };
        }

        public async Task<TicketDetail> GetAsync(int id, CancellationToken cancellationToken)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var ticket = await db.Tickets.AsNoTracking().Include(t => t.FieldValues).FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                         ?? throw new NotFoundException($"Ticket {TicketNumbers.Format(id)} does not exist.");
            return await BuildAsync(db, ticket, forIt: true, cancellationToken);
        }

        public async Task<TicketDetail> ActAsync(int id, string action, TicketActionRequest request, Actor actor, CancellationToken cancellationToken)
        {
            request ??= new TicketActionRequest();
            var settings = await _config.SettingsAsync(cancellationToken);
            var note = Clip(request.Note, MaxComment);
            if (request.MinutesSpent is < 0 or > 1440) throw new ValidationException(new[] { "Time spent is 0 to 1440 minutes." });

            string assignee = null;
            if (action == TicketActions.Assign) assignee = await _members.CanonicalAsync(request.Assignee, cancellationToken);
            if (action == TicketActions.Priority && request.Priority is not (>= 1 and <= 4))
                throw new ValidationException(new[] { "Choose a priority from P1 to P4." });

            await _writes.RunAsync(async write =>
            {
                var ticket = await write.Db.Tickets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                             ?? throw new NotFoundException($"Ticket {TicketNumbers.Format(id)} does not exist.");
                Versions.Expect(write.Db, ticket, request.Version);

                var from = ticket.Status;
                ticket.Status = Workflow.Apply(ticket.Status, action, it: true, note);
                var now = AuditChain.Now();
                var said = new List<string>();
                if (from != ticket.Status) said.Add($"{TicketStatus.Label(from)} → {TicketStatus.Label(ticket.Status)}");
                // Reasons the requester should read; other notes stay internal
                var publicNote = action is TicketActions.Reject or TicketActions.Resolve or TicketActions.Hold or TicketActions.Reopen;

                switch (action)
                {
                    case TicketActions.Accept:
                        ticket.AcceptedAt ??= now;
                        if (request.Priority is >= 1 and <= 4 && request.Priority != ticket.Priority) SetPriority(ticket, request.Priority.Value, settings, said);
                        break;
                    case TicketActions.Reject:
                        ticket.StatusReason = note;
                        ticket.ClosedAt = now;
                        break;
                    case TicketActions.Start:
                        ticket.AcceptedAt ??= now;
                        ticket.StartedAt ??= now;
                        if (ticket.Assignee == null) SetAssignee(ticket, actor.Id, said);
                        break;
                    case TicketActions.Hold:
                        ticket.StatusReason = note;
                        break;
                    case TicketActions.Resolve:
                        ticket.Resolution = note;
                        ticket.ResolvedAt = now;
                        // The one who completes it is who the dashboard credits
                        ticket.ResolvedBy = actor.Id;
                        ticket.AcceptedAt ??= now;
                        ticket.StartedAt ??= now;
                        if (ticket.Assignee == null) SetAssignee(ticket, actor.Id, said);
                        break;
                    case TicketActions.Close:
                        ticket.ClosedAt = now;
                        break;
                    case TicketActions.Reopen:
                        Reopen(ticket, note);
                        break;
                    case TicketActions.Assign:
                        SetAssignee(ticket, assignee, said);
                        break;
                    case TicketActions.Take:
                        SetAssignee(ticket, actor.Id, said);
                        break;
                    case TicketActions.Priority:
                        SetPriority(ticket, request.Priority!.Value, settings, said);
                        break;
                }

                ticket.MinutesSpent += request.MinutesSpent;
                ticket.UpdatedAt = now;
                if (note != null || request.MinutesSpent > 0)
                    AddComment(write.Db, ticket.Id, actor, note ?? $"Worked {request.MinutesSpent} min", internalOnly: !publicNote, request.MinutesSpent, now);

                if (publicNote && note != null) said.Add(note);
                if (request.MinutesSpent > 0) said.Add($"{request.MinutesSpent} min logged");
                write.Audit("ticket", Id(ticket), action, actor, said.Count > 0 ? string.Join(" · ", said) : TicketActions.Label(action), visibleToRequester: true);
                write.Changed(ChangeTopics.Ticket, ticket.Id);
                return 0;
            }, cancellationToken);

            return await GetAsync(id, cancellationToken);
        }

        public async Task<TicketDetail> EditAsync(int id, TicketEditRequest request, Actor actor, CancellationToken cancellationToken)
        {
            var settings = await _config.SettingsAsync(cancellationToken);
            var definitions = await _config.FieldsAsync(cancellationToken);
            var errors = new List<string>();
            var type = CheckBasics(settings, request.Type, request.Summary, request.Description, request.Priority, errors);
            var values = FieldRules.Validate(definitions, type, Everyone, request.Fields, errors);
            if (errors.Count > 0) throw new ValidationException(errors);

            await _writes.RunAsync(async write =>
            {
                var ticket = await write.Db.Tickets.Include(t => t.FieldValues).FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                             ?? throw new NotFoundException($"Ticket {TicketNumbers.Format(id)} does not exist.");
                Versions.Expect(write.Db, ticket, request.Version);

                var changes = new List<string>();
                var visible = new List<string>();
                void Note(string what, string before, string after, bool requesterSees)
                {
                    if (before == after) return;
                    changes.Add($"{what}: {Preview(before) ?? "—"} → {Preview(after) ?? "—"}");
                    if (requesterSees) visible.Add(what);
                }

                Note("Type", ticket.Type, type, true);
                Note("Summary", ticket.Summary, request.Summary.Trim(), true);
                Note("Description", ticket.Description, request.Description.Trim(), true);
                ticket.Type = type;
                ticket.Summary = request.Summary.Trim();
                ticket.Description = request.Description.Trim();
                if (request.Priority != ticket.Priority)
                {
                    // The target follows the priority; for a finished ticket that would rewrite whether it was met
                    if (!TicketStatus.IsOpen(ticket.Status))
                        throw new ValidationException(new[] { "The priority of a ticket can only change while it is open; reopen it first." });
                    var said = new List<string>();
                    SetPriority(ticket, request.Priority, settings, said);
                    changes.AddRange(said);
                    visible.Add("Priority");
                }

                // Values of fields that are hidden now, or do not apply to this type, are kept as they were
                var editable = definitions.Where(f => f.Active && f.AppliesToType(type)).Select(f => f.Key).ToHashSet();
                var current = ticket.FieldValues.ToDictionary(v => v.FieldKey, v => v.Value);
                foreach (var field in definitions.Where(f => editable.Contains(f.Key)))
                {
                    current.TryGetValue(field.Key, out var before);
                    values.TryGetValue(field.Key, out var after);
                    Note(field.Label, before, after, field.Audience == FieldAudience.Requester);
                }
                foreach (var kept in current.Where(c => !editable.Contains(c.Key))) values[kept.Key] = kept.Value;

                if (changes.Count == 0) return 0;

                ticket.FieldValues.Clear();
                foreach (var (key, value) in values) ticket.FieldValues.Add(new TicketFieldValue { TicketId = ticket.Id, FieldKey = key, Value = value });
                ticket.UpdatedAt = AuditChain.Now();

                write.Audit("ticket", Id(ticket), "edited", actor, string.Join("; ", changes), visibleToRequester: false);
                if (visible.Count > 0) write.Audit("ticket", Id(ticket), "details-updated", actor, $"IT updated: {string.Join(", ", visible.Distinct())}", visibleToRequester: true);
                write.Changed(ChangeTopics.Ticket, ticket.Id);
                return 0;
            }, cancellationToken);

            return await GetAsync(id, cancellationToken);
        }

        public async Task<TicketDetail> CommentAsync(int id, string body, bool internalOnly, int minutes, IReadOnlyList<IFormFile> files, Actor actor, CancellationToken cancellationToken)
        {
            var settings = await _config.SettingsAsync(cancellationToken);
            body = Clip(body, MaxComment + 1);
            var errors = new List<string>();
            if (body == null && (files == null || files.Count == 0) && minutes == 0) errors.Add("Write a note, attach a file or log time.");
            if (body?.Length > MaxComment) errors.Add($"A note can be at most {MaxComment} characters.");
            if (minutes is < 0 or > 1440) errors.Add("Time spent is 0 to 1440 minutes.");
            CheckFiles(files, settings, errors);
            if (errors.Count > 0) throw new ValidationException(errors);

            var stored = await StoreAsync(files, settings, cancellationToken);
            try
            {
                await _writes.RunAsync(async write =>
                {
                    var ticket = await write.Db.Tickets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                                 ?? throw new NotFoundException($"Ticket {TicketNumbers.Format(id)} does not exist.");
                    var now = AuditChain.Now();
                    ticket.UpdatedAt = now;
                    ticket.MinutesSpent += minutes;

                    var comment = AddComment(write.Db, id, actor, body ?? (minutes > 0 ? $"Worked {minutes} min" : "(file attached)"), internalOnly, minutes, now);
                    await write.Db.SaveChangesAsync(cancellationToken);
                    AddAttachments(write.Db, id, comment.Id, stored, internalOnly, actor);

                    var detail = internalOnly ? "Internal note" : Preview(body) ?? "File attached";
                    if (stored.Count > 0) detail += $" ({stored.Count} file(s))";
                    if (minutes > 0) detail += $" · {minutes} min logged";
                    write.Audit("ticket", Id(ticket), internalOnly ? "noted" : "commented", actor, detail, visibleToRequester: !internalOnly);
                    write.Changed(ChangeTopics.Ticket, id);
                    return 0;
                }, cancellationToken);
            }
            catch
            {
                _files.Discard(stored);
                throw;
            }

            return await GetAsync(id, cancellationToken);
        }

        // requesterId: null for IT (sees every file); otherwise only that requester's own, non-internal files
        public async Task<(Attachment Info, Stream Content)> OpenAttachmentAsync(Guid id, string requesterId, CancellationToken cancellationToken)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var query = db.Attachments.AsNoTracking().Where(a => a.Id == id);
            if (requesterId != null)
                query = query.Where(a => !a.Internal && db.Tickets.Any(t => t.Id == a.TicketId && t.RequesterId == requesterId));

            var attachment = await query.FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("File not found.");
            return (attachment, _files.Open(attachment.StoredPath));
        }

        // ================================================================== shared

        private async Task<TicketDetail> BuildAsync(HelpdeskDb db, Ticket ticket, bool forIt, CancellationToken cancellationToken)
        {
            var settings = await _config.SettingsAsync(cancellationToken);
            var definitions = await _config.FieldsAsync(cancellationToken);
            var key = Id(ticket);

            var comments = db.Comments.AsNoTracking().Where(c => c.TicketId == ticket.Id);
            var files = db.Attachments.AsNoTracking().Where(a => a.TicketId == ticket.Id);
            var events = db.AuditEvents.AsNoTracking().Where(e => e.EntityType == "ticket" && e.EntityId == key);
            if (!forIt)
            {
                comments = comments.Where(c => !c.Internal);
                files = files.Where(a => !a.Internal);
                events = events.Where(e => e.VisibleToRequester);
            }

            var visibleFields = forIt
                ? ticket.FieldValues
                : ticket.FieldValues.Where(v => definitions.Any(d => d.Key == v.FieldKey && d.Audience == FieldAudience.Requester));

            var allowed = Workflow.Allowed(ticket.Status, forIt).ToList();
            if (!forIt && !CanReopen(ticket, settings)) allowed.Remove(TicketActions.Reopen);

            return new TicketDetail
            {
                Id = ticket.Id,
                Number = TicketNumbers.Format(ticket.Id),
                Version = Versions.Of(ticket.RowVersion),
                Type = ticket.Type,
                Summary = ticket.Summary,
                Description = ticket.Description,
                Priority = ticket.Priority,
                Status = ticket.Status,
                StatusReason = ticket.StatusReason,
                Resolution = ticket.Resolution,
                RequesterName = ticket.RequesterName,
                RequesterUserName = ticket.RequesterUserName,
                RequesterEmail = ticket.RequesterEmail,
                Department = ticket.RequesterDepartment,
                Assignee = ticket.Assignee,
                ResolvedBy = ticket.ResolvedBy,
                CreatedAt = ticket.CreatedAt,
                UpdatedAt = ticket.UpdatedAt,
                AcceptedAt = ticket.AcceptedAt,
                StartedAt = ticket.StartedAt,
                ResolvedAt = ticket.ResolvedAt,
                ClosedAt = ticket.ClosedAt,
                DueAt = ticket.DueAt,
                Overdue = TicketStatus.IsOpen(ticket.Status) && ticket.DueAt < DateTime.UtcNow,
                MinutesSpent = forIt ? ticket.MinutesSpent : 0,
                ReopenCount = ticket.ReopenCount,
                Fields = visibleFields.ToDictionary(v => v.FieldKey, v => v.Value),
                Comments = await comments.OrderBy(c => c.Id).Select(c => new CommentDto
                {
                    Id = c.Id,
                    AuthorKind = c.AuthorKind,
                    AuthorName = c.AuthorName,
                    Body = c.Body,
                    Internal = c.Internal,
                    MinutesSpent = forIt ? c.MinutesSpent : 0,
                    CreatedAt = c.CreatedAt
                }).ToListAsync(cancellationToken),
                Attachments = await files.OrderBy(a => a.CreatedAt).Select(a => new AttachmentDto
                {
                    Id = a.Id,
                    CommentId = a.CommentId,
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    SizeBytes = a.SizeBytes,
                    Internal = a.Internal,
                    UploadedByName = a.UploadedByName,
                    CreatedAt = a.CreatedAt
                }).ToListAsync(cancellationToken),
                Timeline = await events.OrderBy(e => e.Id).Select(e => new TimelineEntry
                {
                    Id = e.Id,
                    EntityType = e.EntityType,
                    EntityId = e.EntityId,
                    At = e.At,
                    ActorKind = e.ActorKind,
                    ActorName = e.ActorName,
                    Action = e.Action,
                    Detail = e.Detail
                }).ToListAsync(cancellationToken),
                AllowedActions = allowed
            };
        }

        private static System.Linq.Expressions.Expression<Func<Ticket, TicketListItem>> ListItem() => t => new TicketListItem
        {
            Id = t.Id,
            Type = t.Type,
            Summary = t.Summary,
            Priority = t.Priority,
            Status = t.Status,
            RequesterName = t.RequesterName,
            Department = t.RequesterDepartment,
            Assignee = t.Assignee,
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt,
            DueAt = t.DueAt
        };

        private static List<TicketListItem> Finish(List<TicketListItem> items)
        {
            var now = DateTime.UtcNow;
            foreach (var item in items)
            {
                item.Number = TicketNumbers.Format(item.Id);
                item.Overdue = TicketStatus.IsOpen(item.Status) && item.DueAt < now;
            }
            return items;
        }

        private static string CheckBasics(HelpdeskSettings settings, string type, string summary, string description, int priority, List<string> errors)
        {
            var known = settings.TicketTypes.FirstOrDefault(t => string.Equals(t, (type ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (known == null) errors.Add("Choose what kind of request this is.");
            var s = (summary ?? "").Trim();
            if (s.Length < 5 || s.Length > MaxSummary) errors.Add($"The summary needs 5 to {MaxSummary} characters.");
            var d = (description ?? "").Trim();
            if (d.Length < 10 || d.Length > MaxDescription) errors.Add($"Describe the problem or requirement in 10 to {MaxDescription} characters.");
            if (priority is < 1 or > 4) errors.Add("Choose a priority.");
            return known;
        }

        private static void CheckFiles(IReadOnlyList<IFormFile> files, HelpdeskSettings settings, List<string> errors)
        {
            if (files == null || files.Count == 0) return;
            if (files.Count > AttachmentStore.MaxFilesPerUpload) errors.Add($"Attach at most {AttachmentStore.MaxFilesPerUpload} files at a time.");
            var max = settings.MaxAttachmentMB * 1024L * 1024L;
            foreach (var file in files.Where(f => f.Length > max)) errors.Add($"\"{file.FileName}\" is larger than {settings.MaxAttachmentMB} MB.");
        }

        private async Task<List<AttachmentStore.Stored>> StoreAsync(IReadOnlyList<IFormFile> files, HelpdeskSettings settings, CancellationToken cancellationToken)
        {
            var stored = new List<AttachmentStore.Stored>();
            if (files == null) return stored;
            try
            {
                foreach (var file in files.Where(f => f.Length > 0))
                {
                    await using var content = file.OpenReadStream();
                    stored.Add(await _files.SaveAsync(file.FileName, content, settings.MaxAttachmentMB * 1024L * 1024L, cancellationToken));
                }
                return stored;
            }
            catch
            {
                _files.Discard(stored);
                throw;
            }
        }

        private static void AddAttachments(HelpdeskDb db, int ticketId, int? commentId, List<AttachmentStore.Stored> stored, bool internalOnly, Actor actor)
        {
            foreach (var file in stored)
                db.Attachments.Add(new Attachment
                {
                    Id = file.Id,
                    TicketId = ticketId,
                    CommentId = commentId,
                    FileName = file.FileName,
                    ContentType = file.ContentType,
                    SizeBytes = file.SizeBytes,
                    Sha256 = file.Sha256,
                    StoredPath = file.StoredPath,
                    Internal = internalOnly,
                    UploadedByKind = actor.Kind,
                    UploadedById = actor.Id,
                    UploadedByName = actor.Name,
                    CreatedAt = AuditChain.Now()
                });
        }

        private static Comment AddComment(HelpdeskDb db, int ticketId, Actor actor, string body, bool internalOnly, int minutes, DateTime at)
        {
            var comment = new Comment
            {
                TicketId = ticketId,
                AuthorKind = actor.Kind,
                AuthorId = actor.Id,
                AuthorName = actor.Name,
                Body = body,
                Internal = internalOnly,
                MinutesSpent = minutes,
                CreatedAt = at
            };
            db.Comments.Add(comment);
            return comment;
        }

        private static void SetAssignee(Ticket ticket, string assignee, List<string> said)
        {
            if (ticket.Assignee == assignee) return;
            said.Add(assignee == null ? $"Unassigned (was {ticket.Assignee})" : $"Assigned to {assignee}" + (ticket.Assignee != null ? $" (was {ticket.Assignee})" : ""));
            ticket.Assignee = assignee;
        }

        // The resolution target always counts from when the ticket was raised
        private static void SetPriority(Ticket ticket, int priority, HelpdeskSettings settings, List<string> said)
        {
            if (ticket.Priority == priority) return;
            said.Add($"Priority {Priorities.Label(ticket.Priority)} → {Priorities.Label(priority)}");
            ticket.Priority = (byte)priority;
            ticket.DueAt = ticket.CreatedAt.AddHours(settings.SlaHours[priority - 1]);
        }

        private static void Reopen(Ticket ticket, string note)
        {
            ticket.ReopenCount++;
            ticket.ResolvedAt = null;
            ticket.ResolvedBy = null;
            ticket.ClosedAt = null;
            ticket.StatusReason = note;
        }

        private static bool CanReopen(Ticket ticket, HelpdeskSettings settings) =>
            ticket.Status == TicketStatus.Resolved && ticket.ResolvedAt != null && settings.ReopenDays > 0 &&
            DateTime.UtcNow - ticket.ResolvedAt.Value <= TimeSpan.FromDays(settings.ReopenDays);

        private static string Id(Ticket ticket) => ticket.Id.ToString(CultureInfo.InvariantCulture);

        private static string Clip(string text, int length)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            return text.Length <= length ? text : text[..length];
        }

        private static string Preview(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim().Replace('\r', ' ').Replace('\n', ' ');
            return text.Length <= 160 ? text : text[..157] + "...";
        }
    }
}
