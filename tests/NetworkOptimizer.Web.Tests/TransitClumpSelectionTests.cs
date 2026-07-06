using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class AssignClumpIndexesTests
{
    [Fact]
    public void Contiguous_hops_share_one_clump()
    {
        UpstreamTracerService.AssignClumpIndexes(new[] { 4, 5, 6 })
            .Should().Equal(0, 0, 0);
    }

    [Fact]
    public void Gap_greater_than_one_starts_new_clump()
    {
        UpstreamTracerService.AssignClumpIndexes(new[] { 4, 5, 9, 10 })
            .Should().Equal(0, 0, 1, 1);
    }

    [Fact]
    public void Ecmp_siblings_at_same_hop_share_clump()
    {
        UpstreamTracerService.AssignClumpIndexes(new[] { 3, 3, 4, 8 })
            .Should().Equal(0, 0, 0, 1);
    }

    [Fact]
    public void Single_hop_is_clump_zero()
    {
        UpstreamTracerService.AssignClumpIndexes(new[] { 7 })
            .Should().Equal(0);
    }

    [Fact]
    public void Empty_input_yields_empty()
    {
        UpstreamTracerService.AssignClumpIndexes(System.Array.Empty<int>())
            .Should().BeEmpty();
    }

    [Fact]
    public void Every_hop_isolated_gets_own_clump()
    {
        UpstreamTracerService.AssignClumpIndexes(new[] { 2, 5, 9 })
            .Should().Equal(0, 1, 2);
    }
}

public class ApplyTransitClumpSelectionTests
{
    private static TransitAsnCandidate Candidate(
        int asn, int clump, double? rtt,
        bool unreachable = false, bool enabled = false, bool preserved = false,
        DiscoveryMethod method = DiscoveryMethod.DirectRouter) => new()
    {
        AsnNumber = asn,
        AsnName = $"AS{asn}",
        Method = method,
        HopAddress = $"192.0.2.{clump * 10 + (int)(rtt ?? 99)}",
        ClumpIndex = clump,
        VerifiedRttMs = rtt,
        Unreachable = unreachable,
        Enabled = enabled,
        PreservedFromExisting = preserved,
    };

    [Fact]
    public void Selects_lowest_rtt_reachable_hop_in_clump()
    {
        var a = Candidate(100, 0, 20, enabled: true);
        var b = Candidate(100, 0, 10);
        var c = Candidate(100, 0, 30);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { a, b, c });

        a.Enabled.Should().BeFalse();
        b.Enabled.Should().BeTrue();
        c.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Selects_one_winner_per_clump()
    {
        var near1 = Candidate(100, 0, 12, enabled: true);
        var near2 = Candidate(100, 0, 8);
        var far1 = Candidate(100, 1, 45, enabled: true);
        var far2 = Candidate(100, 1, 40);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { near1, near2, far1, far2 });

        near1.Enabled.Should().BeFalse();
        near2.Enabled.Should().BeTrue();
        far1.Enabled.Should().BeFalse();
        far2.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Unreachable_hops_never_win()
    {
        var dead = Candidate(100, 0, null, unreachable: true, enabled: false);
        var alive = Candidate(100, 0, 20);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { dead, alive });

        dead.Enabled.Should().BeFalse();
        alive.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Clump_with_no_reachable_hop_selects_nothing()
    {
        var a = Candidate(100, 0, null, unreachable: true, enabled: true);
        var b = Candidate(100, 0, null, unreachable: true);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { a, b });

        a.Enabled.Should().BeFalse();
        b.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Preserved_asn_is_left_untouched()
    {
        // Rediscovery matched one hop to an existing target the user had disabled;
        // the whole ASN keeps its reconciled state instead of being re-selected.
        var kept = Candidate(100, 0, 25, enabled: false, preserved: true);
        var other = Candidate(100, 0, 10, enabled: true);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { kept, other });

        kept.Enabled.Should().BeFalse();
        other.Enabled.Should().BeTrue("preserved ASNs keep their build-time state");
    }

    [Fact]
    public void Path_proxy_candidates_are_ignored()
    {
        var proxy = Candidate(100, 0, null, enabled: true, method: DiscoveryMethod.PathProxy);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { proxy });

        proxy.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Asns_are_selected_independently()
    {
        var asn1Winner = Candidate(100, 0, 15);
        var asn1Loser = Candidate(100, 0, 22, enabled: true);
        var asn2Winner = Candidate(200, 0, 50);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { asn1Winner, asn1Loser, asn2Winner });

        asn1Winner.Enabled.Should().BeTrue();
        asn1Loser.Enabled.Should().BeFalse();
        asn2Winner.Enabled.Should().BeTrue();
    }
}
