using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class AuditServiceDeviceClassificationOverrideTests
{
    private readonly Mock<ISettingsRepository> _settingsRepository = new();
    private readonly AuditService _service;

    public AuditServiceDeviceClassificationOverrideTests()
    {
        _service = new AuditService(
            Mock.Of<ILogger<AuditService>>(),
            null!,
            null!,
            null!,
            _settingsRepository.Object,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
    }

    [Fact]
    public async Task GetDeviceClassificationOverridesAsync_StoredValues_NormalizesAndValidatesEntries()
    {
        _settingsRepository
            .Setup(repository => repository.GetSystemSettingAsync(
                "audit:deviceClassificationOverrides",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
                {
                  "aa:bb:cc:dd:ee:ff": "server",
                  "not-a-mac": "Printer",
                  "11:22:33:44:55:66": "NotADeviceCategory"
                }
                """);

        var result = await _service.GetDeviceClassificationOverridesAsync();

        result.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("AABBCCDDEEFF", "Server"));
    }

    [Fact]
    public async Task GetDeviceClassificationOverridesAsync_MalformedJson_ReturnsEmptyDictionary()
    {
        _settingsRepository
            .Setup(repository => repository.GetSystemSettingAsync(
                "audit:deviceClassificationOverrides",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("{ invalid json");

        var result = await _service.GetDeviceClassificationOverridesAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveDeviceClassificationOverrideAsync_LowercaseCategory_PersistsCanonicalValue()
    {
        string? savedJson = null;
        SetupStoredOverrides("{}");
        _settingsRepository
            .Setup(repository => repository.SaveSystemSettingAsync(
                "audit:deviceClassificationOverrides",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string?, CancellationToken>((_, value, _) => savedJson = value)
            .Returns(Task.CompletedTask);

        await _service.SaveDeviceClassificationOverrideAsync("aa-bb-cc-dd-ee-ff", "server");

        var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(savedJson!);
        saved.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("AABBCCDDEEFF", "Server"));
    }

    [Fact]
    public async Task SaveDeviceClassificationOverrideAsync_NullCategory_RemovesNormalizedEntry()
    {
        SetupStoredOverrides("""{"aa:bb:cc:dd:ee:ff":"Server"}""");

        await _service.SaveDeviceClassificationOverrideAsync("AA-BB-CC-DD-EE-FF", null);

        _settingsRepository.Verify(repository => repository.SaveSystemSettingAsync(
            "audit:deviceClassificationOverrides",
            null,
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task SaveDeviceClassificationOverrideAsync_InvalidCategory_ThrowsWithoutSaving()
    {
        var act = () => _service.SaveDeviceClassificationOverrideAsync(
            "AA:BB:CC:DD:EE:FF",
            "NotADeviceCategory");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("category");
        _settingsRepository.Verify(repository => repository.SaveSystemSettingAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SaveDeviceClassificationOverrideAsync_InvalidMac_ThrowsWithoutSaving()
    {
        var act = () => _service.SaveDeviceClassificationOverrideAsync("not-a-mac", "Server");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("mac");
        _settingsRepository.Verify(repository => repository.SaveSystemSettingAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void AuditResult_WithExpandedClientData_RoundTripsThroughJson()
    {
        var result = new Services.AuditResult
        {
            WiredClients =
            [
                new WiredClientReference
                {
                    DisplayName = "Raspberry Pi",
                    Mac = "AABBCCDDEEFF",
                    SwitchName = "Office Switch",
                    PortIndex = 7,
                    NetworkName = "Corporate",
                    VlanId = 1,
                    DeviceCategory = "Server",
                    Detection = new DeviceDetectionResult
                    {
                        Category = ClientDeviceCategory.Server,
                        Source = DetectionSource.UserOverride,
                        ConfidenceScore = 100,
                        AutoDetectedCategory = ClientDeviceCategory.IoTGeneric,
                        AutoDetectionSource = DetectionSource.DeviceName,
                        AutoDetectionConfidenceScore = 75
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(result);
        var roundTripped = JsonSerializer.Deserialize<Services.AuditResult>(json);

        roundTripped.Should().NotBeNull();
        var wiredClient = roundTripped!.WiredClients.Should().ContainSingle().Subject;
        wiredClient.DisplayName.Should().Be("Raspberry Pi");
        wiredClient.Detection.Category.Should().Be(ClientDeviceCategory.Server);
        wiredClient.Detection.AutoDetectedCategory.Should().Be(ClientDeviceCategory.IoTGeneric);
        wiredClient.Detection.AutoDetectionSource.Should().Be(DetectionSource.DeviceName);
        wiredClient.Detection.AutoDetectionConfidenceScore.Should().Be(75);
    }

    [Fact]
    public void AuditResult_OlderJsonWithoutExpandedClientData_UsesEmptyCollections()
    {
        var result = JsonSerializer.Deserialize<Services.AuditResult>("""{"Score":90}""");

        result.Should().NotBeNull();
        result!.WiredClients.Should().BeEmpty();
        result.WirelessClients.Should().BeEmpty();
        result.OfflineClients.Should().BeEmpty();
    }

    private void SetupStoredOverrides(string json)
    {
        _settingsRepository
            .Setup(repository => repository.GetSystemSettingAsync(
                "audit:deviceClassificationOverrides",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);
        _settingsRepository
            .Setup(repository => repository.SaveSystemSettingAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }
}