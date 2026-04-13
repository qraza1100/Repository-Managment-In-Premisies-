using Microsoft.AspNetCore.Mvc;
using QualityAviationCodeRepositoryManagmentApi.API.Models;
using QualityAviationCodeRepositoryManagmentApi.API.Services;

namespace QualityAviationCodeRepositoryManagmentApi.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthenticationService _authService;

        public AuthController(IAuthenticationService authService)
        {
            _authService = authService;
        }

        [HttpPost("login")]
        public IActionResult Login([FromBody] AuthRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Username) || string.IsNullOrWhiteSpace(request?.Password))
            {
                return BadRequest(new ApiResponse
                {
                    Success = false,
                    Message = "Username and password are required",
                    StatusCode = 400
                });
            }

            var result = _authService.Authenticate(request);

            if (!result.Authenticated)
            {
                return Unauthorized(result);
            }

            return Ok(result);
        }

        [HttpGet("validate")]
        public IActionResult ValidateToken()
        {
            var user = HttpContext.Items["User"] as User;

            if (user == null)
            {
                return Unauthorized(new ApiResponse
                {
                    Success = false,
                    Message = "Invalid token",
                    StatusCode = 401
                });
            }

            return Ok(new ApiResponse
            {
                Success = true,
                Message = "Token is valid",
                StatusCode = 200
            });
        }
    }
}
