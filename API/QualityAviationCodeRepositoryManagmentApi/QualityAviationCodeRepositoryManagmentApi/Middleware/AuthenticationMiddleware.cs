using QualityAviationCodeRepositoryManagmentApi.API.Services;

namespace QualityAviationCodeRepositoryManagmentApi.API.Middleware
{
    public class AuthenticationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IServiceProvider _serviceProvider;

        public AuthenticationMiddleware(RequestDelegate next, IServiceProvider serviceProvider)
        {
            _next = next;
            _serviceProvider = serviceProvider;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value.ToLower();

            if (path.Contains("/api/auth/login"))
            {
                await _next(context);
                return;
            }

            using (var scope = _serviceProvider.CreateScope())
            {
                var authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
                var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();

                if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { message = "Unauthorized - Missing or invalid token" });
                    return;
                }

                var token = authHeader.Substring("Bearer ".Length).Trim();
                var user = authService.GetUserByGatePass(token);

                if (user == null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { message = "Unauthorized - Invalid token" });
                    return;
                }

                context.Items["User"] = user;
                context.Items["Username"] = user.Username;
            }

            await _next(context);
        }
    }
}
