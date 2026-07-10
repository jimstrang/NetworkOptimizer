using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class LicenseKeyUtilitiesTests
{
    [Fact]
    public void GenerateKey_ProducesCanonicalForm()
    {
        var key = LicenseKeyUtilities.GenerateKey();

        key.Should().MatchRegex("^NO(-[0-9ABCDEFGHJKMNPQRSTVWXYZ]{5}){6}$");
    }

    [Fact]
    public void GenerateKey_RoundTripsThroughNormalization()
    {
        var key = LicenseKeyUtilities.GenerateKey();

        LicenseKeyUtilities.TryNormalize(key, out var normalized).Should().BeTrue();
        normalized.Should().Be(key);
    }

    [Fact]
    public void GenerateKey_ProducesUniqueKeys()
    {
        var keys = Enumerable.Range(0, 1000).Select(_ => LicenseKeyUtilities.GenerateKey()).ToHashSet();

        keys.Should().HaveCount(1000);
    }

    [Fact]
    public void TryNormalize_AcceptsLowercaseAndMissingDashes()
    {
        var key = LicenseKeyUtilities.GenerateKey();
        var mangled = key.Replace("-", "").ToLowerInvariant();

        LicenseKeyUtilities.TryNormalize(mangled, out var normalized).Should().BeTrue();
        normalized.Should().Be(key);
    }

    [Fact]
    public void TryNormalize_AcceptsMissingPrefix()
    {
        var key = LicenseKeyUtilities.GenerateKey();
        var withoutPrefix = key["NO-".Length..];

        LicenseKeyUtilities.TryNormalize(withoutPrefix, out var normalized).Should().BeTrue();
        normalized.Should().Be(key);
    }

    [Fact]
    public void TryNormalize_MapsAmbiguousCharacters()
    {
        var key = LicenseKeyUtilities.GenerateKey();

        // 1 -> I/l and 0 -> O/o must normalize back to the same key.
        var ambiguous = key.Replace('1', 'I').Replace('0', 'o');
        // Only meaningful when the key actually contains a 1 or 0; the swap of
        // the prefix's O is exercised on every input regardless.
        LicenseKeyUtilities.TryNormalize(ambiguous, out var normalized).Should().BeTrue();
        normalized.Should().Be(key);
    }

    [Fact]
    public void TryNormalize_RejectsCorruptedChecksum()
    {
        var key = LicenseKeyUtilities.GenerateKey();
        var lastChar = key[^1];
        var replacement = lastChar == 'A' ? 'B' : 'A';
        var corrupted = key[..^1] + replacement;

        LicenseKeyUtilities.TryNormalize(corrupted, out _).Should().BeFalse();
    }

    [Fact]
    public void TryNormalize_RejectsSingleCharacterTypoInData()
    {
        var key = LicenseKeyUtilities.GenerateKey();
        var index = "NO-".Length; // first data character
        var replacement = key[index] == '7' ? '9' : '7';
        var typo = key[..index] + replacement + key[(index + 1)..];

        LicenseKeyUtilities.TryNormalize(typo, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NO-SHORT")]
    [InlineData("not a key at all")]
    [InlineData("NO-4Q7WM-8XKCP-2N9RH-T5VZE-A3BDF")] // one group short
    [InlineData("NO-UUUUU-UUUUU-UUUUU-UUUUU-UUUUU-UUUUU")] // U excluded from alphabet
    public void TryNormalize_RejectsStructurallyInvalidInput(string? input)
    {
        LicenseKeyUtilities.TryNormalize(input, out _).Should().BeFalse();
    }

    [Fact]
    public void IsValid_MatchesTryNormalize()
    {
        var key = LicenseKeyUtilities.GenerateKey();

        LicenseKeyUtilities.IsValid(key).Should().BeTrue();
        LicenseKeyUtilities.IsValid("garbage").Should().BeFalse();
    }

    [Fact]
    public void MaskKey_KeepsFirstAndLastGroups()
    {
        var key = LicenseKeyUtilities.GenerateKey();
        var parts = key.Split('-');

        var masked = LicenseKeyUtilities.MaskKey(key);

        masked.Should().Be($"NO-{parts[1]}-*****-*****-*****-*****-{parts[6]}");
    }

    [Fact]
    public void MaskKey_ReturnsNonCanonicalInputUnchanged()
    {
        LicenseKeyUtilities.MaskKey("whatever").Should().Be("whatever");
    }
}
