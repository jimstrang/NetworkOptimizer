using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for ARRIS Surfboard modems (SB8200, SB6190, SB6183, T25).
/// Supports SB8200 token-based auth (HTTPS), SB6190 CGI login, and SB6183 simple page fetch (HTTP).
/// Routes /cgi-bin/status configurations to SB6190 CGI login; otherwise tries SB8200 HTTPS,
/// falling back to SB6183 HTTP.
/// </summary>
public sealed class ArrisSurfboardProvider : ICableModemProvider, IDisposable
{
    /// <inheritdoc/>
    public string ProviderKey => "arris-surfboard";

    /// <inheritdoc/>
    public string DisplayName => "ARRIS Surfboard (HTTP)";

    private const string Sb8200StatusPath = "/cmconnectionstatus.html";
    private const string Sb6190StatusPath = "/cgi-bin/status";
    private const string Sb6190LoginPath = "/cgi-bin/adv_pwd_cgi";
    private const string Sb6183StatusPath = "/RgConnect.asp";
    private const int TimeoutSeconds = 15;

    private readonly ILogger<ArrisSurfboardProvider> _logger;

    /// <summary>
    /// Cached auth tokens keyed by CmConfiguration.Id.
    /// SB8200 requires token-based auth; tokens are cached until they expire.
    /// </summary>
    private readonly ConcurrentDictionary<int, string> _tokenCache = new();

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
            // /cgi-bin/status is the SB6190 CGI flow; avoid probing it with SB8200 token auth first.
            var html = await TrySb6190Async(context, cancellationToken);
            if (html != null)
            {
                var stats = ParseSb6190(html, context);
                _logger.LogDebug(
                    "ARRIS SB6190 {Name} polled: {DsCount} DS channels, {UsCount} US channels",
                    context.Name, stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);
                return stats;
            }

            // Try SB8200 (HTTPS with token auth) first
            html = await TrySb8200Async(context, cancellationToken);
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

            _logger.LogWarning("ARRIS Surfboard {Name} at {Host}: SB6190, SB8200, and SB6183 fetches failed",
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
            var html = await TrySb6190Async(context, cancellationToken);
            if (html != null)
            {
                return (true, "Connected to ARRIS SB6190 (CGI login)");
            }

            // Try SB8200 first
            html = await TrySb8200Async(context, cancellationToken);
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

            return (false, "Could not connect via CGI login (SB6190), HTTPS token auth (SB8200), or HTTP status page (SB6183). Check host and credentials.");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempt SB6190 CGI login and authenticated status fetch.
    /// </summary>
    private async Task<string?> TrySb6190Async(CmPollContext context, CancellationToken cancellationToken)
    {
        if (!IsSb6190CgiStatusPath(context.StatusPagePath))
            return null;

        var port = context.Port > 0 ? context.Port : 80;
        var scheme = port == 443 ? "https" : "http";
        var portSuffix = (scheme == "https" && port == 443) || (scheme == "http" && port == 80) ? "" : $":{port}";
        var baseUrl = $"{scheme}://{context.Host}{portSuffix}";

        using var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseCookies = true,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };

        var nonce = GenerateSb6190Nonce();

        try
        {
            await client.GetAsync(baseUrl + Sb6190LoginPath, cancellationToken);

            var username = string.IsNullOrWhiteSpace(context.Username) ? "admin" : context.Username;
            var form = new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = context.Password ?? "",
                ["ar_nonce"] = nonce,
            };

            using var loginContent = new FormUrlEncodedContent(form);
            using var loginRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + Sb6190LoginPath)
            {
                Content = loginContent,
            };
            loginRequest.Headers.Referrer = new Uri(baseUrl + Sb6190LoginPath);
            loginRequest.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            loginRequest.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");

            using var loginResponse = await client.SendAsync(loginRequest, cancellationToken);
            if (!loginResponse.IsSuccessStatusCode)
                return null;

            var loginResult = (await loginResponse.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (loginResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("ARRIS SB6190 login failed for {Host}: {Result}", context.Host, loginResult);
                return null;
            }

            var statusPath = loginResult.StartsWith("Url:", StringComparison.OrdinalIgnoreCase)
                ? ResolveSb6190Path(loginResult[4..].Trim())
                : Sb6190StatusPath;

            var statusResponse = await client.GetAsync(baseUrl + statusPath, cancellationToken);
            if (!statusResponse.IsSuccessStatusCode)
                return null;

            var html = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
            return IsAuthPage(html) ? null : html;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "ARRIS SB6190 CGI request failed for {Host}", context.Host);
            return null;
        }
        finally
        {
            await LogoutSb6190Async(client, baseUrl, nonce, CancellationToken.None);
        }
    }

    private async Task LogoutSb6190Async(HttpClient client, string baseUrl, string nonce, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(nonce, Encoding.UTF8, "text/plain");
            await client.PostAsync(baseUrl + "/cgi-bin/logout", content, cancellationToken);
        }
        catch
        {
            // Logout is best-effort.
        }
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

    internal static CableModemStats ParseSb6190(string html, CmPollContext context)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = "ARRIS SB6190",
        };

        var tables = doc.DocumentNode.SelectNodes("//table");
        if (tables == null || tables.Count < 4)
            return stats;

        ParseSb6190DownstreamTable(tables[2], stats);
        ParseSb6190UpstreamTable(tables[3], stats);

        return stats;
    }

    private static void ParseSb6190DownstreamTable(HtmlNode table, CableModemStats stats)
    {
        var rows = table.SelectNodes(".//tr[position()>1]");
        if (rows == null) return;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("td");
            if (cells == null || cells.Count < 9) continue;

            var firstCell = cells[0].InnerText.Trim();
            if (!int.TryParse(firstCell, out _))
                continue;

            var channel = new DsChannel
            {
                ChannelId = ParseInt(cells[3].InnerText),
                LockStatus = cells[1].InnerText.Trim(),
                Modulation = cells[2].InnerText.Trim(),
                Frequency = ParseFrequency(cells[4].InnerText),
                Power = ParseDouble(cells[5].InnerText),
                Snr = ParseDouble(cells[6].InnerText),
                Correctables = ParseLong(cells[7].InnerText),
                Uncorrectables = ParseLong(cells[8].InnerText),
            };

            if (channel.ChannelId > 0 || !string.IsNullOrWhiteSpace(channel.LockStatus))
                stats.DownstreamChannels.Add(channel);
        }
    }

    private static void ParseSb6190UpstreamTable(HtmlNode table, CableModemStats stats)
    {
        var rows = table.SelectNodes(".//tr[position()>1]");
        if (rows == null) return;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("td");
            if (cells == null || cells.Count < 7) continue;

            var firstCell = cells[0].InnerText.Trim();
            if (!int.TryParse(firstCell, out _))
                continue;

            var channel = new UsChannel
            {
                ChannelId = ParseInt(cells[3].InnerText),
                LockStatus = cells[1].InnerText.Trim(),
                ChannelType = cells[2].InnerText.Trim(),
                SymbolRate = ParseLong(cells[4].InnerText),
                Frequency = ParseFrequency(cells[5].InnerText),
                Power = ParseDouble(cells[6].InnerText) ?? 0,
            };

            if (channel.ChannelId > 0 || !string.IsNullOrWhiteSpace(channel.LockStatus))
                stats.UpstreamChannels.Add(channel);
        }
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
        return double.TryParse(cleaned, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    private static long ParseFrequency(string text)
    {
        if (text.Contains("MHz", StringComparison.OrdinalIgnoreCase))
        {
            var mhz = StripUnits(text);
            return double.TryParse(mhz, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var mhzValue)
                ? (long)(mhzValue * 1_000_000)
                : 0;
        }

        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    private static bool IsSb6190CgiStatusPath(string? statusPath)
        => string.Equals(statusPath, Sb6190StatusPath, StringComparison.OrdinalIgnoreCase);

    private static string ResolveSb6190Path(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Sb6190StatusPath;

        if (path.StartsWith("/", StringComparison.Ordinal))
            return path;

        return "/cgi-bin/" + path;
    }

    private static string GenerateSb6190Nonce()
    {
        var value = RandomNumberGenerator.GetInt32(0, 100_000_000);
        return value.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Remove common unit suffixes from cable modem values.
    /// </summary>
    private static string StripUnits(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var cleaned = text.Trim();

        string[] units = { "Ksym/sec", "kSym/s", "Msym/sec", "dBmV", "dB", "MHz", "Hz" };
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

    public void Dispose()
    {
        _tokenCache.Clear();
    }
}
