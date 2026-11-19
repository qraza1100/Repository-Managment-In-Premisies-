using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;
using QualityAviationHelpdesk.Security;
using QualityAviationHelpdesk.Services;

namespace QualityAviationHelpdesk.Pages.Tickets
{
    [EnableRateLimiting("write")]
    public class NewTicketModel : PageModel
    {
        private readonly TicketService _tickets;
        private readonly Config _config;
        private readonly ILegacyDirectory _directory;

        public NewTicketModel(TicketService tickets, Config config, ILegacyDirectory directory)
        {
            _tickets = tickets;
            _config = config;
            _directory = directory;
        }

        [BindProperty] public string Type { get; set; }
        [BindProperty] public string Summary { get; set; }
        [BindProperty] public string Description { get; set; }
        [BindProperty] public int Priority { get; set; } = 3;

        public HelpdeskSettings Settings { get; private set; }
        public List<FieldDefinition> Fields { get; private set; } = new();
        public Dictionary<string, string> Values { get; private set; } = new();
        public LegacyProfile Profile { get; private set; }
        public List<string> Errors { get; } = new();

        public string Target(int priority)
        {
            var hours = Settings.SlaHours[priority - 1];
            return hours < 48 ? $"{hours} h" : $"{hours / 24} days";
        }

        public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

        public async Task<IActionResult> OnPostAsync(List<IFormFile> files, CancellationToken cancellationToken)
        {
            await LoadAsync(cancellationToken);

            // Run-time fields arrive as f.<key>; a multi-choice field arrives once per ticked option
            foreach (var field in Fields)
            {
                var posted = Request.Form["f." + field.Key];
                if (posted.Count > 0) Values[field.Key] = string.Join("|", posted.Where(v => !string.IsNullOrWhiteSpace(v)));
            }

            try
            {
                var id = await _tickets.CreateAsync(User, new TicketService.NewTicket
                {
                    Type = Type,
                    Summary = Summary,
                    Description = Description,
                    Priority = Priority,
                    Fields = Values
                }, files, Actor.From(HttpContext), cancellationToken);

                TempData["Message"] = $"Ticket {TicketNumbers.Format(id)} was sent to IT. You can follow it here.";
                return Redirect($"/tickets/{id}");
            }
            catch (ValidationException ex)
            {
                Errors.AddRange(ex.Errors);
                return Page();
            }
        }

        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            Settings = await _config.SettingsAsync(cancellationToken);
            Fields = (await _config.FieldsAsync(cancellationToken)).Where(f => f.Active && f.Audience == FieldAudience.Requester).OrderBy(f => f.SortOrder).ToList();
            Profile = await _directory.GetProfileAsync(User.Id(), fresh: false, cancellationToken);
            if (Priority is < 1 or > 4) Priority = 3;
        }
    }
}
