using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Assigns one managed site to the license key that covers it. Lives only in
/// the main (default site) database alongside the <see cref="Site"/> registry.
/// A site has at most one covering key; a key covers at most its
/// <see cref="LicenseKeyRecord.SiteAllowance"/> sites (enforced in the
/// licensing services, oldest assignments winning if an allowance shrinks).
/// </summary>
public class SiteLicenseAssignment
{
    [Key]
    public int Id { get; set; }

    /// <summary>The covered site (unique: one covering key per site).</summary>
    public int SiteId { get; set; }

    /// <summary>The covering license key.</summary>
    public int LicenseKeyRecordId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
