using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class ArrisSurfboardProviderTests
{
    [Fact]
    public void ParseS34DownstreamRows_ParsesLiveS34RowFormat()
    {
        var stats = new CableModemStats();
        var rows = "1^Locked^256QAM^37^189000000^ 2.9^40.9^0^0^|+|" +
                   "34^Locked^OFDM PLC^159^300000000^ 2.2^43.0^1148144748^12^";

        ArrisSurfboardProvider.ParseS34DownstreamRows(rows, stats);

        stats.DownstreamChannels.Should().HaveCount(2);
        stats.DownstreamChannels[0].Should().BeEquivalentTo(new DsChannel
        {
            ChannelId = 37,
            LockStatus = "Locked",
            Modulation = "256QAM",
            Frequency = 189000000,
            Power = 2.9,
            Snr = 40.9,
            Correctables = 0,
            Uncorrectables = 0,
        });
        stats.DownstreamChannels[1].Modulation.Should().Be("OFDM PLC");
        stats.DownstreamChannels[1].Correctables.Should().Be(1148144748);
        stats.DownstreamChannels[1].Uncorrectables.Should().Be(12);
    }

    [Fact]
    public void ParseS34UpstreamRows_ParsesLiveS34RowFormat()
    {
        var stats = new CableModemStats();
        var rows = "1^Locked^SC-QAM^2^6400000^24000000^34.5^|+|" +
                   "5^Not Locked^Unknown^0^0^0^-inf^|+|" +
                   "9^Locked^OFDMA^6^44000000^36800000^30.2^";

        ArrisSurfboardProvider.ParseS34UpstreamRows(rows, stats);

        stats.UpstreamChannels.Should().HaveCount(3);
        stats.UpstreamChannels[0].Should().BeEquivalentTo(new UsChannel
        {
            ChannelId = 2,
            LockStatus = "Locked",
            ChannelType = "SC-QAM",
            SymbolRate = 6400000,
            Frequency = 24000000,
            Power = 34.5,
        });
        stats.UpstreamChannels[1].Power.Should().Be(0);
        stats.UpstreamChannels[2].ChannelType.Should().Be("OFDMA");
        stats.UpstreamChannels[2].SymbolRate.Should().Be(44000000);
    }

    [Fact]
    public void ParseS34Hnap_SetsArrisModelAndChannels()
    {
        using var deviceResponse = JsonDocument.Parse("""
        {
          "GetMultipleHNAPsResponse": {
            "GetArrisDeviceStatusResponse": {
              "StatusSoftwareModelName": "S34",
              "GetArrisDeviceStatusResult": "OK"
            },
            "GetMultipleHNAPsResult": "OK"
          }
        }
        """);
        using var channelResponse = JsonDocument.Parse("""
        {
          "GetMultipleHNAPsResponse": {
            "GetCustomerStatusDownstreamChannelInfoResponse": {
              "CustomerConnDownstreamChannel": "1^Locked^256QAM^37^189000000^ 2.9^40.9^0^0^",
              "GetCustomerStatusDownstreamChannelInfoResult": "OK"
            },
            "GetCustomerStatusUpstreamChannelInfoResponse": {
              "CustomerConnUpstreamChannel": "1^Locked^SC-QAM^2^6400000^24000000^34.5^",
              "GetCustomerStatusUpstreamChannelInfoResult": "OK"
            },
            "GetMultipleHNAPsResult": "OK"
          }
        }
        """);
        var context = new CmPollContext
        {
            Id = 1,
            Name = "S34",
            Host = "192.168.100.1",
        };

        var stats = ArrisSurfboardProvider.ParseS34Hnap(deviceResponse, channelResponse, context);

        stats.DeviceModel.Should().Be("ARRIS S34");
        stats.DeviceHost.Should().Be("192.168.100.1");
        stats.DownstreamChannels.Should().ContainSingle();
        stats.UpstreamChannels.Should().ContainSingle();
    }
}