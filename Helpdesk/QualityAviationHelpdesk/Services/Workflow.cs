using QualityAviationHelpdesk.Contracts;

namespace QualityAviationHelpdesk.Services
{
    // The life of a ticket. Pure rules, no database: the same table decides what the client offers, what the web
    // page offers and what the server accepts.
    //
    //   new ──accept──▶ accepted ──start──▶ in-progress ──resolve──▶ resolved ──close/confirm──▶ closed
    //    │                 │   ▲                │  ▲                     │
    //    │                 │   └─────start──── on-hold ◀──hold─┘         └──reopen──▶ in-progress
    //    └──reject / cancel┴──▶ rejected / cancelled
    public static class Workflow
    {
        private sealed record Rule(string Action, string[] From, string To, bool It, bool NoteRequired);

        private static readonly Rule[] Rules =
        {
            new(TicketActions.Accept, new[] { TicketStatus.New }, TicketStatus.Accepted, true, false),
            new(TicketActions.Reject, new[] { TicketStatus.New, TicketStatus.Accepted, TicketStatus.OnHold }, TicketStatus.Rejected, true, true),
            new(TicketActions.Start, new[] { TicketStatus.New, TicketStatus.Accepted, TicketStatus.OnHold }, TicketStatus.InProgress, true, false),
            new(TicketActions.Hold, new[] { TicketStatus.Accepted, TicketStatus.InProgress }, TicketStatus.OnHold, true, true),
            // Straight from New as well: a quick fix needs no separate accept and start
            new(TicketActions.Resolve, new[] { TicketStatus.New, TicketStatus.Accepted, TicketStatus.InProgress, TicketStatus.OnHold }, TicketStatus.Resolved, true, true),
            new(TicketActions.Close, new[] { TicketStatus.Resolved }, TicketStatus.Closed, true, false),
            new(TicketActions.Reopen, new[] { TicketStatus.Resolved, TicketStatus.Closed, TicketStatus.Rejected }, TicketStatus.InProgress, true, true),
            // These change who or how urgent, not where the ticket is
            new(TicketActions.Assign, TicketStatus.Open, null, true, false),
            new(TicketActions.Take, TicketStatus.Open, null, true, false),
            new(TicketActions.Priority, TicketStatus.Open, null, true, false),

            new(TicketActions.Cancel, new[] { TicketStatus.New, TicketStatus.Accepted }, TicketStatus.Cancelled, false, false),
            new(TicketActions.Confirm, new[] { TicketStatus.Resolved }, TicketStatus.Closed, false, false),
            new(TicketActions.Reopen, new[] { TicketStatus.Resolved }, TicketStatus.InProgress, false, true)
        };

        public static IReadOnlyList<string> Allowed(string status, bool it) =>
            Rules.Where(r => r.It == it && r.From.Contains(status)).Select(r => r.Action).Distinct().ToList();

        // The status after the action (unchanged for assign/take/priority). Throws a WorkflowException with a
        // sentence a person can act on when the action does not fit.
        public static string Apply(string status, string action, bool it, string note)
        {
            var rule = Rules.FirstOrDefault(r => r.It == it && r.Action == action && r.From.Contains(status));
            if (rule == null)
            {
                var known = Rules.Any(r => r.It == it && r.Action == action);
                throw new WorkflowException(known
                    ? $"A ticket that is {TicketStatus.Label(status).ToLowerInvariant()} cannot be given \"{TicketActions.Label(action)}\"."
                    : $"\"{action}\" is not something that can be done to a ticket.");
            }

            if (rule.NoteRequired && string.IsNullOrWhiteSpace(note))
                throw new WorkflowException(action switch
                {
                    TicketActions.Resolve => "Say what was done to resolve it: the requester sees this.",
                    TicketActions.Reject => "Give the reason for rejecting it: the requester sees this.",
                    TicketActions.Hold => "Say what the ticket is waiting for.",
                    TicketActions.Reopen => "Say why it needs to be reopened.",
                    _ => "A note is required."
                });

            return rule.To ?? status;
        }

        public static bool NoteRequired(string action, bool it) =>
            Rules.Any(r => r.It == it && r.Action == action && r.NoteRequired);

        // The steps shown to the requester, and how far the ticket has come
        public static readonly string[] Steps = { "Submitted", "Reviewed", "In progress", "Resolved", "Closed" };

        public static int StepOf(string status) => status switch
        {
            TicketStatus.New => 0,
            TicketStatus.Accepted => 1,
            TicketStatus.InProgress or TicketStatus.OnHold => 2,
            TicketStatus.Resolved => 3,
            _ => 4
        };
    }

    public class WorkflowException : Exception
    {
        public WorkflowException(string message) : base(message) { }
    }
}
