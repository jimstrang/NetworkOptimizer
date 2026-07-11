using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Licensing;

/// <summary>
/// Hourly background loop that runs due license server checks. The cadence per
/// key is computed by <see cref="LicenseActivationService.ComputeNextCheck"/>:
/// pending keys retry hourly, unconfirmed perpetual keys get their one confirm
/// check after the 30-day fraud window (then never phone home again), and term
/// keys check daily inside the 30-day pre-expiry window, continuing through
/// grace so pre-purchased renewals and late renewals recover automatically.
/// Failed checks never downgrade cached entitlements.
/// </summary>
public class LicensePhoneHomeService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly LicenseActivationService _activationService;
    private readonly LicenseStateService _stateService;
    private readonly TimeProvider _time;
    private readonly ILogger<LicensePhoneHomeService> _logger;

    public LicensePhoneHomeService(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        LicenseActivationService activationService,
        LicenseStateService stateService,
        TimeProvider time,
        ILogger<LicensePhoneHomeService> logger)
    {
        _mainDbFactory = mainDbFactory;
        _activationService = activationService;
        _stateService = stateService;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before the first pass.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _time, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TickInterval, _time);
        do
        {
            try
            {
                await RunDueChecksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "License phone-home pass failed");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>Runs checks for every key whose next check is due.</summary>
    public async Task RunDueChecksAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        List<string> dueKeys;
        await using (var db = await _mainDbFactory.CreateDbContextAsync(cancellationToken))
        {
            dueKeys = await db.LicenseKeyRecords
                .AsNoTracking()
                .Where(k => k.NextCheckAt != null && k.NextCheckAt <= now)
                .Select(k => k.LicenseKey)
                .ToListAsync(cancellationToken);
        }

        if (dueKeys.Count == 0)
            return;

        _logger.LogDebug("Running {Count} due license check(s)", dueKeys.Count);
        foreach (var key in dueKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _activationService.RefreshKeyAsync(key);
        }

        await _stateService.RecomputeAsync();
    }

    /// <summary>
    /// Immediately re-checks every key regardless of schedule (the Licensing
    /// card's refresh button). Returns the number of keys checked.
    /// </summary>
    public async Task<int> RefreshAllAsync()
    {
        List<string> keys;
        await using (var db = await _mainDbFactory.CreateDbContextAsync())
        {
            keys = await db.LicenseKeyRecords.AsNoTracking().Select(k => k.LicenseKey).ToListAsync();
        }

        foreach (var key in keys)
            await _activationService.RefreshKeyAsync(key);

        await _stateService.RecomputeAsync();
        return keys.Count;
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
