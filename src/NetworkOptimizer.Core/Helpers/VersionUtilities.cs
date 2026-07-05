namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Comparison helpers for the MinVer/SemVer-style version strings the app and
/// its on-site agents report (e.g. "2.0.0-beta.2.14+3449fbae" or "1.4.2").
/// </summary>
public static class VersionUtilities
{
    /// <summary>
    /// True when <paramref name="candidate"/> is a lower version than
    /// <paramref name="required"/> under SemVer precedence: numeric core first,
    /// then prerelease identifiers (a release outranks any prerelease of the
    /// same core; identifiers compare numerically when both numeric, otherwise
    /// ordinally; a longer identifier list outranks its prefix). Build metadata
    /// after '+' is ignored. Returns false when either side is missing or
    /// unparseable - callers flag only provable staleness.
    /// </summary>
    public static bool IsOlderThan(string? candidate, string? required)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(required))
            return false;

        var (candCore, candPre) = Split(candidate);
        var (reqCore, reqPre) = Split(required);
        if (candCore == null || reqCore == null)
            return false;

        var coreCompare = candCore.CompareTo(reqCore);
        if (coreCompare != 0)
            return coreCompare < 0;

        // Same core: a release (no prerelease) outranks any prerelease.
        if (candPre.Length == 0)
            return false;
        if (reqPre.Length == 0)
            return true;

        var count = Math.Min(candPre.Length, reqPre.Length);
        for (var i = 0; i < count; i++)
        {
            int cmp;
            if (long.TryParse(candPre[i], out var candNum) && long.TryParse(reqPre[i], out var reqNum))
                cmp = candNum.CompareTo(reqNum);
            else
                cmp = string.Compare(candPre[i], reqPre[i], StringComparison.OrdinalIgnoreCase);
            if (cmp != 0)
                return cmp < 0;
        }
        return candPre.Length < reqPre.Length;
    }

    private static (Version? Core, string[] Prerelease) Split(string version)
    {
        var v = version.Trim().TrimStart('v', 'V');
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-');
        var core = dash >= 0 ? v[..dash] : v;
        var pre = dash >= 0 ? v[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        return (Version.TryParse(core, out var parsed) ? parsed : null, pre);
    }
}
