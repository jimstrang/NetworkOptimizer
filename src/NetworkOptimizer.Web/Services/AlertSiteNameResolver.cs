using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Storage.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Resolves site display names from the instance-wide site registry for alert delivery.
/// Registered as a singleton because the alert processor is a hosted singleton; the
/// registry repository is scoped, so it is resolved per lookup through a scope. Names
/// change rarely and alerts can burst, so results are cached briefly.
/// </summary>
public class AlertSiteNameResolver : IAlertSiteNameResolver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, (string? Name, DateTime CachedAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public AlertSiteNameResolver(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<string?> ResolveNameAsync(string? slug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(slug) || slug == SiteManagementService.DefaultSiteSlug)
            return null;

        if (_cache.TryGetValue(slug, out var cached) && (DateTime.UtcNow - cached.CachedAt) < CacheDuration)
            return cached.Name;

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISiteRepository>();
        var site = await repository.GetBySlugAsync(slug, cancellationToken);
        _cache[slug] = (site?.Name, DateTime.UtcNow);
        return site?.Name;
    }
}
