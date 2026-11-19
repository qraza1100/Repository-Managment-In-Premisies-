using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Services;

namespace QualityAviationHelpdesk.Tests
{
    public class WorkflowTests
    {
        [Theory]
        [InlineData(TicketStatus.New, TicketActions.Accept, TicketStatus.Accepted)]
        [InlineData(TicketStatus.New, TicketActions.Start, TicketStatus.InProgress)]
        [InlineData(TicketStatus.Accepted, TicketActions.Start, TicketStatus.InProgress)]
        [InlineData(TicketStatus.OnHold, TicketActions.Start, TicketStatus.InProgress)]
        [InlineData(TicketStatus.InProgress, TicketActions.Resolve, TicketStatus.Resolved)]
        [InlineData(TicketStatus.New, TicketActions.Resolve, TicketStatus.Resolved)]
        [InlineData(TicketStatus.Resolved, TicketActions.Close, TicketStatus.Closed)]
        [InlineData(TicketStatus.Closed, TicketActions.Reopen, TicketStatus.InProgress)]
        [InlineData(TicketStatus.InProgress, TicketActions.Assign, TicketStatus.InProgress)]
        [InlineData(TicketStatus.New, TicketActions.Priority, TicketStatus.New)]
        public void It_moves_tickets_along_the_allowed_paths(string from, string action, string to)
        {
            Assert.Equal(to, Workflow.Apply(from, action, it: true, note: "done"));
        }

        [Theory]
        [InlineData(TicketStatus.New, TicketActions.Close)]
        [InlineData(TicketStatus.Closed, TicketActions.Start)]
        [InlineData(TicketStatus.Cancelled, TicketActions.Reopen)]
        [InlineData(TicketStatus.Resolved, TicketActions.Assign)]
        public void It_refuses_steps_that_do_not_fit_the_status(string from, string action)
        {
            var error = Assert.Throws<WorkflowException>(() => Workflow.Apply(from, action, it: true, note: "x"));
            Assert.Contains("cannot", error.Message);
        }

        [Theory]
        [InlineData(TicketActions.Reject)]
        [InlineData(TicketActions.Resolve)]
        [InlineData(TicketActions.Hold)]
        public void Reasons_the_requester_reads_are_required(string action)
        {
            var from = action == TicketActions.Reject ? TicketStatus.New : TicketStatus.InProgress;
            Assert.Throws<WorkflowException>(() => Workflow.Apply(from, action, it: true, note: "  "));
            Assert.True(Workflow.NoteRequired(action, it: true));
        }

        [Fact]
        public void A_requester_can_only_withdraw_confirm_or_reopen()
        {
            Assert.Equal(TicketStatus.Cancelled, Workflow.Apply(TicketStatus.New, TicketActions.Cancel, it: false, note: null));
            Assert.Equal(TicketStatus.Closed, Workflow.Apply(TicketStatus.Resolved, TicketActions.Confirm, it: false, note: null));
            Assert.Equal(TicketStatus.InProgress, Workflow.Apply(TicketStatus.Resolved, TicketActions.Reopen, it: false, note: "still broken"));

            // IT's own steps are not available to the requester
            Assert.Throws<WorkflowException>(() => Workflow.Apply(TicketStatus.New, TicketActions.Accept, it: false, note: null));
            Assert.Throws<WorkflowException>(() => Workflow.Apply(TicketStatus.InProgress, TicketActions.Resolve, it: false, note: "x"));
            // Nor can a requester reopen a ticket IT closed for good
            Assert.Throws<WorkflowException>(() => Workflow.Apply(TicketStatus.Closed, TicketActions.Reopen, it: false, note: "x"));
        }

        [Fact]
        public void The_offered_actions_match_what_is_accepted()
        {
            foreach (var status in TicketStatus.All)
            foreach (var it in new[] { true, false })
            foreach (var action in Workflow.Allowed(status, it))
                Workflow.Apply(status, action, it, "note");
        }

        [Theory]
        [InlineData("QA-1001", 1001)]
        [InlineData("qa-77", 77)]
        [InlineData(" 1234 ", 1234)]
        public void Ticket_numbers_parse(string text, int id)
        {
            Assert.True(TicketNumbers.TryParse(text, out var parsed));
            Assert.Equal(id, parsed);
            Assert.False(TicketNumbers.TryParse("QA-x", out _));
        }
    }
}
