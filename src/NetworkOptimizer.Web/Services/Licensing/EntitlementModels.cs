using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services.Licensing;

/// <summary>
/// Signed entitlement envelope returned by the license server's
/// POST /api/v1/license-checks endpoint. The payload is an opaque base64url
/// string signed as raw bytes, so there is no JSON canonicalization ambiguity:
/// verify the bytes first, then deserialize them into
/// <see cref="EntitlementPayload"/>.
///
/// This contract is duplicated in the NetworkOptimizer.LicenseServer repo; the
/// two copies must stay in sync (shared test vectors in both suites guard drift).
/// </summary>
public sealed record EntitlementEnvelope
{
    /// <summary>Base64url-encoded UTF-8 JSON of <see cref="EntitlementPayload"/>.</summary>
    [JsonPropertyName("payload")]
    public string Payload { get; init; } = string.Empty;

    /// <summary>Base64url-encoded ECDSA P-256 (SHA-256) signature over the payload bytes.</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; init; } = string.Empty;

    /// <summary>Identifier of the server signing key used, for key rotation.</summary>
    [JsonPropertyName("kid")]
    public string KeyId { get; init; } = string.Empty;
}

/// <summary>
/// The verified content of an entitlement: what the license server asserts
/// about one license key at one point in time.
/// </summary>
public sealed record EntitlementPayload
{
    /// <summary>The license's id on the license server (stable across checks).</summary>
    [JsonPropertyName("keyId")]
    public int KeyId { get; init; }

    /// <summary>Organization / name the license is tied to, for display only.</summary>
    [JsonPropertyName("org")]
    public string? Org { get; init; }

    /// <summary>License model: <see cref="EntitlementValues.ModelPerpetual"/> or <see cref="EntitlementValues.ModelTerm"/>.</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    /// <summary>Number of managed sites this key covers.</summary>
    [JsonPropertyName("siteAllowance")]
    public int SiteAllowance { get; init; }

    /// <summary>License status: <see cref="EntitlementValues.StatusActive"/> or <see cref="EntitlementValues.StatusRevoked"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>When the license was issued on the server.</summary>
    [JsonPropertyName("issuedAt")]
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>Paid-through date for term licenses; null for perpetual.</summary>
    [JsonPropertyName("paidThrough")]
    public DateTimeOffset? PaidThrough { get; init; }

    /// <summary>
    /// True once a perpetual license has passed its post-activation fraud
    /// window check; the client then trusts the key locally forever and stops
    /// phoning home for it.
    /// </summary>
    [JsonPropertyName("perpetualConfirmed")]
    public bool PerpetualConfirmed { get; init; }

    /// <summary>Server clock at signing time, for client-side skew awareness.</summary>
    [JsonPropertyName("serverTime")]
    public DateTimeOffset ServerTime { get; init; }

    /// <summary>The requesting installation's id, echoed back to bind the envelope.</summary>
    [JsonPropertyName("installationId")]
    public Guid InstallationId { get; init; }
}

/// <summary>String constants used in <see cref="EntitlementPayload"/> fields.</summary>
public static class EntitlementValues
{
    /// <summary>Perpetual license model.</summary>
    public const string ModelPerpetual = "perpetual";

    /// <summary>Term (e.g. monthly) license model with a paid-through date.</summary>
    public const string ModelTerm = "term";

    /// <summary>License is active.</summary>
    public const string StatusActive = "active";

    /// <summary>License was revoked (perpetual: transaction fraud only).</summary>
    public const string StatusRevoked = "revoked";
}
