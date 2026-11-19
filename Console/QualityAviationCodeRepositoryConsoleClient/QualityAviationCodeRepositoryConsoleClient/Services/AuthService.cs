using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using System.Security.Cryptography;
using System.Text.Json;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    public interface IAuthService
    {
        string CurrentUsername { get; }
        string CurrentGatePass { get; }
        bool IsAuthenticated { get; }
        Task ConnectAsync();
        Task LoginAsync(string username, string password);
        Task ChangePasswordAsync(string currentPassword, string newPassword);
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

        public AuthService(IApiService apiService)
        {
            _apiService = apiService;

            // Per Windows user, outside any shared folder
            var sessionFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QARepository");
            Directory.CreateDirectory(sessionFolder);
            _sessionFile = Path.Combine(sessionFolder, "session.bin");
        }

        // The handshake every start goes through: reach a server over verified TLS,
        // then check that the saved login (if any) is still accepted
        public async Task ConnectAsync()
        {
            await _apiService.CheckConnectionAsync();

            LoadSession();
            if (!IsAuthenticated) return;

            try
            {
                await _apiService.GetAsync<ApiResponse>("auth/validate");
            }
            catch (ApiException ex) when (ex.IsUnauthorized)
            {
                Logout();
            }
        }

        public async Task LoginAsync(string username, string password)
        {
            try
            {
                var response = await _apiService.PostAsync<AuthResponse>("auth/login",
                    new AuthRequest { Username = username, Password = password });

                StartSession(response);
            }
            catch (ApiException ex) when (ex.IsUnauthorized)
            {
                // Wrong credentials, as opposed to a session that expired mid-work
                throw new ApiException(ex.Message);
            }
        }

        public async Task ChangePasswordAsync(string currentPassword, string newPassword)
        {
            var response = await _apiService.PostAsync<AuthResponse>("auth/change-password",
                new ChangePasswordRequest { CurrentPassword = currentPassword, NewPassword = newPassword });

            // Changing the password retires the old gate pass; the server sends its replacement
            StartSession(response);
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

            // Encrypted with the Windows account's own key: other users and other PCs cannot read it
            var json = JsonSerializer.SerializeToUtf8Bytes(sessionData);
            File.WriteAllBytes(_sessionFile, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
        }

        public void LoadSession()
        {
            if (!File.Exists(_sessionFile))
                return;

            try
            {
                var json = ProtectedData.Unprotect(File.ReadAllBytes(_sessionFile), null, DataProtectionScope.CurrentUser);
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

        private void StartSession(AuthResponse response)
        {
            if (response?.Authenticated != true || string.IsNullOrEmpty(response.GatePass))
                throw new ApiException(response?.Message ?? "Login failed.");

            _currentUsername = response.User?.Username;
            _currentGatePass = response.GatePass;
            _apiService.SetAuthToken(_currentGatePass);
            SaveSession();
        }
    }
}
