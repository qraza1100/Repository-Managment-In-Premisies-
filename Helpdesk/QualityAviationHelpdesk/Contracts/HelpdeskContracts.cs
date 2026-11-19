#nullable disable
// The data the IT client and the portal exchange. The client compiles this very file (by link), so the two
// sides can never disagree about a name or a field.

using System;
using System.Collections.Generic;

namespace QualityAviationHelpdesk.Contracts
{
    public static class TicketStatus
    {
        public const string New = "new";
        public const string Accepted = "accepted";
        public const string InProgress = "in-progress";
        public const string OnHold = "on-hold";
        public const string Resolved = "resolved";
        public const string Closed = "closed";
        public const string Rejected = "rejected";
        public const string Cancelled = "cancelled";

        public static readonly string[] All = { New, Accepted, InProgress, OnHold, Resolved, Closed, Rejected, Cancelled };
        // Still waiting for IT
        public static readonly string[] Open = { New, Accepted, InProgress, OnHold };
        // Nothing more happens to these, except a reopen
        public static readonly string[] Final = { Closed, Rejected, Cancelled };

        public static bool IsOpen(string status) => Array.IndexOf(Open, status) >= 0;

        public static string Label(string status) => status switch
        {
            New => "New",
            Accepted => "Accepted",
            InProgress => "In progress",
            OnHold => "On hold",
            Resolved => "Resolved",
            Closed => "Closed",
            Rejected => "Rejected",
            Cancelled => "Cancelled",
            _ => status
        };
    }

    public static class TicketActions
    {
        // IT
        public const string Accept = "accept";
        public const string Reject = "reject";
        public const string Start = "start";
        public const string Hold = "hold";
        public const string Resolve = "resolve";
        public const string Close = "close";
        public const string Reopen = "reopen";
        public const string Assign = "assign";
        public const string Take = "take";
        public const string Priority = "priority";
        // Requester
        public const string Cancel = "cancel";
        public const string Confirm = "confirm";

        public static string Label(string action) => action switch
        {
            Accept => "Accept",
            Reject => "Reject",
            Start => "Start work",
            Hold => "Put on hold",
            Resolve => "Resolve",
            Close => "Close",
            Reopen => "Reopen",
            Assign => "Assign",
            Take => "Take it",
            Priority => "Change priority",
            Cancel => "Withdraw",
            Confirm => "Confirm it is solved",
            _ => action
        };
    }

    public static class Priorities
    {
        public static readonly int[] All = { 1, 2, 3, 4 };

        public static string Label(int priority) => priority switch
        {
            1 => "P1 Critical",
            2 => "P2 High",
            3 => "P3 Medium",
            4 => "P4 Low",
            _ => $"P{priority}"
        };

        public static string Describe(int priority) => priority switch
        {
            1 => "Work has stopped for a person or a team",
            2 => "Work is seriously slowed down, no workaround",
            3 => "There is a workaround, or it is a planned improvement",
            4 => "Small issue or a nice-to-have",
            _ => ""
        };
    }

    public static class FieldKinds
    {
        public const string Text = "text";
        public const string TextArea = "textarea";
        public const string Number = "number";
        public const string Date = "date";
        public const string Select = "select";
        public const string MultiSelect = "multiselect";
        public const string Checkbox = "checkbox";

        public static readonly string[] All = { Text, TextArea, Number, Date, Select, MultiSelect, Checkbox };
        public static bool HasOptions(string kind) => kind is Select or MultiSelect;
    }

    public static class FieldAudience
    {
        // Shown on the web form; the requester fills it in
        public const string Requester = "requester";
        // Only seen and filled in by IT, in the client
        public const string It = "it";
    }

    public static class TodoStatus
    {
        public const string Todo = "todo";
        public const string Doing = "doing";
        public const string Blocked = "blocked";
        public const string Done = "done";

        public static readonly string[] All = { Todo, Doing, Blocked, Done };

        public static string Label(string status) => status switch
        {
            Todo => "To do",
            Doing => "Doing",
            Blocked => "Blocked",
            Done => "Done",
            _ => status
        };
    }

    public static class ChangeTopics
    {
        public const string Ticket = "ticket";
        public const string Todo = "todo";
        public const string Field = "field";
        public const string Setting = "setting";
    }

    // ------------------------------------------------------------------ IT client

    public class ItMe
    {
        public string Username { get; set; }
        public bool IsLead { get; set; }
        public List<string> Members { get; set; } = new();
        public HelpdeskSettings Settings { get; set; }
        public DateTime ServerTime { get; set; }
    }

    public class TicketListItem
    {
        public int Id { get; set; }
        public string Number { get; set; }
        public string Type { get; set; }
        public string Summary { get; set; }
        public int Priority { get; set; }
        public string Status { get; set; }
        public string RequesterName { get; set; }
        public string Department { get; set; }
        public string Assignee { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DueAt { get; set; }
        public bool Overdue { get; set; }
    }

    public class TicketPage
    {
        public List<TicketListItem> Items { get; set; } = new();
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        // Tickets per status over the whole queue, for the filter labels
        public Dictionary<string, int> Counts { get; set; } = new();
    }

    public class TicketDetail
    {
        public int Id { get; set; }
        public string Number { get; set; }
        public string Version { get; set; }
        public string Type { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public int Priority { get; set; }
        public string Status { get; set; }
        public string StatusReason { get; set; }
        public string Resolution { get; set; }
        public string RequesterName { get; set; }
        public string RequesterUserName { get; set; }
        public string RequesterEmail { get; set; }
        public string Department { get; set; }
        public string Assignee { get; set; }
        public string ResolvedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? AcceptedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public DateTime? DueAt { get; set; }
        public bool Overdue { get; set; }
        public int MinutesSpent { get; set; }
        public int ReopenCount { get; set; }
        public Dictionary<string, string> Fields { get; set; } = new();
        public List<CommentDto> Comments { get; set; } = new();
        public List<AttachmentDto> Attachments { get; set; } = new();
        public List<TimelineEntry> Timeline { get; set; } = new();
        public List<string> AllowedActions { get; set; } = new();
    }

    public class CommentDto
    {
        public int Id { get; set; }
        public string AuthorKind { get; set; }
        public string AuthorName { get; set; }
        public string Body { get; set; }
        public bool Internal { get; set; }
        public int MinutesSpent { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class AttachmentDto
    {
        public Guid Id { get; set; }
        public int? CommentId { get; set; }
        public string FileName { get; set; }
        public string ContentType { get; set; }
        public long SizeBytes { get; set; }
        public bool Internal { get; set; }
        public string UploadedByName { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class TimelineEntry
    {
        public long Id { get; set; }
        public string EntityType { get; set; }
        public string EntityId { get; set; }
        public DateTime At { get; set; }
        public string ActorKind { get; set; }
        public string ActorName { get; set; }
        public string Action { get; set; }
        public string Detail { get; set; }
    }

    public class TicketActionRequest
    {
        // The Version of the ticket the user was looking at; a ticket someone else changed in between is refused
        public string Version { get; set; }
        public string Note { get; set; }
        public string Assignee { get; set; }
        public int? Priority { get; set; }
        public int MinutesSpent { get; set; }
    }

    public class TicketEditRequest
    {
        public string Version { get; set; }
        public string Type { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public int Priority { get; set; }
        public Dictionary<string, string> Fields { get; set; } = new();
    }

    public class FieldDefinitionDto
    {
        public int Id { get; set; }
        public string Key { get; set; }
        public string Label { get; set; }
        public string Kind { get; set; }
        public List<string> Options { get; set; } = new();
        public string HelpText { get; set; }
        public bool Required { get; set; }
        public bool Active { get; set; } = true;
        public string Audience { get; set; } = FieldAudience.Requester;
        // Ticket types the field is shown for; empty = every type
        public List<string> AppliesTo { get; set; } = new();
        public int MaxLength { get; set; } = 500;
        public int SortOrder { get; set; }
        public string Version { get; set; }
        public string UpdatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class HelpdeskSettings
    {
        public List<string> TicketTypes { get; set; } = new();
        // Hours to resolve, by priority: index 0 is P1
        public int[] SlaHours { get; set; } = { 4, 24, 72, 168 };
        public int ReopenDays { get; set; } = 7;
        public int AutoCloseDays { get; set; } = 5;
        public int MaxAttachmentMB { get; set; } = 10;
    }

    public class TodoDto
    {
        public int Id { get; set; }
        public string Version { get; set; }
        public string Title { get; set; }
        public string Notes { get; set; }
        public string Owner { get; set; }
        public string Status { get; set; }
        public int Priority { get; set; }
        public DateOnly? DueOn { get; set; }
        public int? TicketId { get; set; }
        public string TicketNumber { get; set; }
        public bool Archived { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public string UpdatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string CompletedBy { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class TodoSaveRequest
    {
        public string Version { get; set; }
        public string Title { get; set; }
        public string Notes { get; set; }
        public string Owner { get; set; }
        public string Status { get; set; } = TodoStatus.Todo;
        public int Priority { get; set; } = 3;
        public DateOnly? DueOn { get; set; }
        public int? TicketId { get; set; }
    }

    public class ChangeDto
    {
        public long Seq { get; set; }
        public string Topic { get; set; }
        public string EntityId { get; set; }
    }

    public class ChangeBatch
    {
        public long Last { get; set; }
        // More changed than the batch can hold: reload everything instead of applying the list
        public bool Reset { get; set; }
        public List<ChangeDto> Changes { get; set; } = new();
    }

    public class AuditVerification
    {
        public bool Intact { get; set; }
        public long Events { get; set; }
        public long? BrokenAt { get; set; }
        public string Message { get; set; }
        public DateTime CheckedAt { get; set; }
    }

    public static class TicketNumbers
    {
        public static string Format(int id) => $"QA-{id}";

        public static bool TryParse(string text, out int id)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("QA-", StringComparison.OrdinalIgnoreCase)) text = text[3..];
            return int.TryParse(text, out id) && id > 0;
        }
    }
}
