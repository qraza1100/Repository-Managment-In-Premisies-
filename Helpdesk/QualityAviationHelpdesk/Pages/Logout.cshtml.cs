using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QualityAviationHelpdesk.Security;

namespace QualityAviationHelpdesk.Pages
{
    // Signing out is a POST with an anti-forgery token, so another site cannot sign people out
    [AllowAnonymous]
    public class LogoutModel : PageModel
    {
        public IActionResult OnGet() => Redirect("/");

        public async Task<IActionResult> OnPostAsync()
        {
            await HttpContext.SignOutAsync(Access.WebScheme);
            return Redirect("/login");
        }
    }
}
