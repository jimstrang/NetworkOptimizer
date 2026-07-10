using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A license key entered on this instance, together with the cached signed
/// entitlement last received from the license server. Lives only in the main
/// (default site) database; per-site coverage is expressed through
/// <see cref="SiteLicenseAssignment"/> rows. The raw key is stored because the
/// instance must re-send it on every license server check.
/// </summary>
public class LicenseKeyRecord
{
    [Key]
    public int Id { get; set; }

    /// <summary>Canonical key form, e.g. "NO-4Q7WM-8XKCP-2N9RH-T5VZE-A3BDF-J6GKM".</summary>
    [Required]
    [MaxLength(64)]
    public string LicenseKey { get; set; } = "";

    /// <summary>Organization / name from the verified entitlement, for display.</summary>
    [MaxLength(200)]
    public string? Org { get; set; }

    /// <summary>License model, one of <see cref="LicenseKeyModels"/>. Set from the entitlement.</summary>
    [MaxLength(20)]
    public string Model { get; set; } = "";

    /// <summary>Managed-site allowance this key grants. Set from the entitlement.</summary>
    public int SiteAllowance { get; set; }

    /// <summary>Key lifecycle status, one of <see cref="LicenseKeyStatuses"/>.</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = LicenseKeyStatuses.Pending;

    /// <summary>When the license was issued on the server. Set from the entitlement.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>Paid-through date (UTC) for term keys; null for perpetual.</summary>
    public DateTime? PaidThrough { get; set; }

    /// <summary>
    /// True once a perpetual key passed its post-activation fraud-window check;
    /// the key is then trusted locally forever and never phones home again.
    /// </summary>
    public bool PerpetualConfirmed { get; set; }

    /// <summary>When this instance first successfully activated the key (UTC).</summary>
    public DateTime? ActivatedAt { get; set; }

    /// <summary>Last successful license server check (UTC).</summary>
    public DateTime? LastCheckAt { get; set; }

    /// <summary>Next scheduled license server check (UTC); null when no further checks are needed.</summary>
    public DateTime? NextCheckAt { get; set; }

    /// <summary>Human-readable error from the most recent failed check, for the UI.</summary>
    [MaxLength(500)]
    public string? LastCheckError { get; set; }

    /// <summary>Raw signed entitlement envelope JSON as last received and verified.</summary>
    public string? EntitlementJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>License model values for <see cref="LicenseKeyRecord.Model"/>.</summary>
public static class LicenseKeyModels
{
    public const string Perpetual = "perpetual";
    public const string Term = "term";
}

/// <summary>Status values for <see cref="LicenseKeyRecord.Status"/>.</summary>
public static class LicenseKeyStatuses
{
    /// <summary>Entered but not yet confirmed by the license server.</summary>
    public const string Pending = "pending";

    /// <summary>Confirmed by a verified entitlement.</summary>
    public const string Active = "active";

    /// <summary>Revoked by a verified entitlement (perpetual: transaction fraud only).</summary>
    public const string Revoked = "revoked";
}
