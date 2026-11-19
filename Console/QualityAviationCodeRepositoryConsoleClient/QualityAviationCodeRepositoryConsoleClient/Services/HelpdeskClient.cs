#nullable disable
using Microsoft.Extensions.Configuration;
using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using QualityAviationHelpdesk.Contracts;
using QualityAviationRepositoryShared;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    // The IT side of the Q+ Helpdesk: review and work on tickets, edit the web form's fields, the team's to-do list.
    // Shared by the Helpdesk tab of the Windows application and the terminal's Helpdesk menu, so both do the same.
    //
    // The helpdesk is a web application the deploy agent runs on the same server; its address is found from the
    // agent (the application named Helpdesk:AppName), and the client's own login is sent with every call.
    public interface IHelpdeskClient
    {
        string Address { get; }
        Task<ItMe> MeAsync();
        Task<TicketPage> TicketsAsync(string status, string assignee, string search, int page = 1, int pageSize = 100);
        Task<TicketDetail> TicketAsync(int id);
        Task<TicketDetail> ActAsync(int id, string action, TicketActionRequest request);
        Task<TicketDetail> EditAsync(int id, TicketEditRequest request);
        Task<TicketDetail> CommentAsync(int id, string body, bool internalOnly, int minutes, IReadOnlyList<string> files);
        Task SaveAttachmentAsync(Guid id, string targetFile);

        Task<List<FieldDefinitionDto>> FieldsAsync();
        Task<List<FieldDefinitionDto>> SaveFieldAsync(FieldDefinitionDto field);
        Task<List<FieldDefinitionDto>> MoveFieldAsync(int id, int direction);
        Task<HelpdeskSettings> SettingsAsync();
        Task<HelpdeskSettings> SaveSettingsAsync(HelpdeskSettings settings);

        Task<List<TodoDto>> TodosAsync(bool includeDone, bool archived);
        Task<TodoDto> SaveTodoAsync(int id, TodoSaveRequest request);
        Task<TodoDto> ArchiveTodoAsync(int id, string version, bool restore);
        Task<List<TimelineEntry>> TodoHistoryAsync(int id);

        Task<ChangeBatch> ChangesAsync(long after, CancellationToken cancellationToken);
        Task<AuditVerification> VerifyAuditAsync();
        Task<List<TimelineEntry>> AuditAsync(int take);
    }

    public class HelpdeskClient : IHelpdeskClient
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly IApiService _api;
        private readonly IDeployClient _deploy;
        private readonly string _appName;
        private readonly int _configuredPort;
        private int _port;
        private DateTime _portFoundAt;

        public HelpdeskClient(IApiService api, IDeployClient deploy, IConfiguration configuration)
        {
            _api = api;
            _deploy = deploy;
            _appName = string.IsNullOrWhiteSpace(configuration["Helpdesk:AppName"]) ? "helpdesk" : configuration["Helpdesk:AppName"].Trim();
            _configuredPort = int.TryParse(configuration["Helpdesk:Port"], out var port) && port > 0 ? port : 0;
        }

        public string Address => _port > 0 ? $"{new Uri(_api.CurrentServer).Host}:{_port}" : "(not found yet)";

        public Task<ItMe> MeAsync() => Get<ItMe>("api/it/me");

        public Task<TicketPage> TicketsAsync(string status, string assignee, string search, int page = 1, int pageSize = 100) =>
            Get<TicketPage>($"api/it/tickets?status={E(status)}&assignee={E(assignee)}&q={E(search)}&page={page}&pageSize={pageSize}");

        public Task<TicketDetail> TicketAsync(int id) => Get<TicketDetail>($"api/it/tickets/{id}");

        public Task<TicketDetail> ActAsync(int id, string action, TicketActionRequest request) => Post<TicketDetail>($"api/it/tickets/{id}/actions/{E(action)}", request);

        public Task<TicketDetail> EditAsync(int id, TicketEditRequest request) => Post<TicketDetail>($"api/it/tickets/{id}/edit", request);

        public async Task<TicketDetail> CommentAsync(int id, string body, bool internalOnly, int minutes, IReadOnlyList<string> files)
        {
            files ??= Array.Empty<string>();
            foreach (var file in files)
                if (!File.Exists(file)) throw new ApiException($"{file} does not exist.");

            var port = await PortAsync();
            return await _api.AppSendAsync<TicketDetail>(port, HttpMethod.Post, $"api/it/tickets/{id}/comments", () =>
            {
                var form = new MultipartFormDataContent
                {
                    { new StringContent(body ?? "", Encoding.UTF8), "body" },
                    { new StringContent(internalOnly ? "true" : "false"), "internal" },
                    { new StringContent(minutes.ToString()), "minutes" }
                };
                foreach (var file in files)
                {
                    var content = new StreamContent(File.OpenRead(file));
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    form.Add(content, "files", Path.GetFileName(file));
                }
                return form;
            }, timeout: TimeSpan.FromMinutes(5));
        }

        public async Task SaveAttachmentAsync(Guid id, string targetFile) => await _api.AppDownloadAsync(await PortAsync(), $"api/it/attachments/{id}", targetFile);

        public Task<List<FieldDefinitionDto>> FieldsAsync() => Get<List<FieldDefinitionDto>>("api/it/fields");

        public Task<List<FieldDefinitionDto>> SaveFieldAsync(FieldDefinitionDto field) => Post<List<FieldDefinitionDto>>("api/it/fields", field);

        public Task<List<FieldDefinitionDto>> MoveFieldAsync(int id, int direction) => Post<List<FieldDefinitionDto>>($"api/it/fields/{id}/move?direction={direction}", new { });

        public Task<HelpdeskSettings> SettingsAsync() => Get<HelpdeskSettings>("api/it/settings");

        public Task<HelpdeskSettings> SaveSettingsAsync(HelpdeskSettings settings) => Post<HelpdeskSettings>("api/it/settings", settings);

        public Task<List<TodoDto>> TodosAsync(bool includeDone, bool archived) =>
            Get<List<TodoDto>>($"api/it/todos?done={includeDone.ToString().ToLowerInvariant()}&archived={archived.ToString().ToLowerInvariant()}");

        public Task<TodoDto> SaveTodoAsync(int id, TodoSaveRequest request) => Post<TodoDto>(id == 0 ? "api/it/todos" : $"api/it/todos/{id}", request);

        public Task<TodoDto> ArchiveTodoAsync(int id, string version, bool restore) =>
            Post<TodoDto>($"api/it/todos/{id}/archive?restore={restore.ToString().ToLowerInvariant()}", new TodoSaveRequest { Version = version });

        public Task<List<TimelineEntry>> TodoHistoryAsync(int id) => Get<List<TimelineEntry>>($"api/it/todos/{id}/history");

        // Waits on the server until something changes (at most ~25 s). after 0: just learn where the feed is now.
        public async Task<ChangeBatch> ChangesAsync(long after, CancellationToken cancellationToken)
        {
            var port = await PortAsync();
            return await _api.AppSendAsync<ChangeBatch>(port, HttpMethod.Get, $"api/it/changes?after={after}", timeout: TimeSpan.FromSeconds(45), cancellationToken: cancellationToken);
        }

        public Task<AuditVerification> VerifyAuditAsync() => Get<AuditVerification>("api/it/audit/verify", TimeSpan.FromMinutes(3));

        public Task<List<TimelineEntry>> AuditAsync(int take) => Get<List<TimelineEntry>>($"api/it/audit?take={take}");

        private async Task<T> Get<T>(string path, TimeSpan? timeout = null)
        {
            var port = await PortAsync();
            try
            {
                return await _api.AppSendAsync<T>(port, HttpMethod.Get, path, timeout: timeout);
            }
            catch (ApiException ex) when (ex.StatusCode == 0)
            {
                // Maybe it moved to another port: look it up again next time
                _portFoundAt = DateTime.MinValue;
                throw;
            }
        }

        private async Task<T> Post<T>(string path, object body)
        {
            var port = await PortAsync();
            var json = JsonSerializer.Serialize(body, body.GetType(), Json);
            return await _api.AppSendAsync<T>(port, HttpMethod.Post, path, () => new StringContent(json, Encoding.UTF8, "application/json"));
        }

        // The helpdesk's public port, from the deploy agent (looked up again every few minutes)
        private async Task<int> PortAsync()
        {
            if (_configuredPort > 0) return _port = _configuredPort;
            if (_port > 0 && DateTime.Now - _portFoundAt < TimeSpan.FromMinutes(5)) return _port;

            var overview = await _deploy.OverviewAsync();
            var app = overview.Apps.Select(a => a.App).FirstOrDefault(a => string.Equals(a.Name, _appName, StringComparison.OrdinalIgnoreCase));
            if (app == null)
                throw new ApiException($"The helpdesk is not set up on the server yet: in Deployments, register the helpdesk repository as an application named \"{_appName}\", with a public port and HTTPS, and deploy it.");
            if (app.PublicPort <= 0)
                throw new ApiException($"The \"{_appName}\" application has no public port. Give it one in Deployments → Settings.");
            if (!app.Https)
                throw new ApiException($"The \"{_appName}\" application is published without HTTPS, so the client will not send its login to it. Turn HTTPS on in Deployments → Settings.");

            _port = app.PublicPort;
            _portFoundAt = DateTime.Now;
            return _port;
        }

        private static string E(string value) => Uri.EscapeDataString(value ?? "");
    }

    // How helpdesk things read in both control panels
    public static class HelpdeskText
    {
        public static string Ago(DateTime? utc)
        {
            if (utc == null) return "";
            var span = DateTime.UtcNow - utc.Value.ToUniversalTime();
            return span.TotalMinutes < 1 ? "just now"
                : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min"
                : span.TotalHours < 24 ? $"{(int)span.TotalHours} h"
                : $"{(int)span.TotalDays} d";
        }

        public static string Due(DateTime? utc)
        {
            if (utc == null) return "";
            var span = utc.Value.ToUniversalTime() - DateTime.UtcNow;
            var late = span < TimeSpan.Zero;
            span = span.Duration();
            var amount = span.TotalHours < 1 ? $"{Math.Max(1, (int)span.TotalMinutes)} min" : span.TotalHours < 48 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalDays} d";
            return late ? $"{amount} late" : $"in {amount}";
        }

        public static string When(DateTime? utc) => utc == null ? "" : utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        public static string Size(long bytes) =>
            bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";

        // What a person typed for a field, in the form the server expects back
        public static string FieldLabel(List<FieldDefinitionDto> fields, string key) => fields.FirstOrDefault(f => f.Key == key)?.Label ?? key;
    }
}
