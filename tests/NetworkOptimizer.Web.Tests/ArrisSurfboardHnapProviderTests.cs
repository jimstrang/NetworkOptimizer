using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class ArrisSurfboardHnapProviderTests
{
    [Fact]
    public void ParseDownstreamRows_ParsesLiveHnapRowFormat()
    {
        var stats = new CableModemStats();
        var rows = "1^Locked^256QAM^37^189000000^ 2.9^40.9^0^0^|+|" +
                   "34^Locked^OFDM PLC^159^300000000^ 2.2^43.0^1148144748^12^";

        ArrisSurfboardHnapProvider.ParseDownstreamRows(rows, stats);

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
    public void ParseUpstreamRows_ParsesLiveHnapRowFormat()
    {
        var stats = new CableModemStats();
        var rows = "1^Locked^SC-QAM^2^6400000^24000000^34.5^|+|" +
                   "5^Not Locked^Unknown^0^0^0^-inf^|+|" +
                   "9^Locked^OFDMA^6^44000000^36800000^30.2^";

        ArrisSurfboardHnapProvider.ParseUpstreamRows(rows, stats);

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
    public void ParseHnap_SetsArrisModelAndChannels()
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

        var stats = ArrisSurfboardHnapProvider.ParseHnap(deviceResponse, channelResponse, context);

        stats.DeviceModel.Should().Be("ARRIS S34");
        stats.DeviceHost.Should().Be("192.168.100.1");
        stats.DownstreamChannels.Should().ContainSingle();
        stats.UpstreamChannels.Should().ContainSingle();
    }

    [Fact]
    public void ParseHnap_UsesModelFromChannelResponseWhenDeviceProbeFails()
    {
        using var channelResponse = JsonDocument.Parse("""
        {
          "GetMultipleHNAPsResponse": {
            "GetArrisDeviceStatusResponse": {
              "StatusSoftwareModelName": "S33",
              "GetArrisDeviceStatusResult": "OK"
            },
            "GetCustomerStatusDownstreamChannelInfoResponse": {
              "CustomerConnDownstreamChannel": "0^Not Locked^Unknown^0^0^0.0^0.0^0^0^",
              "GetCustomerStatusDownstreamChannelInfoResult": "OK"
            },
            "GetCustomerStatusUpstreamChannelInfoResponse": {
              "CustomerConnUpstreamChannel": "0^Not Locked^Unknown^0^0^0^0.0^",
              "GetCustomerStatusUpstreamChannelInfoResult": "OK"
            },
            "GetMultipleHNAPsResult": "OK"
          }
        }
        """);
        var context = new CmPollContext
        {
            Id = 1,
            Name = "S33",
            Host = "192.168.100.1",
        };

        var stats = ArrisSurfboardHnapProvider.ParseHnap(null, channelResponse, context);

        stats.DeviceModel.Should().Be("ARRIS S33");
        stats.DownstreamChannels.Should().ContainSingle();
        stats.UpstreamChannels.Should().ContainSingle();
    }

    [Fact]
    public void ParseRawHnapResponse_IgnoresMalformedS34HeaderLine()
    {
        var rawResponse = "HTTP/1.1 200 OK\r\n" +
            "   2.099998  |Content-type: text/html\r\n" +
            "Server: lighttpd\r\n" +
            "\r\n" +
            "{\"GetMultipleHNAPsResponse\":{\"GetMultipleHNAPsResult\":\"OK\"}}";

        using var document = ArrisSurfboardHnapProvider.ParseRawHnapResponse(rawResponse);

        document.Should().NotBeNull();
        document!.RootElement
            .GetProperty("GetMultipleHNAPsResponse")
            .GetProperty("GetMultipleHNAPsResult")
            .GetString()
            .Should().Be("OK");
    }

    [Fact]
    public void HmacMd5Hex_ReturnsUppercaseDigestForS33Hnap()
    {
        var digest = ArrisSurfboardHnapProvider.HmacMd5Hex("key", "The quick brown fox jumps over the lazy dog");

        digest.Should().Be("80070713463E7749B90C2DC24911E275");
    }
}