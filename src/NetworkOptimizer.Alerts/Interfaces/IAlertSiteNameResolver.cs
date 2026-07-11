namespace NetworkOptimizer.Alerts.Interfaces;

/// <summary>
/// Resolves a site's human-readable name from its slug for alert delivery. Delivery
/// channels (ntfy, email, Slack, ...) have no site column, so an alert from a managed
/// site is otherwise indistinguishable from a main-site alert. The alert pipeline lives
/// below the web layer and can't read the site registry directly, so it depends on this
/// seam; the web layer implements it over the instance-wide site registry.
/// </summary>
public interface IAlertSiteNameResolver
{
    /// <summary>
    /// Returns the display name for a site slug. A null or empty slug is the default
    /// (main) site and returns null, so callers omit the site label. Returns null if
    /// the slug is unknown, letting callers fall back to the slug itself.
    /// </summary>
    Task<string?> ResolveNameAsync(string? slug, CancellationToken cancellationToken = default);
}
