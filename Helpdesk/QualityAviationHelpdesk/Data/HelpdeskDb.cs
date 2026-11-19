using Microsoft.EntityFrameworkCore;

namespace QualityAviationHelpdesk.Data
{
    // The helpdesk's own database. Its tables are created by db\migrations\*.sql (see SchemaMigrator), not by EF,
    // so a DBA can read and run exactly what the application runs.
    public class HelpdeskDb : DbContext
    {
        public HelpdeskDb(DbContextOptions<HelpdeskDb> options) : base(options) { }

        public DbSet<Ticket> Tickets => Set<Ticket>();
        public DbSet<TicketFieldValue> FieldValues => Set<TicketFieldValue>();
        public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();
        public DbSet<Comment> Comments => Set<Comment>();
        public DbSet<Attachment> Attachments => Set<Attachment>();
        public DbSet<TodoItem> Todos => Set<TodoItem>();
        public DbSet<Setting> Settings => Set<Setting>();
        public DbSet<ItMember> ItMembers => Set<ItMember>();
        public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
        public DbSet<Change> Changes => Set<Change>();
        public DbSet<LoginThrottleEntry> LoginThrottle => Set<LoginThrottleEntry>();

        // Every time in the database is UTC; this makes values read back say so, so JSON carries the "Z"
        protected override void ConfigureConventions(ModelConfigurationBuilder builder)
        {
            builder.Properties<DateTime>().HaveConversion<UtcConverter>();
            builder.Properties<DateTime?>().HaveConversion<UtcConverter>();
        }

        private sealed class UtcConverter : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>
        {
            public UtcConverter() : base(v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
        }

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.HasDefaultSchema("hd");

            model.Entity<Ticket>(e =>
            {
                e.ToTable("Tickets");
                e.Property(t => t.RowVersion).IsRowVersion();
                e.HasMany(t => t.FieldValues).WithOne().HasForeignKey(v => v.TicketId);
            });
            model.Entity<TicketFieldValue>(e =>
            {
                e.ToTable("TicketFieldValues");
                e.HasKey(v => new { v.TicketId, v.FieldKey });
            });
            model.Entity<FieldDefinition>(e =>
            {
                e.ToTable("FieldDefinitions");
                e.Property(f => f.RowVersion).IsRowVersion();
            });
            model.Entity<Comment>().ToTable("Comments");
            model.Entity<Attachment>(e =>
            {
                e.ToTable("Attachments");
                e.Property(a => a.Sha256).IsFixedLength();
            });
            model.Entity<TodoItem>(e =>
            {
                e.ToTable("TodoItems");
                e.Property(t => t.RowVersion).IsRowVersion();
            });
            model.Entity<Setting>(e =>
            {
                e.ToTable("Settings");
                e.HasKey(s => s.Key);
            });
            model.Entity<ItMember>(e =>
            {
                e.ToTable("ItMembers");
                e.HasKey(m => m.Username);
            });
            model.Entity<AuditEvent>(e =>
            {
                e.ToTable("AuditEvents");
                e.Property(a => a.PrevHash).IsFixedLength();
                e.Property(a => a.Hash).IsFixedLength();
            });
            model.Entity<Change>(e =>
            {
                e.ToTable("Changes");
                e.HasKey(c => c.Seq);
            });
            model.Entity<LoginThrottleEntry>(e =>
            {
                e.ToTable("LoginThrottle");
                e.HasKey(l => l.Key);
            });
        }
    }

    public class Ticket
    {
        public int Id { get; set; }
        public string Type { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public byte Priority { get; set; }
        public string Status { get; set; }
        public string RequesterId { get; set; }
        public string RequesterUserName { get; set; }
        public string RequesterName { get; set; }
        public string RequesterEmail { get; set; }
        public string RequesterDepartment { get; set; }
        public string Assignee { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? AcceptedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public string ResolvedBy { get; set; }
        public DateTime? ClosedAt { get; set; }
        public DateTime? DueAt { get; set; }
        public string Resolution { get; set; }
        public string StatusReason { get; set; }
        public int MinutesSpent { get; set; }
        public int ReopenCount { get; set; }
        public byte[] RowVersion { get; set; }

        public List<TicketFieldValue> FieldValues { get; set; } = new();
    }

    public class TicketFieldValue
    {
        public int TicketId { get; set; }
        public string FieldKey { get; set; }
        public string Value { get; set; }
    }

    public class FieldDefinition
    {
        public int Id { get; set; }
        public string Key { get; set; }
        public string Label { get; set; }
        public string Kind { get; set; }
        public string Options { get; set; }
        public string HelpText { get; set; }
        public bool Required { get; set; }
        public bool Active { get; set; }
        public string Audience { get; set; }
        public string AppliesTo { get; set; }
        public int MaxLength { get; set; }
        public int SortOrder { get; set; }
        public string UpdatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
        public byte[] RowVersion { get; set; }

        public List<string> OptionList() => Lines(Options);
        public List<string> AppliesToList() => Lines(AppliesTo);

        public bool AppliesToType(string type)
        {
            var types = AppliesToList();
            return types.Count == 0 || types.Contains(type, StringComparer.OrdinalIgnoreCase);
        }

        internal static List<string> Lines(string text) =>
            (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public class Comment
    {
        public int Id { get; set; }
        public int TicketId { get; set; }
        public string AuthorKind { get; set; }
        public string AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string Body { get; set; }
        public bool Internal { get; set; }
        public int MinutesSpent { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class Attachment
    {
        public Guid Id { get; set; }
        public int TicketId { get; set; }
        public int? CommentId { get; set; }
        public string FileName { get; set; }
        public string ContentType { get; set; }
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; }
        public string StoredPath { get; set; }
        public bool Internal { get; set; }
        public string UploadedByKind { get; set; }
        public string UploadedById { get; set; }
        public string UploadedByName { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class TodoItem
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string Notes { get; set; }
        public string Owner { get; set; }
        public string Status { get; set; }
        public byte Priority { get; set; }
        public DateOnly? DueOn { get; set; }
        public int? TicketId { get; set; }
        public bool Archived { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public string UpdatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string CompletedBy { get; set; }
        public DateTime? CompletedAt { get; set; }
        public byte[] RowVersion { get; set; }
    }

    public class Setting
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public string UpdatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class ItMember
    {
        public string Username { get; set; }
        public bool IsLead { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public class AuditEvent
    {
        public long Id { get; set; }
        public string EntityType { get; set; }
        public string EntityId { get; set; }
        public string Action { get; set; }
        public string ActorKind { get; set; }
        public string ActorId { get; set; }
        public string ActorName { get; set; }
        public string Detail { get; set; }
        public bool VisibleToRequester { get; set; }
        public string Ip { get; set; }
        public DateTime At { get; set; }
        public string PrevHash { get; set; }
        public string Hash { get; set; }
    }

    public class Change
    {
        public long Seq { get; set; }
        public string Topic { get; set; }
        public string EntityId { get; set; }
        public DateTime At { get; set; }
    }

    public class LoginThrottleEntry
    {
        public string Key { get; set; }
        public int Failures { get; set; }
        public DateTime WindowStart { get; set; }
        public DateTime? LockedUntil { get; set; }
    }
}
