using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;
using System.Globalization;

namespace QualityAviationHelpdesk.Services
{
    // Settings and form fields that IT changes at run time from the client. Read on almost every request, so they
    // are cached; the cache is dropped on every save here, and expires within seconds for changes made by another
    // running copy of the portal.
    public class Config
    {
        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);
        private const string SettingsKey = "config:settings";
        private const string FieldsKey = "config:fields";

        private readonly IDbContextFactory<HelpdeskDb> _factory;
        private readonly IMemoryCache _cache;
        private readonly Writes _writes;

        public Config(IDbContextFactory<HelpdeskDb> factory, IMemoryCache cache, Writes writes)
        {
            _factory = factory;
            _cache = cache;
            _writes = writes;
        }

        public async Task<HelpdeskSettings> SettingsAsync(CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue(SettingsKey, out HelpdeskSettings cached)) return cached;

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var rows = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
            var settings = new HelpdeskSettings();
            if (rows.TryGetValue("ticket.types", out var types)) settings.TicketTypes = FieldDefinition.Lines(types);
            if (settings.TicketTypes.Count == 0) settings.TicketTypes = new() { "Other" };
            if (rows.TryGetValue("sla.hours", out var sla))
            {
                var hours = sla.Split(',').Select(h => int.TryParse(h.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
                if (hours.Length == 4 && hours.All(h => h > 0)) settings.SlaHours = hours;
            }
            settings.ReopenDays = Number(rows, "reopen.days", settings.ReopenDays);
            settings.AutoCloseDays = Number(rows, "autoclose.days", settings.AutoCloseDays);
            settings.MaxAttachmentMB = Number(rows, "attachments.maxmb", settings.MaxAttachmentMB);

            _cache.Set(SettingsKey, settings, CacheFor);
            return settings;
        }

        public async Task<HelpdeskSettings> SaveSettingsAsync(HelpdeskSettings settings, Actor actor, CancellationToken cancellationToken)
        {
            var types = (settings.TicketTypes ?? new()).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).ToList();
            var errors = new List<string>();
            if (types.Count is 0 or > 30) errors.Add("Give between 1 and 30 ticket types.");
            if (types.Any(t => t.Length > 60)) errors.Add("A ticket type can be at most 60 characters.");
            if (types.Distinct(StringComparer.OrdinalIgnoreCase).Count() != types.Count) errors.Add("Every ticket type must be different.");
            if (settings.SlaHours is not { Length: 4 } || settings.SlaHours.Any(h => h is < 1 or > 2000)) errors.Add("Give 4 resolution targets (P1 to P4), each 1 to 2000 hours.");
            if (settings.ReopenDays is < 0 or > 90) errors.Add("Reopening is allowed for 0 to 90 days.");
            if (settings.AutoCloseDays is < 0 or > 90) errors.Add("Auto-close after 0 (never) to 90 days.");
            if (settings.MaxAttachmentMB is < 1 or > 50) errors.Add("Attachments can be 1 to 50 MB.");
            if (errors.Count > 0) throw new ValidationException(errors);

            var before = await SettingsAsync(cancellationToken);
            var values = new Dictionary<string, string>
            {
                ["ticket.types"] = string.Join('\n', types),
                ["sla.hours"] = string.Join(',', settings.SlaHours),
                ["reopen.days"] = settings.ReopenDays.ToString(CultureInfo.InvariantCulture),
                ["autoclose.days"] = settings.AutoCloseDays.ToString(CultureInfo.InvariantCulture),
                ["attachments.maxmb"] = settings.MaxAttachmentMB.ToString(CultureInfo.InvariantCulture)
            };

            await _writes.RunAsync(async write =>
            {
                var rows = await write.Db.Settings.ToDictionaryAsync(s => s.Key, cancellationToken);
                var changed = new List<string>();
                foreach (var (key, value) in values)
                {
                    if (rows.TryGetValue(key, out var row))
                    {
                        if (row.Value == value) continue;
                        row.Value = value;
                        row.UpdatedBy = actor.Name;
                        row.UpdatedAt = AuditChain.Now();
                    }
                    else
                    {
                        write.Db.Settings.Add(new Setting { Key = key, Value = value, UpdatedBy = actor.Name, UpdatedAt = AuditChain.Now() });
                    }
                    changed.Add(key);
                }

                if (changed.Count > 0)
                {
                    write.Audit("setting", "helpdesk", "settings-changed", actor,
                        $"Types: {string.Join(", ", types)}; SLA hours P1-P4: {string.Join("/", settings.SlaHours)} (was {string.Join("/", before.SlaHours)}); " +
                        $"reopen {settings.ReopenDays} d; auto-close {settings.AutoCloseDays} d; attachments {settings.MaxAttachmentMB} MB", false);
                    write.Changed(ChangeTopics.Setting, "helpdesk");
                }
                return 0;
            }, cancellationToken);

            _cache.Remove(SettingsKey);
            return await SettingsAsync(cancellationToken);
        }

        public async Task<List<FieldDefinition>> FieldsAsync(CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue(FieldsKey, out List<FieldDefinition> cached)) return cached;

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var fields = await db.FieldDefinitions.AsNoTracking().OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync(cancellationToken);
            _cache.Set(FieldsKey, fields, CacheFor);
            return fields;
        }

        public async Task<List<FieldDefinition>> SaveFieldAsync(FieldDefinitionDto input, Actor actor, CancellationToken cancellationToken)
        {
            var settings = await SettingsAsync(cancellationToken);
            var isNew = input.Id == 0;
            var errors = FieldRules.CheckDefinition(input, settings.TicketTypes, isNew);
            if (errors.Count > 0) throw new ValidationException(errors);

            await _writes.RunAsync(async write =>
            {
                FieldDefinition field;
                string before = null;
                if (isNew)
                {
                    if (await write.Db.FieldDefinitions.AnyAsync(f => f.Key == input.Key, cancellationToken))
                        throw new ValidationException(new[] { $"A field with the key \"{input.Key}\" already exists." });

                    var last = await write.Db.FieldDefinitions.MaxAsync(f => (int?)f.SortOrder, cancellationToken) ?? 0;
                    field = new FieldDefinition { Key = input.Key, SortOrder = last + 10 };
                    write.Db.FieldDefinitions.Add(field);
                }
                else
                {
                    field = await write.Db.FieldDefinitions.FirstOrDefaultAsync(f => f.Id == input.Id, cancellationToken)
                            ?? throw new NotFoundException("That field no longer exists.");
                    Versions.Expect(write.Db, field, input.Version);
                    before = Describe(field);
                }

                field.Label = input.Label.Trim();
                field.Kind = input.Kind;
                field.Options = FieldKinds.HasOptions(input.Kind) ? string.Join('\n', input.Options.Select(o => o.Trim()).Where(o => o.Length > 0)) : null;
                field.HelpText = string.IsNullOrWhiteSpace(input.HelpText) ? null : input.HelpText.Trim();
                field.Required = input.Required;
                field.Active = input.Active;
                field.Audience = input.Audience;
                field.AppliesTo = input.AppliesTo is { Count: > 0 } ? string.Join('\n', input.AppliesTo.Select(t => t.Trim())) : null;
                field.MaxLength = input.MaxLength;
                field.UpdatedBy = actor.Name;
                field.UpdatedAt = AuditChain.Now();

                write.Audit("field", field.Key, isNew ? "field-added" : "field-changed", actor,
                    isNew ? Describe(field) : $"{before} → {Describe(field)}", false);
                write.Changed(ChangeTopics.Field, field.Key);
                return 0;
            }, cancellationToken);

            _cache.Remove(FieldsKey);
            return await FieldsAsync(cancellationToken);
        }

        // Moves a field one place up (-1) or down (+1) on the form
        public async Task<List<FieldDefinition>> MoveFieldAsync(int id, int direction, Actor actor, CancellationToken cancellationToken)
        {
            await _writes.RunAsync(async write =>
            {
                var fields = await write.Db.FieldDefinitions.OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync(cancellationToken);
                var index = fields.FindIndex(f => f.Id == id);
                if (index < 0) throw new NotFoundException("That field no longer exists.");
                var other = index + Math.Sign(direction);
                if (other < 0 || other >= fields.Count) return 0;

                (fields[index], fields[other]) = (fields[other], fields[index]);
                for (var i = 0; i < fields.Count; i++) fields[i].SortOrder = (i + 1) * 10;

                write.Audit("field", fields[other].Key, "field-moved", actor, $"{fields[other].Label} moved {(direction < 0 ? "up" : "down")}", false);
                write.Changed(ChangeTopics.Field, fields[other].Key);
                return 0;
            }, cancellationToken);

            _cache.Remove(FieldsKey);
            return await FieldsAsync(cancellationToken);
        }

        public static FieldDefinitionDto ToDto(FieldDefinition f) => new()
        {
            Id = f.Id,
            Key = f.Key,
            Label = f.Label,
            Kind = f.Kind,
            Options = f.OptionList(),
            HelpText = f.HelpText,
            Required = f.Required,
            Active = f.Active,
            Audience = f.Audience,
            AppliesTo = f.AppliesToList(),
            MaxLength = f.MaxLength,
            SortOrder = f.SortOrder,
            Version = Versions.Of(f.RowVersion),
            UpdatedBy = f.UpdatedBy,
            UpdatedAt = DateTime.SpecifyKind(f.UpdatedAt, DateTimeKind.Utc)
        };

        private static string Describe(FieldDefinition f) =>
            $"{f.Label} ({f.Kind}, {(f.Audience == FieldAudience.It ? "IT" : "requester")}{(f.Required ? ", required" : "")}{(f.Active ? "" : ", hidden")})" +
            (string.IsNullOrEmpty(f.Options) ? "" : $" options: {string.Join(" / ", f.OptionList())}");

        private static int Number(Dictionary<string, string> rows, string key, int fallback) =>
            rows.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    // Problems the caller can fix, shown to them as they are
    public class ValidationException : Exception
    {
        public ValidationException(IEnumerable<string> errors) : base(string.Join(" ", errors)) => Errors = errors.ToList();
        public List<string> Errors { get; }
    }

    public class NotFoundException : Exception
    {
        public NotFoundException(string message) : base(message) { }
    }

    public class ConflictException : Exception
    {
        public ConflictException(string message) : base(message) { }
    }

    // Optimistic concurrency: every editable row carries a rowversion, handed out as Version and expected back
    public static class Versions
    {
        public static string Of(byte[] rowVersion) => rowVersion == null ? null : Convert.ToBase64String(rowVersion);

        public static void Expect<T>(DbContext db, T entity, string version) where T : class
        {
            if (string.IsNullOrEmpty(version))
                throw new ConflictException("The request did not say which version it was based on. Reload and try again.");

            byte[] expected;
            try
            {
                expected = Convert.FromBase64String(version);
            }
            catch (FormatException)
            {
                throw new ConflictException("The version sent with the request is not valid. Reload and try again.");
            }

            var property = db.Entry(entity).Property("RowVersion");
            if (property.CurrentValue is byte[] current && !current.AsSpan().SequenceEqual(expected))
                throw new ConflictException("Someone else changed this in the meantime. Reload to see their change, then try again.");

            // Also checked by the database itself when the row is written
            property.OriginalValue = expected;
        }
    }
}
