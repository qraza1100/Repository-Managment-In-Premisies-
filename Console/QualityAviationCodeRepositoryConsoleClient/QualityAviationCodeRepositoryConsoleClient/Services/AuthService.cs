using Microsoft.Extensions.Configuration;
using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    public interface IAuthService
    {
        string CurrentUsername { get; }
        string CurrentGatePass { get; }
        bool IsAuthenticated { get; }
        Task<bool> LoginAsync(string username, string password);
        void Logout();
        void SaveSession();
        void LoadSession();
    }

    public class AuthService : IAuthService
    {
        private readonly IApiService _apiService;
        private readonly string _sessionFile;
        private string _currentUsername;
        private string _currentGatePass;

        public string CurrentUsername => _currentUsername;
        public string CurrentGatePass => _currentGatePass;
        public bool IsAuthenticated => !string.IsNullOrEmpty(_currentGatePass);

        public AuthService(IApiService apiService, IConfiguration configuration)
        {
            _apiService = apiService;
            var workspacePath = configuration["LocalSettings:WorkspacePath"];
            Directory.CreateDirectory(workspacePath);
            _sessionFile = Path.Combine(workspacePath, ".session");
        }

        public async Task<bool> LoginAsync(string username, string password)
        {
            try
            {
                var response = await _apiService.PostAsync<AuthResponse>("auth/login", 
                    new AuthRequest { Username = username, Password = password }, 
                    isAnonymous: true);

                if (response?.Authenticated == true)
                {
                    _currentUsername = username;
                    _currentGatePass = response.GatePass;
                    _apiService.SetAuthToken(_currentGatePass);
                    SaveSession();
                    return true;
                }

                System.Console.WriteLine($"❌ Login failed: {response?.Message}");
                return false;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"❌ Error: {ex.Message}");
                return false;
            }
        }

        public void Logout()
        {
            _currentUsername = null;
            _currentGatePass = null;
            _apiService.SetAuthToken(null);
            File.Delete(_sessionFile);
        }

        public void SaveSession()
        {
            var sessionData = new
            {
                Username = _currentUsername,
                GatePass = _currentGatePass,
                SavedAt = DateTime.Now
            };

            var json = JsonSerializer.Serialize(sessionData);
            File.WriteAllText(_sessionFile, json);
        }

        public void LoadSession()
        {
            if (!File.Exists(_sessionFile))
                return;

            try
            {
                var json = File.ReadAllText(_sessionFile);
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    _currentUsername = root.GetProperty("Username").GetString();
                    _currentGatePass = root.GetProperty("GatePass").GetString();
                    _apiService.SetAuthToken(_currentGatePass);
                }
            }
            catch { }
        }
    }
}
