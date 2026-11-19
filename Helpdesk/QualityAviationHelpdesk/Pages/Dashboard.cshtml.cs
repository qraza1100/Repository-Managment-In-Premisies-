using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QualityAviationHelpdesk.Services;

namespace QualityAviationHelpdesk.Pages
{
    // Read-only by construction: the page has no forms, and its data comes from a GET that changes nothing
    public class DashboardModel : PageModel
    {
        [BindProperty(SupportsGet = true)] public int Days { get; set; } = 30;

        public void OnGet() => Days = DashboardService.NormalizeRange(Days);
    }
}
