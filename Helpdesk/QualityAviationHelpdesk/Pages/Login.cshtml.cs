using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using QualityAviationHelpdesk.Security;

namespace QualityAviationHelpdesk.Pages
{
    [EnableRateLimiting("login")]
    public class LoginModel : PageModel
    {
        private readonly WebSignIn _signIn;
        private readonly ILogger<LoginModel> _logger;

        public LoginModel(WebSignIn signIn, ILogger<LoginModel> logger)
        {
            _signIn = signIn;
            _logger = logger;
        }

        [BindProperty] public string Login { get; set; }
        [BindProperty] public string Password { get; set; }
        [BindProperty(SupportsGet = true)] public string ReturnUrl { get; set; }
        public string Error { get; private set; }

        public IActionResult OnGet()
        {
            if (User.Identity?.IsAuthenticated == true && User.Identity.AuthenticationType == Access.WebScheme) return LocalRedirect(Target());
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            WebSignIn.Result result;
            try
            {
                result = await _signIn.SignInAsync(Login, Password, HttpContext);
            }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or TimeoutException or InvalidOperationException)
            {
                _logger.LogError(ex, "Sign-in could not reach a database");
                Error = "Signing in is not possible right now: the Q+ CMR or helpdesk database is not answering. Try again in a few minutes.";
                return Page();
            }

            if (result.Principal == null)
            {
                Error = result.Error;
                Password = null;
                return Page();
            }

            await HttpContext.SignInAsync(Access.WebScheme, result.Principal, new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
            return LocalRedirect(Target());
        }

        // Only addresses on this site, so the sign-in page cannot be used to send people elsewhere
        private string Target() => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) && !ReturnUrl.StartsWith("/login", StringComparison.OrdinalIgnoreCase) ? ReturnUrl : "/";
    }
}
