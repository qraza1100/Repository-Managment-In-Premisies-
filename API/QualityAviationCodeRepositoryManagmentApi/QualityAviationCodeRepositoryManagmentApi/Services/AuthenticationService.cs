using OfficeOpenXml;
using QualityAviationCodeRepositoryManagmentApi.API.Models;
using System.Text.RegularExpressions;

namespace QualityAviationCodeRepositoryManagmentApi.API.Services
{
    public interface IAuthenticationService
    {
        AuthResponse Authenticate(AuthRequest request);
        User GetUserByGatePass(string gatePass);
        bool ValidateGatePass(string gatePass);
    }

    public class AuthenticationService : IAuthenticationService
    {
        private readonly IConfiguration _configuration;
        private readonly EncryptionService _encryptionService;
        private readonly string _usersExcelPath;

        public AuthenticationService(IConfiguration configuration)
        {
            _configuration = configuration;
            _encryptionService = new EncryptionService(configuration);
            _usersExcelPath = configuration["RepositorySettings:UsersExcelPath"];
            ExcelPackage.License.SetNonCommercialPersonal("ITQA");
        }

        public AuthResponse Authenticate(AuthRequest request)
        {
            var user = GetUserFromExcel(request.Username);
            
            if (user == null)
            {
                return new AuthResponse
                {
                    Authenticated = false,
                    Message = "User not found"
                };
            }

            if (!VerifyPassword(request.Password, user.Password))
            {
                return new AuthResponse
                {
                    Authenticated = false,
                    Message = "Invalid password"
                };
            }

            var gatePass = _encryptionService.Encrypt($"{user.Username}|{DateTime.UtcNow.Ticks}");

            return new AuthResponse
            {
                Authenticated = true,
                GatePass = gatePass,
                Message = "Authentication successful",
                User = user
            };
        }

        public User GetUserByGatePass(string gatePass)
        {
            try
            {
                var decrypted = _encryptionService.Decrypt(gatePass);
                if (decrypted == null) return null;

                var parts = decrypted.Split('|');
                if (parts.Length < 1) return null;

                return GetUserFromExcel(parts[0]);
            }
            catch
            {
                return null;
            }
        }

        public bool ValidateGatePass(string gatePass)
        {
            var user = GetUserByGatePass(gatePass);
            return user != null;
        }

        private User GetUserFromExcel(string username)
        {
            try
            {
                if (!File.Exists(_usersExcelPath))
                    return null;

                using (var package = new ExcelPackage(new FileInfo(_usersExcelPath)))
                {
                    var worksheet = package.Workbook.Worksheets.FirstOrDefault();
                    if (worksheet == null) return null;

                    for (int row = 2; row <= worksheet.Dimension?.Rows; row++)
                    {
                        var cellUsername = worksheet.Cells[row, 1].Value?.ToString();
                        
                        if (cellUsername?.Equals(username, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            return new User
                            {
                                Username = cellUsername,
                                Password = worksheet.Cells[row, 2].Value?.ToString(),
                                Email = worksheet.Cells[row, 3].Value?.ToString(),
                                GatePass = worksheet.Cells[row, 4].Value?.ToString()
                            };
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private bool VerifyPassword(string inputPassword, string storedPassword)
        {
            return inputPassword == storedPassword;
        }
    }
}
