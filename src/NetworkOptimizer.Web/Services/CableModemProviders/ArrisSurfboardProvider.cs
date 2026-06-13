using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for ARRIS Surfboard modems (S33/S34, SB8200, SB6183, T25).
/// Supports S34 HNAP-over-HTTPS, SB8200 token-based auth (HTTPS), and SB6183 simple page fetch (HTTP).
/// Auto-detects model by trying S34 HNAP first, then SB8200 HTTPS, then SB6183 HTTP.
/// </summary>
public sealed class ArrisSurfboardProvider : ICableModemProvider, IDisposable
{
    /// <inheritdoc/>
    public string ProviderKey => "arris-surfboard";

    /// <inheritdoc/>
    public string DisplayName => "ARRIS Surfboard (HTTP)";

    private const string Sb8200StatusPath = "/cmconnectionstatus.html";
    private const string S34StatusPath = "/Cmconnectionstatus.html";
    private const string Sb6183StatusPath = "/RgConnect.asp";
    private const string HnapPath = "/HNAP1/";
    private const string SoapActionPrefix = "http://purenetworks.com/HNAP1/";
    private const string RowDelimiter = "|+|";
    private const char ColumnDelimiter = '^';
    private const int TimeoutSeconds = 15;

    private readonly ILogger<ArrisSurfboardProvider> _logger;

    /// <summary>
    /// Cached auth tokens keyed by CmConfiguration.Id.
    /// SB8200 requires token-based auth; tokens are cached until they expire.
    /// </summary>
    private readonly ConcurrentDictionary<int, string> _tokenCache = new();
    private readonly ConcurrentDictionary<int, HnapSession> _hnapSessions = new();
    private readonly ConditionalWeakTable<HttpClient, CookieContainer> _hnapCookieContainers = new();

    public ArrisSurfboardProvider(ILogger<ArrisSurfboardProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<CableModemStats?> PollAsync(
        CmPollContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("ARRIS Surfboard poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        try
        {
            // Try S33/S34 HNAP first. S34 commonly keeps legacy port 80 config defaults,
            // while HNAP uses HTTPS/443; probing SB8200 first can time out before HNAP runs.
            var hnapStats = await TryS34HnapAsync(context, cancellationToken);
            if (hnapStats != null)
            {
                _logger.LogDebug(
                    "ARRIS HNAP {Name} polled: {Model}, {DsCount} DS channels, {UsCount} US channels",
                    context.Name, hnapStats.DeviceModel,
                    hnapStats.DownstreamChannels.Count, hnapStats.UpstreamChannels.Count);
                return hnapStats;
            }

            // Fall back to SB8200 (HTTPS with token auth)
            var html = await TrySb8200Async(context, cancellationToken);
            if (html != null)
            {
                var stats = ParseSb8200(html, context);
                _logger.LogDebug(
                    "ARRIS SB8200 {Name} polled: {DsCount} DS channels, {UsCount} US channels",
                    context.Name, stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);

                // Logout to free the session
                await LogoutAsync(context, cancellationToken);
                return stats;
            }

            // Fall back to SB6183 (HTTP, no auth)
            html = await TrySb6183Async(context, cancellationToken);
            if (html != null)
            {
                var stats = ParseSb6183(html, context);
                _logger.LogDebug(
                    "ARRIS SB6183 {Name} polled: {DsCount} DS channels, {UsCount} US channels",
                    context.Name, stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);
                return stats;
            }

            _logger.LogWarning("ARRIS Surfboard {Name} at {Host}: both SB8200 and SB6183 fetch failed",
                context.Name, context.Host);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling ARRIS Surfboard {Name} at {Host}", context.Name, context.Host);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<(bool Success, string Message)> TestConnectionAsync(
        CmPollContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return (false, "Host is empty");

        try
        {
            // Try S33/S34 HNAP first. S34 commonly keeps legacy port 80 config defaults,
            // while HNAP uses HTTPS/443; probing SB8200 first can time out before HNAP runs.
            var hnapStats = await TryS34HnapAsync(context, cancellationToken);
            if (hnapStats != null)
            {
                return (true, $"Connected to {hnapStats.DeviceModel} (HNAP) - " +
                    $"{hnapStats.DownstreamChannels.Count} downstream, {hnapStats.UpstreamChannels.Count} upstream channels detected");
            }

            // Fall back to SB8200
            var html = await TrySb8200Async(context, cancellationToken);
            if (html != null)
            {
                await LogoutAsync(context, cancellationToken);
                return (true, "Connected to ARRIS SB8200 (HTTPS with token auth)");
            }

            // Try SB6183
            html = await TrySb6183Async(context, cancellationToken);
            if (html != null)
            {
                return (true, "Connected to ARRIS SB6183 (HTTP)");
            }

            return (false, "Could not connect via HNAP (S33/S34), HTTPS (SB8200), or HTTP (SB6183). Check host and credentials.");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private async Task<CableModemStats?> TryS34HnapAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Password))
            return null;

        using var client = CreateHnapHttpClient();
        var baseUrl = BuildHttpsBaseUrl(context);
        var endpoint = baseUrl + HnapPath;

        try
        {
            var session = await EnsureHnapSessionAsync(client, endpoint, context, cancellationToken);
            if (session == null)
                return null;

            using var deviceResponse = await CallMultipleHnapsAsync(
                client,
                endpoint,
                session,
                ["GetArrisDeviceStatus", "GetDsPartSetting"],
                cancellationToken);

            using var channelResponse = await CallMultipleHnapsAsync(
                client,
                endpoint,
                session,
                ["GetCustomerStatusDownstreamChannelInfo", "GetCustomerStatusUpstreamChannelInfo"],
                cancellationToken);

            if (channelResponse == null)
            {
                _hnapSessions.TryRemove(context.Id, out _);
                return null;
            }

            return ParseS34Hnap(deviceResponse, channelResponse, context);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _hnapSessions.TryRemove(context.Id, out _);
            _logger.LogDebug(ex, "ARRIS HNAP request failed for {Name} at {Host}", context.Name, context.Host);
            return null;
        }
    }

    private async Task<HnapSession?> EnsureHnapSessionAsync(
        HttpClient client,
        string endpoint,
        CmPollContext context,
        CancellationToken cancellationToken)
    {
        if (_hnapSessions.TryGetValue(context.Id, out var cached))
        {
            ApplyHnapSession(client, endpoint[..^HnapPath.Length], cached);

            // Validate the cached session with the same GetMultipleHNAPs shape the real
            // fetches use, so a passing check guarantees the subsequent polls will work.
            using var testResponse = await CallMultipleHnapsAsync(
                client,
                endpoint,
                cached,
                ["GetArrisDeviceStatus"],
                cancellationToken);

            if (testResponse != null)
                return cached;

            _hnapSessions.TryRemove(context.Id, out _);
        }

        var session = await LoginHnapAsync(client, endpoint, context, cancellationToken);
        if (session != null)
            _hnapSessions[context.Id] = session;

        return session;
    }

    private async Task<HnapSession?> LoginHnapAsync(
        HttpClient client,
        string endpoint,
        CmPollContext context,
        CancellationToken cancellationToken)
    {
        var username = string.IsNullOrWhiteSpace(context.Username) ? "admin" : context.Username;
        var password = context.Password ?? "";

        await SeedHnapSessionAsync(client, endpoint[..^HnapPath.Length], cancellationToken);
        await ProbeS34DeviceStatusAsync(client, endpoint, cancellationToken);

        var requestPayload = new Dictionary<string, object>
        {
            ["Login"] = new Dictionary<string, string>
            {
                ["Action"] = "request",
                ["Username"] = username,
                ["LoginPassword"] = "",
                ["Captcha"] = "",
                ["PrivateLogin"] = "LoginPassword",
            },
        };

        using var phase1Response = await PostHnapAsync(
            client, endpoint, "Login", "withoutloginkey", requestPayload, cancellationToken);

        if (phase1Response == null || !phase1Response.RootElement.TryGetProperty("LoginResponse", out var loginResp))
            return null;

        if (!loginResp.TryGetProperty("PublicKey", out var publicKeyEl) ||
            !loginResp.TryGetProperty("Challenge", out var challengeEl) ||
            !loginResp.TryGetProperty("Cookie", out var cookieEl))
        {
            return null;
        }

        var publicKey = publicKeyEl.GetString() ?? "";
        var challenge = challengeEl.GetString() ?? "";
        var uid = cookieEl.GetString();

        var privateKey = HmacSha256Hex(publicKey + password, challenge);
        var loginPassword = HmacSha256Hex(privateKey, challenge);
        var session = new HnapSession(privateKey, uid);
        ApplyHnapSession(client, endpoint[..^HnapPath.Length], session);

        var loginPayload = new Dictionary<string, object>
        {
            ["Login"] = new Dictionary<string, string>
            {
                ["Action"] = "login",
                ["Username"] = username,
                ["LoginPassword"] = loginPassword,
                ["Captcha"] = "",
                ["PrivateLogin"] = "LoginPassword",
            },
        };

        using var phase2Response = await PostHnapAsync(
            client, endpoint, "Login", privateKey, loginPayload, cancellationToken);

        if (phase2Response == null || !phase2Response.RootElement.TryGetProperty("LoginResponse", out var authResp))
            return null;

        var result = authResp.TryGetProperty("LoginResult", out var resultEl) ? resultEl.GetString() : null;
        if (!string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase))
            return null;

        return session;
    }

    private async Task ProbeS34DeviceStatusAsync(HttpClient client, string endpoint, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["GetMultipleHNAPs"] = new Dictionary<string, string>
            {
                ["GetArrisDeviceStatus"] = "",
            },
        };

        using var response = await PostHnapAsync(
            client,
            endpoint,
            "GetMultipleHNAPs",
            "withoutloginkey",
            payload,
            cancellationToken);
    }

    private async Task SeedHnapSessionAsync(HttpClient client, string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetAsync(baseUrl + "/Login.html", cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "ARRIS HNAP login page seed failed");
        }
    }

    private async Task<JsonDocument?> CallMultipleHnapsAsync(
        HttpClient client,
        string endpoint,
        HnapSession session,
        string[] actions,
        CancellationToken cancellationToken)
    {
        var innerDict = new Dictionary<string, string>();
        foreach (var action in actions)
            innerDict[action] = "";

        var payload = new Dictionary<string, object>
        {
            ["GetMultipleHNAPs"] = innerDict,
        };

        var response = await PostHnapAsync(
            client, endpoint, "GetMultipleHNAPs", session.PrivateKey, payload, cancellationToken, S34StatusPath);

        if (response == null)
            return null;

        if (response.RootElement.TryGetProperty("GetMultipleHNAPsResponse", out var multiResp) &&
            multiResp.TryGetProperty("GetMultipleHNAPsResult", out var result) &&
            string.Equals(result.GetString(), "OK", StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

        response.Dispose();
        return null;
    }

    private async Task<JsonDocument?> PostHnapAsync(
        HttpClient client,
        string endpoint,
        string action,
        string privateKey,
        object payload,
        CancellationToken cancellationToken,
        string refererPath = "/Login.html")
    {
        var hnapAuth = MakeHnapAuth(action, privateKey);
        var soapAction = $"\"{SoapActionPrefix}{action}\"";

        using var content = CreateHnapContent(action, payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = content;
        request.Headers.TryAddWithoutValidation("HNAP_AUTH", hnapAuth);
        request.Headers.TryAddWithoutValidation("SOAPAction", soapAction);
        request.Headers.Referrer = new Uri(endpoint[..^HnapPath.Length] + refererPath);
        if (string.Equals(action, "Login", StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("ARRIS HNAP POST {Action} returned {Status}", action, response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(body);
        }
        catch (HttpRequestException ex)
        {
            // Some S34 firmware emits malformed HTTP response headers (e.g. a stray
            // "  2.0  |Content-type" line) that HttpClient's strict parser rejects with an
            // "invalid header name" error. Retry that one case over a raw TLS socket that
            // tolerates the bad framing. Message-matching is framework-dependent but is the
            // only signal HttpClient surfaces for this parse failure.
            if (string.Equals(action, "GetMultipleHNAPs", StringComparison.OrdinalIgnoreCase) &&
                ex.Message.Contains("invalid header name", StringComparison.OrdinalIgnoreCase))
            {
                var fallback = await PostHnapRawAsync(
                    client,
                    endpoint,
                    action,
                    privateKey,
                    payload,
                    refererPath,
                    cancellationToken);
                if (fallback != null)
                    return fallback;
            }

            _logger.LogDebug(ex, "ARRIS HNAP POST {Action} failed", action);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "ARRIS HNAP POST {Action} returned invalid JSON", action);
            return null;
        }
    }

    private async Task<JsonDocument?> PostHnapRawAsync(
        HttpClient client,
        string endpoint,
        string action,
        string privateKey,
        object payload,
        string refererPath,
        CancellationToken cancellationToken)
    {
        var endpointUri = new Uri(endpoint);
        var baseUrl = endpoint[..^HnapPath.Length];
        var json = JsonSerializer.Serialize(payload);
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var soapAction = $"\"{SoapActionPrefix}{action}\"";
        var hnapAuth = MakeHnapAuth(action, privateKey);
        var cookieHeader = "";
        if (_hnapCookieContainers.TryGetValue(client, out var cookies))
            cookieHeader = cookies.GetCookieHeader(new Uri(baseUrl));

        // This raw path manages its own socket, so HttpClient.Timeout no longer applies.
        // Bound the read with a linked token that fires after the same TimeoutSeconds budget.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var rawToken = timeoutCts.Token;

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(endpointUri.Host, endpointUri.Port, rawToken);
            await using var stream = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await stream.AuthenticateAsClientAsync(endpointUri.Host);

            var requestBuilder = new StringBuilder()
                .Append("POST ").Append(endpointUri.PathAndQuery).Append(" HTTP/1.1\r\n")
                .Append("Host: ").Append(endpointUri.Host).Append("\r\n")
                .Append("User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36\r\n")
                .Append("Accept: application/json\r\n")
                .Append("Content-Type: application/json\r\n")
                .Append("SOAPAction: ").Append(soapAction).Append("\r\n")
                .Append("HNAP_AUTH: ").Append(hnapAuth).Append("\r\n")
                .Append("Referer: ").Append(baseUrl).Append(refererPath).Append("\r\n")
                .Append("Content-Length: ").Append(bodyBytes.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("Connection: close\r\n");

            if (!string.IsNullOrWhiteSpace(cookieHeader))
                requestBuilder.Append("Cookie: ").Append(cookieHeader).Append("\r\n");

            requestBuilder.Append("\r\n");
            var headerBytes = Encoding.ASCII.GetBytes(requestBuilder.ToString());
            await stream.WriteAsync(headerBytes, rawToken);
            await stream.WriteAsync(bodyBytes, rawToken);
            await stream.FlushAsync(rawToken);

            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, rawToken);
                if (read == 0)
                    break;
                memory.Write(buffer, 0, read);
            }

            // The request advertises no Accept-Encoding, so the modem returns plain text we
            // can decode directly; we never negotiate gzip/deflate on this raw path.
            var responseText = Encoding.UTF8.GetString(memory.ToArray());
            var jsonStart = responseText.IndexOf('{');
            var jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return null;

            return JsonDocument.Parse(responseText[jsonStart..(jsonEnd + 1)]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own read-timeout fired (not caller cancellation); treat as a failed probe.
            _logger.LogDebug("ARRIS HNAP raw POST {Action} timed out", action);
            return null;
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or JsonException)
        {
            _logger.LogDebug(ex, "ARRIS HNAP raw POST {Action} failed", action);
            return null;
        }
    }

    private static HttpContent CreateHnapContent(string action, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        if (string.Equals(action, "Login", StringComparison.OrdinalIgnoreCase))
            return new StringContent(json, Encoding.UTF8, "application/json");

        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>
    /// Attempt SB8200 fetch via HTTPS with token-based authentication.
    /// Returns HTML on success, null if this model/auth method doesn't work.
    /// </summary>
    private async Task<string?> TrySb8200Async(CmPollContext context, CancellationToken cancellationToken)
    {
        var statusPath = string.IsNullOrWhiteSpace(context.StatusPagePath)
            ? Sb8200StatusPath
            : context.StatusPagePath;

        var port = context.Port > 0 ? context.Port : 443;
        var portSuffix = port == 443 ? "" : $":{port}";
        var baseUrl = $"https://{context.Host}{portSuffix}";
        var statusUrl = $"{baseUrl}{statusPath}";

        using var client = CreateHttpClient(ignoreSslErrors: true);

        // Try with cached token first
        if (_tokenCache.TryGetValue(context.Id, out var cachedToken))
        {
            var html = await FetchWithTokenAsync(client, statusUrl, cachedToken, cancellationToken);
            if (html != null && !IsAuthPage(html))
                return html;

            // Cached token expired
            _tokenCache.TryRemove(context.Id, out _);
            _logger.LogDebug("ARRIS SB8200 cached token expired for {Name}, re-authenticating", context.Name);
        }

        // Authenticate to get a new token
        var token = await AuthenticateAsync(client, statusUrl, context, cancellationToken);
        if (token == null)
            return null;

        _tokenCache[context.Id] = token;

        // Fetch with new token
        var result = await FetchWithTokenAsync(client, statusUrl, token, cancellationToken);
        if (result != null && !IsAuthPage(result))
            return result;

        // Auth failed completely
        _tokenCache.TryRemove(context.Id, out _);
        return null;
    }

    /// <summary>
    /// Attempt SB6183 fetch via plain HTTP (no auth required).
    /// </summary>
    private async Task<string?> TrySb6183Async(CmPollContext context, CancellationToken cancellationToken)
    {
        var statusPath = string.IsNullOrWhiteSpace(context.StatusPagePath)
            ? Sb6183StatusPath
            : context.StatusPagePath;

        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        var url = $"http://{context.Host}{portSuffix}{statusPath}";

        try
        {
            using var client = CreateHttpClient(ignoreSslErrors: false);
            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(html) ? null : html;
        }
        catch (HttpRequestException)
        {
            // Expected when host doesn't support plain HTTP
            return null;
        }
    }

    /// <summary>
    /// SB8200 token auth flow:
    /// 1. Base64 encode credentials
    /// 2. GET statusUrl?login_{base64Creds} with Basic auth header
    /// 3. Response body contains the session token
    /// </summary>
    private async Task<string?> AuthenticateAsync(
        HttpClient client,
        string statusUrl,
        CmPollContext context,
        CancellationToken cancellationToken)
    {
        var username = context.Username ?? "admin";
        var password = context.Password ?? "";
        var base64Creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

        var loginUrl = $"{statusUrl}?login_{base64Creds}";

        using var request = new HttpRequestMessage(HttpMethod.Get, loginUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64Creds);

        try
        {
            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("ARRIS SB8200 auth returned {Status} for {Host}",
                    response.StatusCode, context.Host);
                return null;
            }

            var token = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogDebug("ARRIS SB8200 auth returned empty token for {Host}", context.Host);
                return null;
            }

            return token.Trim();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "ARRIS SB8200 auth request failed for {Host}", context.Host);
            return null;
        }
    }

    /// <summary>
    /// Fetch the status page using an authenticated session token.
    /// GET statusUrl?ct_{token}
    /// </summary>
    private async Task<string?> FetchWithTokenAsync(
        HttpClient client,
        string statusUrl,
        string token,
        CancellationToken cancellationToken)
    {
        var url = $"{statusUrl}?ct_{token}";

        try
        {
            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Logout after polling to free the session slot on the modem.
    /// </summary>
    private async Task LogoutAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        var port = context.Port > 0 ? context.Port : 443;
        var portSuffix = port == 443 ? "" : $":{port}";
        var logoutUrl = $"https://{context.Host}{portSuffix}/logout.html";

        try
        {
            using var client = CreateHttpClient(ignoreSslErrors: true);
            await client.GetAsync(logoutUrl, cancellationToken);
        }
        catch
        {
            // Logout is best-effort
        }
    }

    /// <summary>
    /// Detect if the response is an auth/login page rather than the status data.
    /// SB8200 shows "Password:" text when auth is required.
    /// </summary>
    private static bool IsAuthPage(string html)
    {
        return html.Contains("Password:", StringComparison.OrdinalIgnoreCase);
    }

    internal static CableModemStats ParseS34Hnap(JsonDocument? deviceResponse, JsonDocument channelResponse, CmPollContext context)
    {
        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = "ARRIS S34",
        };

        if (deviceResponse != null &&
            deviceResponse.RootElement.TryGetProperty("GetMultipleHNAPsResponse", out var deviceMulti) &&
            deviceMulti.TryGetProperty("GetArrisDeviceStatusResponse", out var deviceStatus) &&
            deviceStatus.TryGetProperty("StatusSoftwareModelName", out var modelEl))
        {
            var model = modelEl.GetString();
            if (!string.IsNullOrWhiteSpace(model))
                stats.DeviceModel = model.StartsWith("ARRIS", StringComparison.OrdinalIgnoreCase) ? model : $"ARRIS {model}";
        }

        if (!channelResponse.RootElement.TryGetProperty("GetMultipleHNAPsResponse", out var multiResp))
            return stats;

        if (multiResp.TryGetProperty("GetCustomerStatusDownstreamChannelInfoResponse", out var dsResp) &&
            dsResp.TryGetProperty("CustomerConnDownstreamChannel", out var dsData))
        {
            ParseS34DownstreamRows(dsData.GetString(), stats);
        }

        if (multiResp.TryGetProperty("GetCustomerStatusUpstreamChannelInfoResponse", out var usResp) &&
            usResp.TryGetProperty("CustomerConnUpstreamChannel", out var usData))
        {
            ParseS34UpstreamRows(usData.GetString(), stats);
        }

        return stats;
    }

    internal static void ParseS34DownstreamRows(string? tableStr, CableModemStats stats)
    {
        if (string.IsNullOrWhiteSpace(tableStr))
            return;

        foreach (var row in tableStr.Split(RowDelimiter))
        {
            var cols = row.Split(ColumnDelimiter);
            if (cols.Length < 9) continue;

            var channel = new DsChannel
            {
                ChannelId = ParseInt(cols[3]),
                LockStatus = cols[1].Trim(),
                Modulation = cols[2].Trim(),
                Frequency = ParseFrequency(cols[4]),
                Power = ParseDouble(cols[5]),
                Snr = ParseDouble(cols[6]),
                Correctables = ParseLong(cols[7]),
                Uncorrectables = ParseLong(cols[8]),
            };

            if (channel.ChannelId > 0 || !string.IsNullOrWhiteSpace(channel.LockStatus))
                stats.DownstreamChannels.Add(channel);
        }
    }

    internal static void ParseS34UpstreamRows(string? tableStr, CableModemStats stats)
    {
        if (string.IsNullOrWhiteSpace(tableStr))
            return;

        foreach (var row in tableStr.Split(RowDelimiter))
        {
            var cols = row.Split(ColumnDelimiter);
            if (cols.Length < 7) continue;

            var channel = new UsChannel
            {
                ChannelId = ParseInt(cols[3]),
                LockStatus = cols[1].Trim(),
                ChannelType = cols[2].Trim(),
                SymbolRate = ParseLong(cols[4]),
                Frequency = ParseFrequency(cols[5]),
                Power = ParseDouble(cols[6]) ?? 0,
            };

            if (channel.ChannelId > 0 || !string.IsNullOrWhiteSpace(channel.LockStatus))
                stats.UpstreamChannels.Add(channel);
        }
    }

    /// <summary>
    /// Parse SB8200 HTML. DS table is the second table, US table is the third.
    /// The HTML often contains malformed extra &lt;/tr&gt; tags after "Bonded Channels" text.
    /// </summary>
    private CableModemStats ParseSb8200(string html, CmPollContext context)
    {
        // Fix malformed HTML: strip extra </tr> after "Bonded Channels"
        html = FixMalformedHtml(html);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = "ARRIS SB8200",
        };

        var tables = doc.DocumentNode.SelectNodes("//table");
        if (tables == null || tables.Count < 3)
        {
            _logger.LogDebug("ARRIS SB8200 {Name}: expected 3+ tables, found {Count}",
                context.Name, tables?.Count ?? 0);
            return stats;
        }

        // SB8200: tables[1] = downstream, tables[2] = upstream
        ParseDownstreamTable(tables[1], stats);
        ParseUpstreamTable(tables[2], stats);

        return stats;
    }

    /// <summary>
    /// Parse SB6183 HTML. DS table is the third table, US table is the fourth.
    /// </summary>
    private CableModemStats ParseSb6183(string html, CmPollContext context)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = "ARRIS SB6183",
        };

        var tables = doc.DocumentNode.SelectNodes("//table");
        if (tables == null || tables.Count < 4)
        {
            _logger.LogDebug("ARRIS SB6183 {Name}: expected 4+ tables, found {Count}",
                context.Name, tables?.Count ?? 0);
            return stats;
        }

        // SB6183: tables[2] = downstream, tables[3] = upstream
        ParseDownstreamTable(tables[2], stats);
        ParseUpstreamTable(tables[3], stats);

        return stats;
    }

    private void ParseDownstreamTable(HtmlNode table, CableModemStats stats)
    {
        var rows = table.SelectNodes(".//tr[position()>1]");
        if (rows == null) return;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("td");
            if (cells == null || cells.Count < 8) continue;

            // Skip header-like rows
            var firstCell = cells[0].InnerText.Trim();
            if (!int.TryParse(firstCell, out _) &&
                !firstCell.Contains("Channel", StringComparison.OrdinalIgnoreCase))
                continue;

            var channel = new DsChannel
            {
                ChannelId = ParseInt(cells[0].InnerText),
                LockStatus = cells[1].InnerText.Trim(),
                Modulation = cells[2].InnerText.Trim(),
                Frequency = ParseFrequency(cells[3].InnerText),
                Power = ParseDouble(cells[4].InnerText),
                Snr = ParseDouble(cells[5].InnerText),
                Correctables = ParseLong(cells[6].InnerText),
                Uncorrectables = ParseLong(cells[7].InnerText),
            };

            if (channel.ChannelId > 0 || !string.IsNullOrWhiteSpace(channel.LockStatus))
                stats.DownstreamChannels.Add(channel);
        }
    }

    private void ParseUpstreamTable(HtmlNode table, CableModemStats stats)
    {
        var rows = table.SelectNodes(".//tr[position()>1]");
        if (rows == null) return;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("td");
            if (cells == null || cells.Count < 5) continue;

            // Skip header-like rows
            var firstCell = cells[0].InnerText.Trim();
            if (!int.TryParse(firstCell, out _) &&
                !firstCell.Contains("Channel", StringComparison.OrdinalIgnoreCase))
                continue;

            var channel = new UsChannel
            {
                ChannelId = ParseInt(cells[0].InnerText),
                LockStatus = cells[1].InnerText.Trim(),
                ChannelType = cells[2].InnerText.Trim(),
                Frequency = ParseFrequency(cells[3].InnerText),
                Power = ParseDouble(cells[4].InnerText) ?? 0,
            };

            if (channel.ChannelId > 0 || !string.IsNullOrWhiteSpace(channel.LockStatus))
                stats.UpstreamChannels.Add(channel);
        }
    }

    /// <summary>
    /// Fix malformed HTML from SB8200: remove extra &lt;/tr&gt; tags after "Bonded Channels" text.
    /// </summary>
    private static string FixMalformedHtml(string html)
    {
        // The SB8200 sometimes emits an extra </tr> between the header row and data rows
        // right after "Bonded Channels" text, which breaks table parsing.
        return html.Replace("Bonded Channels</td></tr></tr>", "Bonded Channels</td></tr>")
                   .Replace("Bonded Channels</td>\n</tr>\n</tr>", "Bonded Channels</td>\n</tr>");
    }

    private HttpClient CreateHttpClient(bool ignoreSslErrors)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        if (ignoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };
    }

    private HttpClient CreateHnapHttpClient()
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseCookies = true,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            DefaultRequestHeaders =
            {
                { "Accept", "application/json" },
                { "Cache-Control", "no-cache" },
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" },
            },
        };

        _hnapCookieContainers.Add(client, cookies);
        return client;
    }

    private static int ParseInt(string text)
    {
        var cleaned = StripUnits(text);
        return int.TryParse(cleaned, out var val) ? val : 0;
    }

    private static long ParseLong(string text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    private static double? ParseDouble(string text)
    {
        var cleaned = StripUnits(text);
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    private static long ParseFrequency(string text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    /// <summary>
    /// Remove common unit suffixes from cable modem values.
    /// </summary>
    private static string StripUnits(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var cleaned = text.Trim();

        string[] units = { "Ksym/sec", "Msym/sec", "dBmV", "dB", "MHz", "Hz" };
        foreach (var unit in units)
        {
            var idx = cleaned.IndexOf(unit, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                cleaned = cleaned[..idx];
                break;
            }
        }

        return cleaned.Trim();
    }

    private static string BuildHttpsBaseUrl(CmPollContext context)
    {
        var port = context.Port is > 0 and not 80 ? context.Port : 443;
        var portSuffix = port == 443 ? "" : $":{port}";
        return $"https://{context.Host}{portSuffix}";
    }

    private static string MakeHnapAuth(string action, string privateKey)
    {
        var timestamp = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 2000000000000).ToString(CultureInfo.InvariantCulture);
        var soapActionUri = $"\"{SoapActionPrefix}{action}\"";
        var hash = HmacSha256Hex(privateKey, timestamp + soapActionUri);
        return $"{hash} {timestamp}";
    }

    private static string HmacSha256Hex(string key, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
    }

    private void ApplyHnapSession(HttpClient client, string baseUrl, HnapSession session)
    {
        client.DefaultRequestHeaders.Remove("Cookie");

        if (!_hnapCookieContainers.TryGetValue(client, out var cookies))
            return;

        var uri = new Uri(baseUrl);
        if (!string.IsNullOrWhiteSpace(session.Uid))
            cookies.Add(uri, new Cookie("uid", session.Uid, "/"));
        cookies.Add(uri, new Cookie("PrivateKey", session.PrivateKey, "/"));
    }

    public void Dispose()
    {
        _tokenCache.Clear();
        _hnapSessions.Clear();
    }

    private sealed record HnapSession(string PrivateKey, string? Uid);
}
