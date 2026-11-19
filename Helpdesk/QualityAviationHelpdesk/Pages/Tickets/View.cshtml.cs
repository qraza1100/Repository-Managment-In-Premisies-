using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;
using QualityAviationHelpdesk.Security;
using QualityAviationHelpdesk.Services;

namespace QualityAviationHelpdesk.Pages.Tickets
{
    [EnableRateLimiting("write")]
    public class ViewTicketModel : PageModel
    {
        private readonly TicketService _tickets;
        private readonly Config _config;

        public ViewTicketModel(TicketService tickets, Config config)
        {
            _tickets = tickets;
            _config = config;
        }

        [BindProperty(SupportsGet = true)] public int Id { get; set; }
        public TicketDetail Ticket { get; private set; }
        public List<FieldDefinition> Definitions { get; private set; } = new();
        public int MaxMb { get; private set; }
        public List<string> Errors { get; } = new();

        public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => await ShowAsync(cancellationToken);

        public async Task<IActionResult> OnPostActAsync(string act, string version, string note, CancellationToken cancellationToken)
        {
            try
            {
                await _tickets.RequesterActAsync(User, Id, act, note, version, Actor.From(HttpContext), cancellationToken);
                TempData["Message"] = act switch
                {
                    TicketActions.Confirm => "Thank you. The ticket is closed.",
                    TicketActions.Reopen => "The ticket is open again and IT has been told.",
                    TicketActions.Cancel => "The ticket was withdrawn.",
                    _ => null
                };
                return Redirect($"/tickets/{Id}");
            }
            catch (Exception ex) when (ex is ValidationException or WorkflowException or ConflictException or DbUpdateConcurrencyException)
            {
                Errors.Add(ex is DbUpdateConcurrencyException ? "The ticket changed while you were looking at it. Here is the latest; please try again." : ex.Message);
                return await ShowAsync(cancellationToken);
            }
        }

        public async Task<IActionResult> OnPostCommentAsync(string body, List<IFormFile> files, CancellationToken cancellationToken)
        {
            try
            {
                await _tickets.RequesterCommentAsync(User, Id, body, files, Actor.From(HttpContext), cancellationToken);
                return Redirect($"/tickets/{Id}");
            }
            catch (ValidationException ex)
            {
                Errors.AddRange(ex.Errors);
                return await ShowAsync(cancellationToken);
            }
        }

        private async Task<IActionResult> ShowAsync(CancellationToken cancellationToken)
        {
            // Someone else's ticket answers exactly like one that does not exist
            Ticket = await _tickets.GetForRequesterAsync(User.Id(), Id, cancellationToken);
            if (Ticket == null) return NotFound();

            Definitions = (await _config.FieldsAsync(cancellationToken)).Where(f => f.Audience == FieldAudience.Requester).OrderBy(f => f.SortOrder).ToList();
            MaxMb = (await _config.SettingsAsync(cancellationToken)).MaxAttachmentMB;
            return Page();
        }
    }
}
