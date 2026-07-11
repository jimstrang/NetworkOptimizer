using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Storage.Services;

/// <summary>
/// Service for encrypting/decrypting sensitive credentials at rest
/// Uses AES-256 encryption with a machine-specific key derived from DPAPI
/// </summary>
public class CredentialProtectionService : ICredentialProtectionService
{
    private readonly byte[] _key;
    private readonly ILogger<CredentialProtectionService>? _logger;
    private const string KeyPurpose = "NetworkOptimizer.Credentials.v1";

    public CredentialProtectionService(ILogger<CredentialProtectionService>? logger = null)
    {
        _logger = logger;
        // Derive a machine-specific key using DPAPI (Windows) or a file-based key (Linux)
        // This also generates the key file if it doesn't exist
        _key = DeriveKey();
    }

    /// <summary>
    /// Ensures the credential key file exists. Call at startup to pre-generate.
    /// The key is already created in the constructor, so this is a no-op but
    /// provides a clear intent when called at application startup via DI.
    /// </summary>
    public void EnsureKeyExists()
    {
        // Key is already generated in constructor via DeriveKey()
        // This method exists to provide explicit startup initialization via DI
    }

    /// <summary>
    /// Encrypt a plaintext credential
    /// </summary>
    public string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        // Prepend IV to ciphertext and encode as base64
        var result = new byte[aes.IV.Length + ciphertext.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(ciphertext, 0, result, aes.IV.Length, ciphertext.Length);

        return "ENC:" + Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypt an encrypted credential
    /// </summary>
    public string Decrypt(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
            return encrypted;

        // Check if it's encrypted (starts with ENC:)
        if (!encrypted.StartsWith("ENC:"))
            return encrypted; // Return as-is if not encrypted (migration support)

        try
        {
            var data = Convert.FromBase64String(encrypted.Substring(4));

            using var aes = Aes.Create();
            aes.Key = _key;

            // Extract IV from the beginning
            var iv = new byte[aes.BlockSize / 8];
            var ciphertext = new byte[data.Length - iv.Length];
            Buffer.BlockCopy(data, 0, iv, 0, iv.Length);
            Buffer.BlockCopy(data, iv.Length, ciphertext, 0, ciphertext.Length);

            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            var plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex)
        {
            // If decryption fails, return empty (don't expose partial data)
            _logger?.LogError(ex, "Decryption failed");
            return "";
        }
    }

    /// <summary>
    /// Check if a value is already encrypted
    /// </summary>
    public bool IsEncrypted(string? value)
    {
        return value?.StartsWith("ENC:") == true;
    }

    private byte[] DeriveKey()
    {
        // Use a combination of machine-specific data and a salt
        var keyMaterial = GetKeyMaterial();

        using var sha256 = SHA256.Create();
        var salt = Encoding.UTF8.GetBytes(KeyPurpose);

        // PBKDF2 to derive a 256-bit key
        return Rfc2898DeriveBytes.Pbkdf2(keyMaterial, salt, 100000, HashAlgorithmName.SHA256, 32);
    }

    private byte[] GetKeyMaterial()
    {
        // Operators can supply the key out-of-band via NO_CREDENTIAL_KEY_FILE - e.g.
        // a Docker secret mounted at /run/secrets/... or any path OUTSIDE the data
        // volume - so a leak or backup of the data volume does not also hand over the
        // key. When unset, the key lives beside the database in the data directory,
        // which then must be treated as secret material (see DEPLOYMENT.md).
        // WARNING: pointing an EXISTING install at a new/empty path generates a fresh
        // key and makes previously-stored secrets undecryptable; move the existing
        // .credential_key contents to the new path first.
        var overridePath = Environment.GetEnvironmentVariable("NO_CREDENTIAL_KEY_FILE");
        // In Docker, default to /app/data; otherwise use LocalApplicationData.
        var isDocker = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
        var keyFilePath = !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath.Trim()
            : isDocker
                ? "/app/data/.credential_key"
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NetworkOptimizer",
                    ".credential_key"
                );

        try
        {
            var directory = Path.GetDirectoryName(keyFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(keyFilePath))
            {
                return File.ReadAllBytes(keyFilePath);
            }

            // Generate a new random key and save it
            var key = new byte[64];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(key);
            }

            File.WriteAllBytes(keyFilePath, key);

            // Set restrictive permissions on Linux/macOS (600 = owner read/write only)
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Unable to set Unix file permissions on credential key file");
                }
            }

            return key;
        }
        catch (Exception ex)
        {
            // Fallback: use machine name + some entropy
            _logger?.LogWarning(ex, "Failed to create/read credential key file, using fallback key derivation");
            var fallback = Environment.MachineName + KeyPurpose + Environment.UserName;
            return Encoding.UTF8.GetBytes(fallback.PadRight(64, 'X'));
        }
    }
}
