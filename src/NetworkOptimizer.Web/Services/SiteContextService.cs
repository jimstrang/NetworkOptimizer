using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Scoped ambient site context. The current site is carried in a browser cookie
/// so it is readable synchronously during prerender, in API endpoints, and when
/// the scoped DbContext options are built - no site parameter threading anywhere.
/// Scopes without an HTTP context (background jobs, startup) resolve to the
/// default site, preserving single-site behavior exactly.
/// </summary>
public class SiteContextService : IAlertSiteScope
{
    /// <summary>Cookie carrying the selected site slug for this browser.</summary>
    public const string CookieName = "no-site";

    /// <summary>
    /// Query-string parameter that selects a site for a single request (used by alert
    /// "View" links so a notification lands on its originating site). A middleware
    /// persists it to <see cref="CookieName"/> so the whole session follows the link.
    /// </summary>
    public const string SiteQueryParam = "site";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly SiteDatabasePaths _dbPaths;
    private string? _slug;

    public SiteContextService(IHttpContextAccessor httpContextAccessor, SiteDatabasePaths dbPaths)
    {
        _httpContextAccessor = httpContextAccessor;
        _dbPaths = dbPaths;
    }

    /// <summary>Slug of the site this scope operates on. Resolved once per scope.</summary>
    public string Slug => _slug ??= Resolve();

    /// <summary>
    /// Pins this scope to an explicit site, for scopes created outside an HTTP
    /// request (per-site background fan-out, site-bound singletons). Must be
    /// called before any scoped service resolves the DbContext.
    /// </summary>
    public void OverrideSite(string slug) => _slug = slug;

    /// <summary>
    /// <see cref="IAlertSiteScope"/>: pin this scope to the site an alert originated
    /// from (null/empty = default site) before the alert repository resolves its DbContext.
    /// </summary>
    public void UseSite(string? siteSlug) =>
        OverrideSite(string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug);

    /// <summary>True when this scope operates on the default site (the main database).</summary>
    public bool IsDefault => Slug == SiteManagementService.DefaultSiteSlug;

    /// <summary>SQLite database path for the current site.</summary>
    public string DbPath => _dbPaths.GetSiteDbPath(Slug, IsDefault);

    /// <summary>
    /// True when the slug is selectable for this instance: the default site, or a
    /// non-default site whose database is provisioned. Validates against the slug
    /// alphabet and the on-disk database so a stale or tampered value can never route
    /// to an arbitrary path. Shared by cookie/query resolution and the selection
    /// middleware.
    /// </summary>
    public bool IsSelectableSite(string? slug) =>
        !string.IsNullOrEmpty(slug) &&
        (slug == SiteManagementService.DefaultSiteSlug ||
         (StringUtilities.IsSlug(slug) && File.Exists(_dbPaths.GetSiteDbPath(slug, isDefault: false))));

    private string Resolve()
    {
        var request = _httpContextAccessor.HttpContext?.Request;

        // A ?site= query param (an alert "View" link) wins over the cookie so the linked
        // page renders the correct site on first paint, before the selection middleware's
        // cookie takes effect on subsequent requests. An invalid value falls through.
        var queryParam = request?.Query[SiteQueryParam].ToString();
        if (!string.IsNullOrEmpty(queryParam) && IsSelectableSite(queryParam))
            return queryParam;

        var cookie = request?.Cookies[CookieName];
        if (string.IsNullOrEmpty(cookie) || cookie == SiteManagementService.DefaultSiteSlug)
            return SiteManagementService.DefaultSiteSlug;

        if (!IsSelectableSite(cookie))
            return SiteManagementService.DefaultSiteSlug;

        return cookie;
    }
}
