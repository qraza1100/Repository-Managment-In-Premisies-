using Microsoft.Extensions.Configuration;
using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    public interface IApiService
    {
        void SetAuthToken(string token);
        Task<T> GetAsync<T>(string endpoint, bool isAnonymous = false);
        Task<T> PostAsync<T>(string endpoint, object body, bool isAnonymous = false);
        Task<byte[]> GetFileAsync(string endpoint);
        Task UploadFileAsync(string endpoint, string filePath, Dictionary<string, string> parameters);
    }

    public class ApiService : IApiService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private string _authToken;

        public ApiService(IConfiguration configuration)
        {
            _configuration = configuration;
            var baseUrl = configuration["ApiSettings:BaseUrl"];
            var timeout = int.Parse(configuration["ApiSettings:Timeout"] ?? "300000");

            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromMilliseconds(timeout)
            };
        }

        public void SetAuthToken(string token)
        {
            _authToken = token;
            _httpClient.DefaultRequestHeaders.Authorization = 
                string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token);
        }

        public async Task<T> GetAsync<T>(string endpoint, bool isAnonymous = false)
        {
            try
            {
                var url = $"api/{endpoint}";
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    System.Console.WriteLine($"❌ API Error: {response.StatusCode}");
                    return default;
                }

                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<T>(content, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"❌ Request failed: {ex.Message}");
                return default;
            }
        }

        public async Task<T> PostAsync<T>(string endpoint, object body, bool isAnonymous = false)
        {
            try
            {
                var url = $"api/{endpoint}";
                var json = JsonSerializer.Serialize(body);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(url, content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    System.Console.WriteLine($"❌ API Error: {response.StatusCode}");
                    return default;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<T>(responseContent, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"❌ Request failed: {ex.Message}");
                return default;
            }
        }

        public async Task<byte[]> GetFileAsync(string endpoint)
        {
            try
            {
                var url = $"api/{endpoint}";
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    System.Console.WriteLine($"❌ API Error: {response.StatusCode}");
                    return null;
                }

                return await response.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"❌ Download failed: {ex.Message}");
                return null;
            }
        }

        public async Task UploadFileAsync(string endpoint, string filePath, Dictionary<string, string> parameters)
        {
            try
            {
                using (var form = new MultipartFormDataContent())
                {
                    if (File.Exists(filePath))
                    {
                        var fileContent = await System.IO.File.ReadAllBytesAsync(filePath);
                        form.Add(new ByteArrayContent(fileContent), "archiveFile", Path.GetFileName(filePath));
                    }

                    foreach (var param in parameters)
                    {
                        form.Add(new StringContent(param.Value), param.Key);
                    }

                    var url = $"api/{endpoint}";
                    var response = await _httpClient.PostAsync(url, form);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        System.Console.WriteLine($"❌ Upload failed: {response.StatusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"❌ Upload error: {ex.Message}");
            }
        }
    }
}
