using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Security;
using QualityAviationHelpdesk.Services;

namespace QualityAviationHelpdesk.Pages
{
    public class IndexModel : PageModel
    {
        public const int Size = 20;
        private readonly TicketService _tickets;

        public IndexModel(TicketService tickets) => _tickets = tickets;

        [BindProperty(SupportsGet = true)] public string Filter { get; set; }
        [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;
        public int PageSize => Size;
        public List<TicketListItem> Items { get; private set; } = new();
        public int Total { get; private set; }
        public Dictionary<string, int> Counts { get; private set; } = new();

        public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
        {
            // Management without a requester role goes straight to the dashboard
            if (!User.IsInRole(Access.Requester)) return Redirect("/dashboard");

            Filter = Filter is "resolved" or "closed" or "all" ? Filter : "open";
            PageNumber = Math.Max(PageNumber, 1);
            (Items, Total, Counts) = await _tickets.MineAsync(User.Id(), Filter, PageNumber, Size, cancellationToken);
            return Page();
        }
    }
}
