using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class ApplyTransitClumpSelectionTests
{
    private static TransitAsnCandidate Candidate(
        int asn, int hopNumber, double? rtt,
        bool unreachable = false, bool enabled = false, bool preserved = false,
        DiscoveryMethod method = DiscoveryMethod.DirectRouter) => new()
    {
        AsnNumber = asn,
        AsnName = $"AS{asn}",
        Method = method,
        HopAddress = $"192.0.2.{hopNumber}",
        HopNumber = hopNumber,
        VerifiedRttMs = rtt,
        Unreachable = unreachable,
        Enabled = enabled,
        PreservedFromExisting = preserved,
    };

    [Fact]
    public void Rtt_step_splits_consecutive_hops_into_two_clumps()
    {
        // Regression from a real trace: same-city ingress hops at 11.3/12.4 ms, then
        // the long-haul to a distant POP at 17.1 ms - hop numbers are consecutive, so
        // only the RTT step can reveal the second clump.
        var near1 = Candidate(7029, 5, 11.3, enabled: true);
        var near2 = Candidate(7029, 6, 12.4);
        var far = Candidate(7029, 7, 17.1);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { near1, near2, far });

        near1.Enabled.Should().BeTrue("lowest RTT of the near clump");
        near2.Enabled.Should().BeFalse("same clump as the 11.3 ms hop (step 1.1 ms)");
        far.Enabled.Should().BeTrue("the 4.7 ms step starts the far clump");
    }

    [Fact]
    public void Two_hop_run_with_material_step_selects_both()
    {
        // Real-trace regression: 13.7 -> 16.9 ms (step 3.2 > max(2, 15%)) is two POPs.
        var near = Candidate(22773, 8, 13.7, enabled: true);
        var far = Candidate(22773, 9, 16.9);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { near, far });

        near.Enabled.Should().BeTrue();
        far.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Small_step_stays_one_clump_with_one_winner()
    {
        var a = Candidate(100, 5, 11.3);
        var b = Candidate(100, 6, 12.4, enabled: true);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { a, b });

        a.Enabled.Should().BeTrue();
        b.Enabled.Should().BeFalse();
    }

    [Fact]
    public void High_rtt_paths_use_fractional_threshold()
    {
        // 100 -> 104 ms is noise at that distance (4 < 15% of 100), not a new POP.
        var a = Candidate(100, 10, 100);
        var b = Candidate(100, 11, 104);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { a, b });

        a.Enabled.Should().BeTrue();
        b.Enabled.Should().BeFalse();
    }

    [Fact]
    public void At_most_two_clumps_are_selected()
    {
        var first = Candidate(100, 5, 10);
        var second = Candidate(100, 6, 20);
        var third = Candidate(100, 7, 40);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { first, second, third });

        first.Enabled.Should().BeTrue();
        second.Enabled.Should().BeTrue();
        third.Enabled.Should().BeFalse("third clump exceeds MaxClumpsPerAsn");
    }

    [Fact]
    public void Lowest_rtt_in_clump_wins_regardless_of_hop_order()
    {
        var earlier = Candidate(100, 5, 12.4, enabled: true);
        var later = Candidate(100, 6, 11.3);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { earlier, later });

        earlier.Enabled.Should().BeFalse();
        later.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Unreachable_hops_never_win_and_dont_split_clumps()
    {
        var dead = Candidate(100, 5, null, unreachable: true, enabled: true);
        var alive1 = Candidate(100, 6, 20);
        var alive2 = Candidate(100, 7, 21);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { dead, alive1, alive2 });

        dead.Enabled.Should().BeFalse();
        alive1.Enabled.Should().BeTrue();
        alive2.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Asn_with_no_reachable_hop_selects_nothing()
    {
        var a = Candidate(100, 5, null, unreachable: true, enabled: true);
        var b = Candidate(100, 6, null, unreachable: true);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { a, b });

        a.Enabled.Should().BeFalse();
        b.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Preserved_asn_is_left_untouched()
    {
        // Rediscovery matched one hop to an existing target the user had disabled;
        // the whole ASN keeps its reconciled state instead of being re-selected.
        var kept = Candidate(100, 5, 25, enabled: false, preserved: true);
        var other = Candidate(100, 6, 10, enabled: true);

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
        var asn1Winner = Candidate(100, 5, 15);
        var asn1Loser = Candidate(100, 6, 16, enabled: true);
        var asn2Winner = Candidate(200, 8, 50);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { asn1Winner, asn1Loser, asn2Winner });

        asn1Winner.Enabled.Should().BeTrue();
        asn1Loser.Enabled.Should().BeFalse();
        asn2Winner.Enabled.Should().BeTrue();
    }
}
