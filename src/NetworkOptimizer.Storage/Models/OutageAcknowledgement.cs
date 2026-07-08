using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A WAN outage the user marked "that was me" on ISP Health (their own maintenance, e.g.
/// pulling the coax to add a pad), keyed by the outage's onset time. Acknowledged outages are
/// excluded from ISP Health scoring; matching is tolerance-based because a recompute can
/// shift a detected outage's boundaries by a bucket.
/// </summary>
public class OutageAcknowledgement
{
    [Key]
    public int Id { get; set; }

    /// <summary>Onset (UTC) of the acknowledged outage event.</summary>
    public DateTime OutageStartUtc { get; set; }

    /// <summary>When the user acknowledged it.</summary>
    public DateTime AcknowledgedAt { get; set; }
}
