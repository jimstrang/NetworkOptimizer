using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for system settings and license information
/// </summary>
public class SettingsRepository : ISettingsRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<SettingsRepository> _logger;

    public SettingsRepository(NetworkOptimizerDbContext context, ILogger<SettingsRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region System Settings

    /// <summary>
    /// Retrieves a system setting value by key.
    /// </summary>
    /// <param name="key">The setting key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The setting value, or null if not found.</returns>
    public async Task<string?> GetSystemSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var setting = await _context.SystemSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
            return setting?.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get system setting {Key}", key);
            throw;
        }
    }

    /// <summary>
    /// Saves or updates a system setting.
    /// </summary>
    /// <param name="key">The setting key.</param>
    /// <param name="value">The setting value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveSystemSettingAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.SystemSettings
                .FirstOrDefaultAsync(s => s.Key == key, cancellationToken);

            if (existing != null)
            {
                existing.Value = value;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _context.SystemSettings.Add(new SystemSetting
                {
                    Key = key,
                    Value = value,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Saved system setting {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save system setting {Key}", key);
            throw;
        }
    }

    #endregion

    #region Admin Settings

    /// <summary>
    /// Retrieves the admin authentication settings.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The admin settings, or null if not configured.</returns>
    public async Task<AdminSettings?> GetAdminSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AdminSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get admin settings");
            throw;
        }
    }

    /// <summary>
    /// Saves or updates the admin authentication settings.
    /// </summary>
    /// <param name="settings">The admin settings to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveAdminSettingsAsync(AdminSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.AdminSettings.FirstOrDefaultAsync(cancellationToken);

            if (existing != null)
            {
                existing.Password = settings.Password;
                existing.Enabled = settings.Enabled;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                settings.CreatedAt = DateTime.UtcNow;
                settings.UpdatedAt = DateTime.UtcNow;
                _context.AdminSettings.Add(settings);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Saved admin settings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save admin settings");
            throw;
        }
    }

    #endregion
}
