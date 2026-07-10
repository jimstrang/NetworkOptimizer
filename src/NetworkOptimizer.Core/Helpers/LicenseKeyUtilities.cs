using System.Security.Cryptography;
using System.Text;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Generation, normalization and validation for NetworkOptimizer license keys.
/// Canonical form: "NO-" followed by six dash-separated groups of five
/// Crockford base32 characters - five data groups (125 bits of entropy) and a
/// final checksum group (the first 25 bits of SHA-256 over the 25 data
/// characters), e.g. "NO-4Q7WM-8XKCP-2N9RH-T5VZE-A3BDF-J6GKM".
///
/// This file is duplicated in the NetworkOptimizer.LicenseServer repo; the two
/// copies must stay in sync (shared test vectors in both suites guard drift).
/// </summary>
public static class LicenseKeyUtilities
{
    /// <summary>Key prefix without the trailing dash.</summary>
    public const string Prefix = "NO";

    /// <summary>
    /// Crockford base32 alphabet: digits and uppercase letters excluding
    /// I, L, O (mapped from input) and U (always invalid).
    /// </summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int DataChars = 25;
    private const int ChecksumChars = 5;
    private const int GroupSize = 5;

    /// <summary>
    /// Generates a new license key in canonical form using a cryptographically
    /// secure random source (125 bits of entropy plus checksum group).
    /// </summary>
    public static string GenerateKey()
    {
        var data = new char[DataChars];
        for (var i = 0; i < DataChars; i++)
            data[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        var body = new string(data) + ComputeChecksum(data);
        return Format(body);
    }

    /// <summary>
    /// Normalizes user input to the canonical key form. Accepts any casing,
    /// missing or extra dashes/whitespace, an optional "NO" prefix, and the
    /// Crockford ambiguity mappings I/L to 1 and O to 0. Returns false when the
    /// input is not a structurally valid key or its checksum does not match.
    /// </summary>
    public static bool TryNormalize(string? input, out string canonicalKey)
    {
        canonicalKey = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var sb = new StringBuilder(DataChars + ChecksumChars);
        foreach (var raw in input)
        {
            if (raw is '-' or ' ' or '\t')
                continue;

            var c = char.ToUpperInvariant(raw);
            c = c switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                _ => c,
            };
            if (Alphabet.IndexOf(c) < 0)
                return false;
            sb.Append(c);
        }

        var body = sb.ToString();

        // The normalized prefix "NO" becomes "N0" via the O->0 mapping.
        if (body.Length == DataChars + ChecksumChars + Prefix.Length && body.StartsWith("N0", StringComparison.Ordinal))
            body = body[Prefix.Length..];

        if (body.Length != DataChars + ChecksumChars)
            return false;

        var data = body[..DataChars];
        var checksum = body[DataChars..];
        if (!string.Equals(ComputeChecksum(data.ToCharArray()), checksum, StringComparison.Ordinal))
            return false;

        canonicalKey = Format(body);
        return true;
    }

    /// <summary>True when the input normalizes to a valid key (checksum included).</summary>
    public static bool IsValid(string? input) => TryNormalize(input, out _);

    /// <summary>
    /// Masks a canonical key for display, keeping the first and last groups
    /// visible: "NO-4Q7WM-*****-*****-*****-*****-J6GKM". Returns the input
    /// unchanged when it is not a canonical key.
    /// </summary>
    public static string MaskKey(string canonicalKey)
    {
        var parts = canonicalKey.Split('-');
        if (parts.Length != 7 || parts[0] != Prefix)
            return canonicalKey;

        return $"{Prefix}-{parts[1]}-*****-*****-*****-*****-{parts[6]}";
    }

    /// <summary>
    /// Computes the five-character checksum group: the first 25 bits of
    /// SHA-256 over the ASCII bytes of the 25 normalized data characters,
    /// encoded as five base32 characters.
    /// </summary>
    private static string ComputeChecksum(char[] data)
    {
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(data));
        var bits = ((uint)digest[0] << 24) | ((uint)digest[1] << 16) | ((uint)digest[2] << 8) | digest[3];
        bits >>= 7; // keep the top 25 bits

        var chars = new char[ChecksumChars];
        for (var i = ChecksumChars - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(bits & 0x1F)];
            bits >>= 5;
        }
        return new string(chars);
    }

    /// <summary>Formats a 30-character normalized body as the canonical dashed key.</summary>
    private static string Format(string body)
    {
        var sb = new StringBuilder(Prefix.Length + 1 + body.Length + body.Length / GroupSize);
        sb.Append(Prefix);
        for (var i = 0; i < body.Length; i += GroupSize)
        {
            sb.Append('-');
            sb.Append(body, i, GroupSize);
        }
        return sb.ToString();
    }
}
