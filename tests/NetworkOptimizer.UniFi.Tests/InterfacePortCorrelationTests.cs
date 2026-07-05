using FluentAssertions;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class InterfacePortCorrelationTests
{
    /// <summary>Switch-style port table: entries carry no Linux ifname.</summary>
    private static List<SwitchPort> SwitchPortTable() => new()
    {
        new() { PortIdx = 1, Name = "Port 1", Speed = 1000 },
        new() { PortIdx = 2, Name = "Port 2", Speed = 2500 },
        new() { PortIdx = 3, Name = "Uplink", Speed = 10000, SfpFound = true },
    };

    /// <summary>Gateway-style port table: every entry declares its Linux ifname.</summary>
    private static List<SwitchPort> GatewayPortTable() => new()
    {
        new() { PortIdx = 1, Name = "Port 1", IfName = "eth0", Speed = 1000 },
        new() { PortIdx = 2, Name = "Port 2", IfName = "eth1", Speed = 2500 },
        new() { PortIdx = 6, Name = "Main Switch", IfName = "eth5", Speed = 10000, SfpFound = true },
    };

    #region Switch matching (ifIndex == port_idx)

    [Fact]
    public void Switch_MatchesByIfIndex()
    {
        var result = InterfacePortCorrelation.Correlate(
            SwitchPortTable(), ifIndex: 2, snmpSpeedBps: 2_500_000_000, "Port 2", "Port 2");

        result.PortNumber.Should().Be(2);
        result.FriendlyName.Should().Be("Port 2");
        result.LinkSpeedMbps.Should().Be(2500);
    }

    [Fact]
    public void Switch_UnknownIfIndex_NoMatch()
    {
        var result = InterfacePortCorrelation.Correlate(
            SwitchPortTable(), ifIndex: 0, snmpSpeedBps: 0, "0/1", "Port 1");

        result.PortNumber.Should().BeNull();
    }

    #endregion

    #region Gateway matching (ifname join; numeric collisions must not match)

    [Fact]
    public void Gateway_MatchesByIfName_NotByCoincidentalIfIndex()
    {
        // eth1 on a gateway has SNMP ifIndex 14; port_idx 2 is the right answer via ifname.
        var result = InterfacePortCorrelation.Correlate(
            GatewayPortTable(), ifIndex: 14, snmpSpeedBps: 10_000_000_000, "eth1", "eth1");

        result.PortNumber.Should().Be(2);
        result.FriendlyName.Should().Be("Port 2");
        result.LinkSpeedMbps.Should().Be(2500, "port_table's negotiated speed is lower than SNMP's inflated ceiling");
    }

    [Fact]
    public void Gateway_VirtualInterface_DoesNotClaimPortByIfIndexCollision()
    {
        // A gateway's dummy0 sits at ifIndex 2. The port_idx-2 entry belongs to eth1
        // (it declares ifname), so dummy0 must not claim it numerically.
        var result = InterfacePortCorrelation.Correlate(
            GatewayPortTable(), ifIndex: 2, snmpSpeedBps: 0, "dummy0", "dummy0");

        result.PortNumber.Should().BeNull();
        result.FriendlyName.Should().BeNull();
        result.IsSfp.Should().BeNull();
    }

    [Fact]
    public void Gateway_AgentPath_NoIfIndex_StillMatchesByIfName()
    {
        // Older agents stream ifIndex 0; the ifname join alone must resolve the port.
        var result = InterfacePortCorrelation.Correlate(
            GatewayPortTable(), ifIndex: 0, snmpSpeedBps: 0, "eth5", "eth5");

        result.PortNumber.Should().Be(6);
        result.FriendlyName.Should().Be("Main Switch");
        result.IsSfp.Should().BeTrue();
    }

    #endregion

    #region PortNumberBelongsToOtherInterface (stale false-claim healing)

    [Fact]
    public void StaleClaim_VirtualInterfaceHoldingRealPortsNumber_IsDetected()
    {
        InterfacePortCorrelation.PortNumberBelongsToOtherInterface(
            GatewayPortTable(), "dummy0", 2).Should().BeTrue();
    }

    [Fact]
    public void StaleClaim_OwnPort_IsNotDetected()
    {
        InterfacePortCorrelation.PortNumberBelongsToOtherInterface(
            GatewayPortTable(), "eth1", 2).Should().BeFalse();
    }

    [Fact]
    public void StaleClaim_SwitchStyleEntryWithoutIfName_IsNeverDetected()
    {
        // Switch entries carry no ifname, so a correct switch correlation is never cleared.
        InterfacePortCorrelation.PortNumberBelongsToOtherInterface(
            SwitchPortTable(), "Port 2", 2).Should().BeFalse();
    }

    [Fact]
    public void StaleClaim_NullPortTable_IsNeverDetected()
    {
        InterfacePortCorrelation.PortNumberBelongsToOtherInterface(
            null, "dummy0", 2).Should().BeFalse();
    }

    [Fact]
    public void StaleClaim_AliasKeyedRow_OwnRawIfName_IsNotDetected()
    {
        // A row keyed by an SNMP alias whose claim came from the raw-ifname join:
        // passing the raw name marks the owning entry as "self", so the heal never
        // clears a legitimate alias-keyed row.
        InterfacePortCorrelation.PortNumberBelongsToOtherInterface(
            GatewayPortTable(), "Fiber Uplink", 2, rawIfName: "eth1").Should().BeFalse();
    }

    [Fact]
    public void StaleClaim_AliasKeyedRow_DifferentRawIfName_IsDetected()
    {
        InterfacePortCorrelation.PortNumberBelongsToOtherInterface(
            GatewayPortTable(), "Some Alias", 2, rawIfName: "dummy0").Should().BeTrue();
    }

    #endregion
}
