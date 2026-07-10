namespace NetworkOptimizer.Web.Services.Licensing;

/// <summary>
/// Applies license state transitions to live resources: when a site becomes
/// Restricted its open agent tunnels are force-dropped (new connections are
/// already rejected at tunnel auth, and the agent's own dial-out backoff
/// resumes it automatically once the site is re-licensed). Registered as a
/// hosted service purely to subscribe/unsubscribe around the app lifetime.
/// </summary>
public class LicenseEnforcementCoordinator : IHostedService
{
    private readonly LicenseStateService _licenseState;
    private readonly AgentTunnelRegistry _tunnelRegistry;
    private readonly ILogger<LicenseEnforcementCoordinator> _logger;

    public LicenseEnforcementCoordinator(
        LicenseStateService licenseState,
        AgentTunnelRegistry tunnelRegistry,
        ILogger<LicenseEnforcementCoordinator> logger)
    {
        _licenseState = licenseState;
        _tunnelRegistry = tunnelRegistry;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _licenseState.OnStateChanged += EnforceTunnels;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _licenseState.OnStateChanged -= EnforceTunnels;
        return Task.CompletedTask;
    }

    private void EnforceTunnels()
    {
        try
        {
            foreach (var connection in _tunnelRegistry.GetAll())
            {
                if (_licenseState.IsSiteOperational(connection.SiteSlug))
                    continue;
                _logger.LogInformation(
                    "Dropping agent tunnel {Agent} for license-restricted site {Slug}",
                    connection.AgentName, connection.SiteSlug);
                connection.Drop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "License tunnel enforcement pass failed");
        }
    }
}
