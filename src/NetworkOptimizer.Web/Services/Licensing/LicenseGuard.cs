namespace NetworkOptimizer.Web.Services.Licensing;

/// <summary>
/// Thrown when an operation is attempted against a license-restricted site.
/// The message is user-facing.
/// </summary>
public class LicenseRestrictedException : InvalidOperationException
{
    public LicenseRestrictedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Shared operation gate for license enforcement. Called at the entry of every
/// user-initiated or scheduled operation service method; historic data reads
/// are intentionally never guarded so restricted sites keep their dashboards.
/// </summary>
public static class LicenseGuard
{
    /// <summary>User-facing explanation shown when an operation is blocked.</summary>
    public const string RestrictedMessage =
        "This site is restricted because its license expired. Historic data remains available; " +
        "enter or renew a license key in Settings > Application > Licensing to restore operations.";

    /// <summary>
    /// Throws <see cref="LicenseRestrictedException"/> when the site is
    /// restricted; no-op otherwise (including before the first state compute).
    /// </summary>
    public static void EnsureOperational(LicenseStateService licenseState, string slug)
    {
        if (!licenseState.IsSiteOperational(slug))
            throw new LicenseRestrictedException(RestrictedMessage);
    }
}
