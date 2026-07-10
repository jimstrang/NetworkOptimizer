using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Licensing;

/// <summary>Outcome categories for a license server check.</summary>
public enum LicenseCheckErrorType
{
    /// <summary>Network failure or timeout; keep cached state and retry.</summary>
    Unreachable,

    /// <summary>Server does not know this key (unsigned response; never downgrades cached state).</summary>
    UnknownKey,

    /// <summary>Key failed server-side format validation.</summary>
    InvalidKeyFormat,

    /// <summary>Response envelope failed signature verification.</summary>
    InvalidSignature,

    /// <summary>Unexpected server response.</summary>
    ServerError,
}

/// <summary>
/// Result of one license check: a verified payload plus the raw envelope JSON
/// for caching, or a typed error.
/// </summary>
public sealed record LicenseCheckResult(
    EntitlementPayload? Payload,
    string? RawEnvelopeJson,
    LicenseCheckErrorType? Error,
    string? ErrorMessage)
{
    /// <summary>True when a signed entitlement was received and verified.</summary>
    public bool Success => Payload != null;

    internal static LicenseCheckResult Ok(EntitlementPayload payload, string rawJson) => new(payload, rawJson, null, null);
    internal static LicenseCheckResult Fail(LicenseCheckErrorType error, string message) => new(null, null, error, message);
}

/// <summary>
/// HTTP client for the license server's public check endpoint. Every response
/// is signature-verified via <see cref="EntitlementVerifier"/> before it is
/// surfaced; callers never see unverified data as success.
/// </summary>
public class LicenseServerClient
{
    /// <summary>Production license server base URL.</summary>
    public const string DefaultServerUrl = "https://licensing.ozarkconnect.net";

    private const string HttpClientName = "LicenseServer";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly ILogger<LicenseServerClient> _logger;

    public LicenseServerClient(
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        ILogger<LicenseServerClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _mainDbFactory = mainDbFactory;
        _logger = logger;
    }

    /// <summary>
    /// The effective license server base URL: the global override setting when
    /// present, otherwise <see cref="DefaultServerUrl"/>.
    /// </summary>
    public async Task<string> GetServerUrlAsync()
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var setting = await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.LicensingServerUrl);
        var url = setting?.Value;
        return string.IsNullOrWhiteSpace(url) ? DefaultServerUrl : url.TrimEnd('/');
    }

    /// <summary>
    /// Checks one license key against the license server and returns the
    /// verified entitlement or a typed error.
    /// </summary>
    public async Task<LicenseCheckResult> CheckAsync(
        string licenseKey,
        Guid installationId,
        string appVersion,
        int siteCount,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = await GetServerUrlAsync();
        var request = new LicenseCheckRequest
        {
            LicenseKey = licenseKey,
            InstallationId = installationId,
            AppVersion = appVersion,
            SiteCount = siteCount,
        };

        HttpResponseMessage response;
        string body;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            response = await client.PostAsync($"{baseUrl}/api/v1/license-checks", content, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "License server unreachable at {BaseUrl}", baseUrl);
            return LicenseCheckResult.Fail(LicenseCheckErrorType.Unreachable, $"License server unreachable: {ex.Message}");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.NotFound:
                    return LicenseCheckResult.Fail(LicenseCheckErrorType.UnknownKey, "License server does not recognize this key");
                case HttpStatusCode.UnprocessableEntity:
                    return LicenseCheckResult.Fail(LicenseCheckErrorType.InvalidKeyFormat, "License server rejected the key format");
                default:
                    return LicenseCheckResult.Fail(LicenseCheckErrorType.ServerError, $"License server returned {(int)response.StatusCode}");
            }
        }

        EntitlementEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EntitlementEnvelope>(body);
        }
        catch (JsonException)
        {
            return LicenseCheckResult.Fail(LicenseCheckErrorType.ServerError, "License server returned an unparseable response");
        }

        var payload = EntitlementVerifier.Verify(envelope);
        if (payload == null)
            return LicenseCheckResult.Fail(LicenseCheckErrorType.InvalidSignature, "Entitlement signature verification failed");

        if (payload.InstallationId != installationId)
            return LicenseCheckResult.Fail(LicenseCheckErrorType.InvalidSignature, "Entitlement was issued for a different installation");

        return LicenseCheckResult.Ok(payload, body);
    }

    /// <summary>
    /// Best-effort release of this installation's binding for a key, so it can be
    /// claimed on another installation. Fire-and-forget by design: the response is
    /// not verified and failures are swallowed (an unreachable server just leaves
    /// the binding in place for the operator to clear). Returns true when the
    /// server acknowledged the release.
    /// </summary>
    public async Task<bool> ReleaseAsync(
        string licenseKey,
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = await GetServerUrlAsync();
        var request = new LicenseReleaseRequest
        {
            LicenseKey = licenseKey,
            InstallationId = installationId,
        };

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"{baseUrl}/api/v1/license-releases", content, cancellationToken);
            if (response.IsSuccessStatusCode)
                return true;

            _logger.LogDebug("License release returned {Status} for {BaseUrl}", (int)response.StatusCode, baseUrl);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "License server unreachable for release at {BaseUrl}", baseUrl);
            return false;
        }
    }
}

/// <summary>Request body for POST /api/v1/license-checks.</summary>
public sealed record LicenseCheckRequest
{
    [JsonPropertyName("licenseKey")]
    public string LicenseKey { get; init; } = string.Empty;

    [JsonPropertyName("installationId")]
    public Guid InstallationId { get; init; }

    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; init; }

    [JsonPropertyName("siteCount")]
    public int SiteCount { get; init; }
}

/// <summary>Request body for POST /api/v1/license-releases.</summary>
public sealed record LicenseReleaseRequest
{
    [JsonPropertyName("licenseKey")]
    public string LicenseKey { get; init; } = string.Empty;

    [JsonPropertyName("installationId")]
    public Guid InstallationId { get; init; }
}
