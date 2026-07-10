using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for system settings and admin settings
/// </summary>
public interface ISettingsRepository
{
    // System Settings
    Task<string?> GetSystemSettingAsync(string key, CancellationToken cancellationToken = default);
    Task SaveSystemSettingAsync(string key, string? value, CancellationToken cancellationToken = default);

    // Admin Settings
    Task<AdminSettings?> GetAdminSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveAdminSettingsAsync(AdminSettings settings, CancellationToken cancellationToken = default);
}
