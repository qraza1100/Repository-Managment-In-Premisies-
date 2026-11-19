using Microsoft.Extensions.Configuration;
using QualityAviationCodeRepositoryConsoleClient.Console.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace QualityAviationCodeRepositoryConsoleClient.Console.Services
{
    public interface IApiService
    {
        string CurrentServer { get; }
        event Action<string> StatusChanged;
        Task CheckConnectionAsync();
        void SetAuthToken(string token);
        // agent: true sends the call to the deploy agent on the same server instead of the repository API
        Task<T> GetAsync<T>(string endpoint, bool agent = false);
        Task<T> PostAsync<T>(string endpoint, object body, bool agent = false);
        Task<string> DownloadFileAsync(string endpoint, string targetFile, IProgress<TransferProgress> progress = null, object postBody = null, bool agent = false);
        Task<T> UploadFileAsync<T>(string endpoint, string filePath, IProgress<TransferProgress> progress = null, bool agent = false);
        Task<bool> AgentAvailableAsync();

        // A web application the deploy agent publishes on this same server (the helpdesk), on its public port.
        // Same pinned certificate, same login. Reads are retried like every other call; changes are sent once,
        // so a reply lost on the way can never apply a change twice. content is built per attempt.
        Task<T> AppSendAsync<T>(int port, HttpMethod method, string path, Func<HttpContent> content = null, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default);
        Task AppDownloadAsync(int port, string path, string targetFile);
    }

    public class ApiService : IApiService
    {
        // How long the server may take to pack or unpack a large repository before it answers
        private static readonly TimeSpan ServerProcessingTimeout = TimeSpan.FromMinutes(10);
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly HttpClient _httpClient;
        private readonly List<Uri> _configuredServers = new();
        private readonly HashSet<string> _pinnedThumbprints = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _rememberedServerFile;
        private readonly int _discoveryPort;
        private readonly int _serverPort;
        private readonly int _agentPort;
        private bool _agentAvailable;
        private DateTime _agentCheckedAt = DateTime.MinValue;
        private readonly List<string> _searchNetworks;
        private readonly TimeSpan _timeout;
        private readonly TimeSpan _stallTimeout;
        private readonly int _maxRetries;
        // The addresses in use for this session: the one that answered first, then the fallbacks
        private List<Uri> _servers = new();
        private int _serverIndex;
        private string _rejectedThumbprint;
        private ApiException _connectProblem;

        public string CurrentServer => _servers.Count > 0 ? _servers[_serverIndex].ToString() : "(server not found yet)";

        public event Action<string> StatusChanged;

        public ApiService(IConfiguration configuration)
        {
            var urls = configuration.GetSection("ApiSettings:BaseUrls").GetChildren().Select(c => c.Value).ToList();
            if (urls.Count == 0) urls.Add(configuration["ApiSettings:BaseUrl"]);

            foreach (var url in urls.Where(u => !string.IsNullOrWhiteSpace(u)))
            {
                var server = new Uri(url.Trim().TrimEnd('/') + "/");

                // Credentials and source code never travel unencrypted, except to this same machine during development
                if (server.Scheme != Uri.UriSchemeHttps && !server.IsLoopback)
                    throw new ApiException($"Refusing to use {server}: the server address must be https://.");

                _configuredServers.Add(server);
            }

            foreach (var thumbprint in (configuration["ApiSettings:CertificateThumbprint"] ?? "").Split(',', ';'))
            {
                var normalized = thumbprint.Replace(":", "").Replace(" ", "").Trim();
                if (normalized.Length > 0) _pinnedThumbprints.Add(normalized);
            }

            _timeout = TimeSpan.FromSeconds(ReadNumber(configuration, "ApiSettings:TimeoutSeconds", 30));
            _stallTimeout = TimeSpan.FromSeconds(ReadNumber(configuration, "ApiSettings:TransferStallSeconds", 60));
            _maxRetries = ReadNumber(configuration, "ApiSettings:MaxRetries", 4);
            _discoveryPort = int.TryParse(configuration["ApiSettings:DiscoveryPort"], out var discoveryPort) ? discoveryPort : 5002;

            // The certificate thumbprint is how the server is recognised. Without it the server cannot be
            // searched for, and an address is then the only way to reach it.
            _serverPort = ReadNumber(configuration, "ApiSettings:ServerPort", 5001);
            _agentPort = ReadNumber(configuration, "ApiSettings:AgentPort", 5003);
            _searchNetworks = configuration.GetSection("ApiSettings:SearchNetworks").GetChildren()
                .Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();

            if (_configuredServers.Count == 0 && _pinnedThumbprints.Count == 0)
                throw new ApiException("The client does not know which server to trust. Set ApiSettings:CertificateThumbprint in appsettings.json " +
                    "(the server is then found automatically), or list its address in ApiSettings:BaseUrls.");

            var settingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QARepository");
            Directory.CreateDirectory(settingsFolder);
            _rememberedServerFile = Path.Combine(settingsFolder, "server.txt");
            _servers.AddRange(_configuredServers);

            var handler = new SocketsHttpHandler
            {
                // A server on the LAN or VPN answers in well under a second; waiting longer only delays the fallback
                ConnectTimeout = TimeSpan.FromSeconds(5),
                SslOptions = { RemoteCertificateValidationCallback = ValidateServerCertificate }
            };

            // Every call sets its own deadline, because a quick lookup and a large transfer need very different ones
            _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        // The start-up check. Finds the server and proves it is the right one before anything is sent to it.
        //
        // The server is searched for, not looked up: a question goes to this PC, to where the server was last
        // time, and then to every address of every network this PC is connected to. Only an answer signed by
        // the trusted certificate counts, and the HTTPS connection that follows checks that certificate again.
        // Addresses written in appsettings.json are only a last resort for a network the search cannot reach.
        public async Task CheckConnectionAsync()
        {
            _connectProblem = null;
            string searched = null;
            var lookalikes = new ConcurrentBag<string>();

            if (_pinnedThumbprints.Count > 0)
            {
                var found = await ServerDiscovery.FindAsync(_discoveryPort, _serverPort, _pinnedThumbprints, GetLastKnownAddresses(), _searchNetworks, ranges =>
                {
                    searched = ranges;
                    StatusChanged?.Invoke($"Searching the whole network for the server: {ranges} ...");
                }, lookalikes);

                if (found != null)
                {
                    if (await TryConnectAsync(found)) return;

                    _connectProblem ??= new ApiException($"The server answered the search from {found.Host}, but its HTTPS port {found.Port} could not be reached. " +
                        $"Check that the firewall on the server allows TCP port {found.Port}.");
                }
            }

            foreach (var candidate in _configuredServers)
                if (await TryConnectAsync(candidate)) return;

            _servers = new List<Uri>(_configuredServers);
            _serverIndex = 0;

            // The most useful thing to say when the server is there but this client was set up for another certificate
            if (_connectProblem == null && !lookalikes.IsEmpty)
                throw new ApiException($"A QA Repository server was found at {string.Join(", ", lookalikes.Distinct())}, but it does not present the certificate this client trusts. " +
                    "If that is your server, run \"<api exe> cert\" on it, check that it shows that thumbprint, and put it in ApiSettings:CertificateThumbprint in appsettings.json.");

            throw _connectProblem ?? new ApiException(
                "The QA Repository server could not be found. " + await ExplainLastSeenAsync() +
                (searched != null ? $"Searched {searched} and no trusted server answered. " : "") +
                (_configuredServers.Count > 0 ? $"Also tried {string.Join(", ", _configuredServers)}. " : "") +
                "Check that the API is running and that this PC is on the office network or connected to the VPN.");
        }

        // Turns "nothing answered" into something the user can act on, using the address the server was last seen at:
        // a machine that does not answer at all means the wrong network or no VPN; one that answers but keeps
        // the port shut means the API is not running there or a firewall is in the way.
        private async Task<string> ExplainLastSeenAsync()
        {
            Uri lastSeen = null;
            try
            {
                if (File.Exists(_rememberedServerFile))
                    Uri.TryCreate(File.ReadAllText(_rememberedServerFile).Trim(), UriKind.Absolute, out lastSeen);
            }
            catch (IOException) { }

            if (lastSeen == null || lastSeen.IsLoopback) return "";

            bool machineAnswers;
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                machineAnswers = (await ping.SendPingAsync(lastSeen.Host, 1500)).Status == System.Net.NetworkInformation.IPStatus.Success;
            }
            catch (System.Net.NetworkInformation.PingException)
            {
                machineAnswers = false;
            }

            return machineAnswers
                ? $"The server was last seen at {lastSeen.Host}. That machine answers on the network, but nothing accepts connections on port {lastSeen.Port}: " +
                  "the API is not running there, or a firewall between this PC and the server blocks that port. "
                : $"The server was last seen at {lastSeen.Host}, which does not answer from this PC: this PC is probably not on the office network " +
                  "or connected to the VPN, or the server is switched off. ";
        }

        // One quick, verified HTTPS attempt. On success the address becomes the one this session uses.
        private async Task<bool> TryConnectAsync(Uri candidate)
        {
            StatusChanged?.Invoke($"Connecting to {candidate} ...");
            _servers = new List<Uri> { candidate };
            _serverIndex = 0;

            try
            {
                await WithRetryAsync(async server =>
                {
                    using var deadline = new CancellationTokenSource(_timeout);
                    using var response = await _httpClient.GetAsync(new Uri(server, "api/health"), deadline.Token);
                    await EnsureSuccessAsync(response, deadline.Token);
                    return true;
                }, maxRetries: 0, pause: false);
            }
            catch (ApiException ex) when (ex.StatusCode == 0)
            {
                // Not answering, or answering with the wrong identity: either way it is not our server.
                // A certificate complaint is kept, because it tells the user exactly what to fix.
                if (_rejectedThumbprint != null) _connectProblem ??= ex;
                return false;
            }

            // Remembered so the next search asks here first; the other addresses stay as fallbacks for this session
            _servers.AddRange(_configuredServers.Where(other => other != candidate));
            File.WriteAllText(_rememberedServerFile, candidate.ToString());
            return true;
        }

        // Where the server has been seen before. These are only asked first; they prove nothing by themselves.
        private List<IPAddress> GetLastKnownAddresses()
        {
            var hosts = _configuredServers.Select(server => server.Host).ToList();
            try
            {
                if (File.Exists(_rememberedServerFile) && Uri.TryCreate(File.ReadAllText(_rememberedServerFile).Trim(), UriKind.Absolute, out var remembered))
                    hosts.Insert(0, remembered.Host);
            }
            catch (IOException) { }

            var addresses = new List<IPAddress>();
            foreach (var host in hosts)
                if (IPAddress.TryParse(host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    addresses.Add(address);
            return addresses;
        }

        public void SetAuthToken(string token)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token);
        }

        public Task<T> GetAsync<T>(string endpoint, bool agent = false)
        {
            return WithRetryAsync(async server =>
            {
                using var deadline = new CancellationTokenSource(_timeout);
                using var response = await _httpClient.GetAsync(new Uri(server, $"api/{endpoint}"), deadline.Token);
                return await ReadJsonAsync<T>(response, deadline.Token);
            }, agent: agent);
        }

        public Task<T> PostAsync<T>(string endpoint, object body, bool agent = false)
        {
            return WithRetryAsync(async server =>
            {
                using var deadline = new CancellationTokenSource(_timeout);
                var json = JsonSerializer.Serialize(body);
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(new Uri(server, $"api/{endpoint}"), content, deadline.Token);
                return await ReadJsonAsync<T>(response, deadline.Token);
            }, agent: agent);
        }

        // Asked once, then remembered for a minute: whether the deploy agent runs next to the repository API.
        // Without it the client still works; it then sends and fetches whole versions.
        public async Task<bool> AgentAvailableAsync()
        {
            if (_servers.Count == 0) return false;
            if (DateTime.Now - _agentCheckedAt < TimeSpan.FromMinutes(1)) return _agentAvailable;

            try
            {
                await WithRetryAsync(async server =>
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    using var response = await _httpClient.GetAsync(new Uri(server, "api/health"), deadline.Token);
                    await EnsureSuccessAsync(response, deadline.Token);
                    return true;
                }, maxRetries: 0, pause: false, agent: true);
                _agentAvailable = true;
            }
            catch (ApiException)
            {
                _agentAvailable = false;
            }

            _agentCheckedAt = DateTime.Now;
            return _agentAvailable;
        }

        // Streams the response to targetFile. Returns the version the server resolved the request to.
        // With postBody the request is a POST carrying it as JSON.
        public Task<string> DownloadFileAsync(string endpoint, string targetFile, IProgress<TransferProgress> progress = null, object postBody = null, bool agent = false)
        {
            return WithRetryAsync(async server =>
            {
                // The watchdog is pushed back every time data arrives: a long transfer is fine, a stalled one is not
                using var watchdog = new CancellationTokenSource(ServerProcessingTimeout);
                using var request = new HttpRequestMessage(postBody == null ? HttpMethod.Get : HttpMethod.Post, new Uri(server, $"api/{endpoint}"));
                if (postBody != null)
                    request.Content = new StringContent(JsonSerializer.Serialize(postBody), System.Text.Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
                await EnsureSuccessAsync(response, watchdog.Token);

                var total = response.Content.Headers.ContentLength ?? 0;
                var throttle = Stopwatch.StartNew();
                long received = 0;

                await using (var source = await response.Content.ReadAsStreamAsync(watchdog.Token))
                await using (var target = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    int read;
                    watchdog.CancelAfter(_stallTimeout);
                    while ((read = await source.ReadAsync(buffer, watchdog.Token)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), watchdog.Token);
                        received += read;
                        watchdog.CancelAfter(_stallTimeout);

                        if (throttle.ElapsedMilliseconds >= 100)
                        {
                            progress?.Report(new TransferProgress(received, total));
                            throttle.Restart();
                        }
                    }
                }

                if (total > 0 && received != total)
                    throw new IOException("The download ended before the whole file arrived.");

                progress?.Report(new TransferProgress(received, received));
                return response.Headers.TryGetValues("X-Version", out var version) ? version.FirstOrDefault() : null;
            }, agent: agent);
        }

        // Streams filePath as the request body, so the file is never held in memory
        public Task<T> UploadFileAsync<T>(string endpoint, string filePath, IProgress<TransferProgress> progress = null, bool agent = false)
        {
            return WithRetryAsync(async server =>
            {
                using var watchdog = new CancellationTokenSource(_stallTimeout);
                await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

                var total = file.Length;
                var throttle = Stopwatch.StartNew();
                using var content = new ProgressStreamContent(file, sent =>
                {
                    // Once the last byte is out, the wait is for the server to unpack and store the version
                    watchdog.CancelAfter(sent >= total ? ServerProcessingTimeout : _stallTimeout);

                    if (sent >= total || throttle.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new TransferProgress(sent, total));
                        throttle.Restart();
                    }
                });
                content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, $"api/{endpoint}")) { Content = content };

                // Lets the server refuse (expired login, unknown repository, too large) before any of the file is sent
                request.Headers.ExpectContinue = true;

                using var response = await _httpClient.SendAsync(request, watchdog.Token);
                return await ReadJsonAsync<T>(response, watchdog.Token);
            }, agent: agent);
        }

        public Task<T> AppSendAsync<T>(int port, HttpMethod method, string path, Func<HttpContent> content = null, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            return WithRetryAsync(async server =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(timeout ?? _timeout);
                using var request = new HttpRequestMessage(method, new Uri(server, path)) { Content = content?.Invoke() };
                using var response = await _httpClient.SendAsync(request, deadline.Token);
                return await ReadJsonAsync<T>(response, deadline.Token);
            }, maxRetries: method == HttpMethod.Get ? null : 0, appPort: port, stop: cancellationToken);
        }

        public Task AppDownloadAsync(int port, string path, string targetFile)
        {
            return WithRetryAsync(async server =>
            {
                using var watchdog = new CancellationTokenSource(_stallTimeout);
                using var response = await _httpClient.GetAsync(new Uri(server, path), HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
                await EnsureSuccessAsync(response, watchdog.Token);

                // Into a temporary file first, so a broken download never leaves half a file under the chosen name
                var partial = targetFile + ".part";
                try
                {
                    await using (var source = await response.Content.ReadAsStreamAsync(watchdog.Token))
                    await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await source.ReadAsync(buffer, watchdog.Token)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), watchdog.Token);
                            watchdog.CancelAfter(_stallTimeout);
                        }
                    }
                    File.Move(partial, targetFile, overwrite: true);
                    return true;
                }
                finally
                {
                    if (File.Exists(partial)) File.Delete(partial);
                }
            }, appPort: port);
        }

        // Network trouble is retried with growing pauses, moving to the next configured address each time.
        // Answers from the server (wrong password, unknown repository...) are final and are not retried.
        // appPort: a web application on the same server; stop: the caller gave up, which is not a network problem.
        private async Task<T> WithRetryAsync<T>(Func<Uri, Task<T>> attempt, int? maxRetries = null, bool pause = true, bool agent = false,
            int appPort = 0, CancellationToken stop = default)
        {
            if (_servers.Count == 0)
                throw new ApiException("Not connected to a server yet.");

            var retryLimit = maxRetries ?? _maxRetries;

            for (var failures = 0; ; failures++)
            {
                try
                {
                    _rejectedThumbprint = null;
                    // The deploy agent and the applications it publishes run on the same server as the repository API, each on its own port
                    var server = _servers[_serverIndex];
                    var port = appPort > 0 ? appPort : agent ? _agentPort : 0;
                    return await attempt(port > 0 ? new UriBuilder(server) { Port = port }.Uri : server);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or TransientServerException && !stop.IsCancellationRequested)
                {
                    if (ex.InnerException is AuthenticationException || _rejectedThumbprint != null)
                        throw new ApiException(DescribeCertificateProblem(), inner: ex);

                    if (failures >= retryLimit)
                    {
                        // Start from the preferred address again next time
                        _serverIndex = 0;
                        throw new ApiException(appPort > 0
                            ? $"Cannot reach the helpdesk on {string.Join(" or ", _servers.Select(s => $"{s.Host}:{appPort}"))} ({DescribeNetworkProblem(ex)}). " +
                              "Check in Deployments that the helpdesk application is running, and that its port is open in the server's firewall."
                            : agent
                            ? $"Cannot reach the deploy agent on {string.Join(" or ", _servers.Select(s => $"{s.Host}:{_agentPort}"))} ({DescribeNetworkProblem(ex)}). " +
                              "Check that the QA Deploy Agent is installed and running on the server and that its port is open."
                            : $"Cannot reach the server at {string.Join(" or ", _servers)} ({DescribeNetworkProblem(ex)}). " +
                              "Check your network or VPN connection and that the API is running.", inner: ex);
                    }

                    var unreachable = CurrentServer;
                    _serverIndex = (_serverIndex + 1) % _servers.Count;

                    // Said out loud, so a wait never looks like a hang
                    StatusChanged?.Invoke($"No answer from {unreachable} ({DescribeNetworkProblem(ex)}). Trying {CurrentServer} (attempt {failures + 2} of {retryLimit + 1})...");

                    if (pause) await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Pow(2, failures), 15)));
                }
            }
        }

        private async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            await EnsureSuccessAsync(response, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<T>(content, JsonOptions);
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode) return;

            var status = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                throw new TransientServerException($"server answered {status}");

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ApiException("Too many attempts. Wait a minute and try again.", status);

            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                throw new ApiException("The archive is larger than the server accepts. Add build output or large files to .qaignore.", status);

            // The server explains refusals as { "message": ... }; model validation uses { "title": ... }
            var message = $"The server answered {status} {response.ReasonPhrase}";
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String) continue;
                    if (property.NameEquals("message") || property.NameEquals("Message") || property.NameEquals("title"))
                    {
                        message = property.Value.GetString();
                        break;
                    }
                }
            }
            catch (JsonException) { /* not JSON: keep the status line */ }

            throw new ApiException(message, status);
        }

        private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            if (certificate == null) return false;

            var thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);

            // A pinned thumbprint is the whole trust decision: it identifies the exact server certificate,
            // which is what makes a self-signed internal server safe to talk to
            var trusted = _pinnedThumbprints.Count > 0
                ? _pinnedThumbprints.Contains(thumbprint)
                : errors == SslPolicyErrors.None;

            if (!trusted) _rejectedThumbprint = thumbprint;
            return trusted;
        }

        private string DescribeCertificateProblem()
        {
            var presented = _rejectedThumbprint ?? "unknown";
            return _pinnedThumbprints.Count > 0
                ? $"The server at {CurrentServer} presented a different certificate than the one this client trusts (got {presented}). " +
                  "Do not continue unless the server certificate was deliberately replaced; in that case update ApiSettings:CertificateThumbprint in appsettings.json."
                : $"The certificate of {CurrentServer} is not trusted by this PC. If its SHA-256 thumbprint {presented} matches the one the API " +
                  "shows at startup (or \"<api exe> cert\" on the host), set it as ApiSettings:CertificateThumbprint in appsettings.json.";
        }

        private static int ReadNumber(IConfiguration configuration, string key, int fallback)
        {
            return int.TryParse(configuration[key], out var value) && value > 0 ? value : fallback;
        }

        private static string DescribeNetworkProblem(Exception ex)
        {
            return ex switch
            {
                OperationCanceledException => "the connection timed out",
                TransientServerException => ex.Message,
                _ => (ex.InnerException ?? ex).Message.TrimEnd('.')
            };
        }

        private sealed class TransientServerException : Exception
        {
            public TransientServerException(string message) : base(message) { }
        }

        private sealed class ProgressStreamContent : HttpContent
        {
            private readonly Stream _source;
            private readonly Action<long> _onProgress;

            public ProgressStreamContent(Stream source, Action<long> onProgress)
            {
                _source = source;
                _onProgress = onProgress;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                return SerializeToStreamAsync(stream, context, CancellationToken.None);
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context, CancellationToken cancellationToken)
            {
                var buffer = new byte[81920];
                long sent = 0;
                int read;
                while ((read = await _source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sent += read;
                    _onProgress(sent);
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _source.Length;
                return true;
            }
        }
    }
}
