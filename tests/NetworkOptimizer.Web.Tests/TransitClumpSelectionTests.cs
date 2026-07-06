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
    public void Rtt_step_splits_hops_into_two_clusters()
    {
        // Real-trace regression: same-city ingress hops at 11.3/12.4 ms, then the
        // long-haul to a distant POP at 17.1 ms - only RTT reveals the far cluster.
        var near1 = Candidate(7029, 5, 11.3, enabled: true);
        var near2 = Candidate(7029, 6, 12.4);
        var far = Candidate(7029, 7, 17.1);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { near1, near2, far });

        near1.Enabled.Should().BeTrue("lowest RTT of the near cluster");
        near2.Enabled.Should().BeFalse("same cluster as the 11.3 ms hop (step 1.1 ms)");
        far.Enabled.Should().BeTrue("the 4.7 ms step starts the far cluster");
    }

    [Fact]
    public void Interleaved_trace_hop_numbers_dont_corrupt_clusters()
    {
        // Real-trace regression: the merged pool interleaves hops from different
        // traces, so hop-number order alternates near/far (11.3, 17.0, 11.3,
        // 17.5, 17.5). Hop-ordered walking put a near hop in the far cluster and
        // selected two near hops; RTT-sorted clustering must not.
        var nearA = Candidate(7029, 5, 11.3, enabled: true);
        var farA = Candidate(7029, 5, 17.0);
        var nearB = Candidate(7029, 6, 11.3);
        var farB = Candidate(7029, 6, 17.5);
        var farC = Candidate(7029, 7, 17.5);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { nearA, farA, nearB, farB, farC });

        nearA.Enabled.Should().BeTrue("lowest RTT, lowest hop of the near cluster");
        nearB.Enabled.Should().BeFalse("one pick per cluster");
        farA.Enabled.Should().BeTrue("lowest RTT of the far cluster");
        farB.Enabled.Should().BeFalse();
        farC.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Hop_from_a_different_path_with_lower_hop_number_still_forms_far_cluster()
    {
        // Real-trace regression: the far hop sits on a different trace whose hop
        // number sorts BEFORE the near hop (16.9 at hop 7 vs 13.5 at hop 8).
        // Hop-ordered walking merged them (negative RTT step); RTT-sorted
        // clustering selects both.
        var far = Candidate(22773, 7, 16.9);
        var near = Candidate(22773, 8, 13.5, enabled: true);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { far, near });

        near.Enabled.Should().BeTrue();
        far.Enabled.Should().BeTrue("step 3.4 ms above the near cluster");
    }

    [Fact]
    public void Small_step_stays_one_cluster_with_one_winner()
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
    public void At_most_two_clusters_are_selected()
    {
        var first = Candidate(100, 5, 10);
        var second = Candidate(100, 6, 20);
        var third = Candidate(100, 7, 40);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { first, second, third });

        first.Enabled.Should().BeTrue();
        second.Enabled.Should().BeTrue();
        third.Enabled.Should().BeFalse("third cluster exceeds MaxClumpsPerAsn");
    }

    [Fact]
    public void Unreachable_hops_never_win()
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
    public void Preserved_enabled_row_covers_its_cluster()
    {
        // The user already monitors a hop in the near cluster: no second near pick
        // is added, but the net-new far cluster still gets its winner.
        var monitored = Candidate(22773, 8, 13.5, enabled: true, preserved: true);
        var nearAlternate = Candidate(22773, 9, 14.0);
        var far = Candidate(22773, 7, 16.9);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { monitored, nearAlternate, far });

        monitored.Enabled.Should().BeTrue("existing rows are never flipped");
        nearAlternate.Enabled.Should().BeFalse("cluster already covered by an existing target");
        far.Enabled.Should().BeTrue("net-new far cluster still gets selected");
    }

    [Fact]
    public void Preserved_disabled_row_is_never_reenabled_but_doesnt_block_netnew()
    {
        // A disabled row can be a flaky-target verdict: it must stay off even as
        // the cluster's lowest RTT, and a net-new hop in the same cluster wins.
        var flaky = Candidate(100, 5, 10, enabled: false, preserved: true);
        var fresh = Candidate(100, 6, 11);

        UpstreamTracerService.ApplyTransitClumpSelection(new[] { flaky, fresh });

        flaky.Enabled.Should().BeFalse("flaky-target verdicts are permanent until the user says otherwise");
        fresh.Enabled.Should().BeTrue();
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
