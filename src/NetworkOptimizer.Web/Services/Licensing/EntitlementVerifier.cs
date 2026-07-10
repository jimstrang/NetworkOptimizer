using System.Security.Cryptography;
using System.Text.Json;

namespace NetworkOptimizer.Web.Services.Licensing;

/// <summary>
/// Verifies license server entitlement envelopes against the embedded set of
/// trusted signing public keys. Verification failure of any kind returns null;
/// callers must treat that as "keep the cached entitlement and retry later" -
/// only a successfully verified payload may ever change license state.
/// </summary>
public static class EntitlementVerifier
{
    /// <summary>
    /// Trusted license server signing keys, kid to SubjectPublicKeyInfo PEM.
    /// New server signing keys ship in new releases by appending here (never
    /// remove entries while any issued entitlement may still reference them).
    /// The production key is displayed on the license server's Server Info page.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> TrustedKeys = new Dictionary<string, string>
    {
        // Populated with the production license server key during initial deployment.
    };

    /// <summary>
    /// Verifies an envelope against the embedded trusted keys and returns the
    /// deserialized payload, or null when the kid is unknown, the signature is
    /// invalid, or the payload cannot be parsed.
    /// </summary>
    public static EntitlementPayload? Verify(EntitlementEnvelope? envelope)
    {
        if (envelope == null || !TrustedKeys.TryGetValue(envelope.KeyId, out var pem))
            return null;
        return Verify(envelope, pem);
    }

    /// <summary>
    /// Verifies an envelope against an explicit SubjectPublicKeyInfo PEM.
    /// Exposed for tests and tooling; production callers use the embedded keys.
    /// </summary>
    public static EntitlementPayload? Verify(EntitlementEnvelope? envelope, string publicKeyPem)
    {
        if (envelope == null || string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Signature))
            return null;

        try
        {
            var payloadBytes = DecodeBase64Url(envelope.Payload);
            var signatureBytes = DecodeBase64Url(envelope.Signature);

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            if (!ecdsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256))
                return null;

            return JsonSerializer.Deserialize<EntitlementPayload>(payloadBytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Decodes an unpadded base64url string.</summary>
    private static byte[] DecodeBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - s.Length % 4) % 4;
        return Convert.FromBase64String(s + new string('=', padding));
    }
}
