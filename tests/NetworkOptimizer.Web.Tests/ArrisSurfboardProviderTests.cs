using FluentAssertions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class ArrisSurfboardProviderTests
{
    [Fact]
    public void ParseSb6190_ParsesAuthenticatedStatusTables()
    {
        var html = """
        <html><body>
        <table></table>
        <table>
          <tr><th>Startup Procedure</th></tr>
          <tr><td>Acquire Downstream Channel</td><td>In Progress</td></tr>
        </table>
        <table>
          <tr><th colspan="9">Downstream Bonded Channels</th></tr>
          <tr>
            <td><strong>Channel</strong></td><td><strong>Lock Status</strong></td><td><strong>Modulation</strong></td>
            <td><strong>Channel ID</strong></td><td><strong>Frequency</strong></td><td><strong>Power</strong></td>
            <td><strong>SNR</strong></td><td><strong>Corrected</strong></td><td><strong>Uncorrectables</strong></td>
          </tr>
          <tr><td>1</td><td>Locked</td><td>256QAM</td><td>37</td><td>615.00 MHz</td><td>2.3 dBmV</td><td>40.1 dB</td><td>12</td><td>3</td></tr>
        </table>
        <table>
          <tr><th colspan="7">Upstream Bonded Channels</th></tr>
          <tr>
            <td><strong>Channel</strong></td><td><strong>Lock Status</strong></td><td><strong>US Channel Type</strong></td>
            <td><strong>Channel ID</strong></td><td><strong>Symbol Rate</strong></td><td><strong>Frequency</strong></td><td><strong>Power</strong></td>
          </tr>
          <tr><td>1</td><td>Locked</td><td>ATDMA</td><td>2</td><td>5120 kSym/s</td><td>24.00 MHz</td><td>35.0 dBmV</td></tr>
        </table>
        </body></html>
        """;
        var context = new CmPollContext
        {
            Id = 1,
            Name = "SB6190",
            Host = "192.168.100.1",
        };

        var stats = ArrisSurfboardProvider.ParseSb6190(html, context);

        stats.DeviceModel.Should().Be("ARRIS SB6190");
        stats.DownstreamChannels.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ChannelId = 37,
            LockStatus = "Locked",
            Modulation = "256QAM",
            Frequency = 615000000,
            Power = 2.3,
            Snr = 40.1,
            Correctables = 12L,
            Uncorrectables = 3L,
        });
        stats.UpstreamChannels.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ChannelId = 2,
            LockStatus = "Locked",
            ChannelType = "ATDMA",
            SymbolRate = 5120L,
            Frequency = 24000000,
            Power = 35.0,
        });
    }
}
