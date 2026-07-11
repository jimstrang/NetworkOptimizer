using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Web.Services.Licensing;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Licensing;

public class EntitlementVerifierTests
{
    /// <summary>
    /// Fixed cross-repo contract vector. The identical envelope, public key and
    /// expected field values are checked into the NetworkOptimizer.LicenseServer
    /// test suite; if either repo drifts from the shared contract (field names,
    /// encoding, signature scheme), this test breaks first. The keypair is
    /// test-only and never used in production.
    /// </summary>
    private const string VectorPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE3RDyjA4wbN2o2aMkkSG91iGK5Vit
        oiFodEOyi0FM47CaispLt5wapeP2/3RfOByEQSr2FZxgVsrG0IgmQh8PUg==
        -----END PUBLIC KEY-----
        """;

    private const string VectorKid = "f467cbb6";

    private const string VectorPayload =
        "eyJrZXlJZCI6NDIsIm9yZyI6IkV4YW1wbGUgTmV0d29ya3MgTExDIiwibW9kZWwiOiJ0ZXJtIiwic2l0ZUFsbG93YW5jZSI6NSwic3RhdHVzIjoiYWN0aXZlIiwiaXNzdWVkQXQiOiIyMDI2LTAxLTE1VDAwOjAwOjAwKzAwOjAwIiwicGFpZFRocm91Z2giOiIyMDI2LTEyLTAxVDAwOjAwOjAwKzAwOjAwIiwicGVycGV0dWFsQ29uZmlybWVkIjpmYWxzZSwic2VydmVyVGltZSI6IjIwMjYtMDctMTBUMTI6MDA6MDArMDA6MDAiLCJpbnN0YWxsYXRpb25JZCI6IjNmMmM4YTQxLTlkNjctNGE1ZS04YjFjLTJlN2Y5MGQ0YzZhYSJ9";

    private const string VectorSignature =
        "OvBUl9zSZVHa7GUQk71T4k8GlU9ll90VKjYsy2tQ2gkxuWbi6q011VdSzeG-f0krdK5CI4-WatThXaf9Yu6bjw";

    private static EntitlementEnvelope VectorEnvelope => new()
    {
        Payload = VectorPayload,
        Signature = VectorSignature,
        KeyId = VectorKid,
    };

    [Fact]
    public void Verify_SharedContractVector_YieldsExpectedPayload()
    {
        var payload = EntitlementVerifier.Verify(VectorEnvelope, VectorPublicKeyPem);

        payload.Should().NotBeNull();
        payload!.KeyId.Should().Be(42);
        payload.Org.Should().Be("Example Networks LLC");
        payload.Model.Should().Be(EntitlementValues.ModelTerm);
        payload.SiteAllowance.Should().Be(5);
        payload.Status.Should().Be(EntitlementValues.StatusActive);
        payload.IssuedAt.Should().Be(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
        payload.PaidThrough.Should().Be(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        payload.PerpetualConfirmed.Should().BeFalse();
        payload.ServerTime.Should().Be(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));
        payload.InstallationId.Should().Be(Guid.Parse("3f2c8a41-9d67-4a5e-8b1c-2e7f90d4c6aa"));
    }

    [Fact]
    public void Verify_RoundTrip_WithFreshKeypair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = new EntitlementPayload
        {
            KeyId = 7,
            Org = "Test Org",
            Model = EntitlementValues.ModelPerpetual,
            SiteAllowance = 10,
            Status = EntitlementValues.StatusActive,
            IssuedAt = DateTimeOffset.UtcNow,
            PaidThrough = null,
            PerpetualConfirmed = true,
            ServerTime = DateTimeOffset.UtcNow,
            InstallationId = Guid.NewGuid(),
        };

        var envelope = Sign(ecdsa, payload);
        var verified = EntitlementVerifier.Verify(envelope, ecdsa.ExportSubjectPublicKeyInfoPem());

        verified.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public void Verify_TamperedPayload_ReturnsNull()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Sign(ecdsa, new EntitlementPayload { KeyId = 1, SiteAllowance = 3 });

        // Flip the allowance inside the signed payload.
        var json = Encoding.UTF8.GetString(DecodeBase64Url(envelope.Payload));
        var tamperedJson = json.Replace("\"siteAllowance\":3", "\"siteAllowance\":300");
        var tampered = envelope with { Payload = EncodeBase64Url(Encoding.UTF8.GetBytes(tamperedJson)) };

        EntitlementVerifier.Verify(tampered, ecdsa.ExportSubjectPublicKeyInfoPem()).Should().BeNull();
    }

    [Fact]
    public void Verify_WrongPublicKey_ReturnsNull()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Sign(signer, new EntitlementPayload { KeyId = 1 });

        EntitlementVerifier.Verify(envelope, other.ExportSubjectPublicKeyInfoPem()).Should().BeNull();
    }

    [Theory]
    [InlineData("not base64url!!!", VectorSignature)]
    [InlineData(VectorPayload, "not base64url!!!")]
    [InlineData("", VectorSignature)]
    [InlineData(VectorPayload, "")]
    public void Verify_MalformedEncoding_ReturnsNull(string payload, string signature)
    {
        var envelope = new EntitlementEnvelope { Payload = payload, Signature = signature, KeyId = VectorKid };

        EntitlementVerifier.Verify(envelope, VectorPublicKeyPem).Should().BeNull();
    }

    [Fact]
    public void Verify_NullEnvelope_ReturnsNull()
    {
        EntitlementVerifier.Verify(null, VectorPublicKeyPem).Should().BeNull();
    }

    [Fact]
    public void Verify_EmbeddedKeys_UnknownKid_ReturnsNull()
    {
        // The embedded key set must never accept an unknown kid.
        var envelope = VectorEnvelope with { KeyId = "00000000" };

        EntitlementVerifier.Verify(envelope).Should().BeNull();
    }

    [Fact]
    public void Verify_NonJsonSignedBytes_ReturnsNull()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = Encoding.UTF8.GetBytes("definitely not json");
        var envelope = new EntitlementEnvelope
        {
            Payload = EncodeBase64Url(bytes),
            Signature = EncodeBase64Url(ecdsa.SignData(bytes, HashAlgorithmName.SHA256)),
            KeyId = "test",
        };

        EntitlementVerifier.Verify(envelope, ecdsa.ExportSubjectPublicKeyInfoPem()).Should().BeNull();
    }

    private static EntitlementEnvelope Sign(ECDsa ecdsa, EntitlementPayload payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return new EntitlementEnvelope
        {
            Payload = EncodeBase64Url(payloadBytes),
            Signature = EncodeBase64Url(ecdsa.SignData(payloadBytes, HashAlgorithmName.SHA256)),
            KeyId = "test",
        };
    }

    private static string EncodeBase64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }
}
