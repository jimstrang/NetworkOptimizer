using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

public class TransitUnreachableDetectorTests
{
    private static readonly IspHealthOptions Options = new();
    private static readonly DateTime Start = TestSeries.Start;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static List<TransitUnreachableDetector.DarkWindow> Detect(List<LatencySample> samples) =>
        TransitUnreachableDetector.Detect("t1", 3356, "Lumen", samples, Options);

    [Fact]
    public void Sustained_total_loss_becomes_a_dark_window()
    {
        // 20 minutes at 100% loss inside an otherwise clean hour (1-min samples).
        var samples = TestSeries.Flat(Start, Hour, 12, 0.5)
            .WithSegment(Start.AddMinutes(10), Start.AddMinutes(30), 12, 0.5, lossPct: 100);

        var windows = Detect(samples);

        windows.Should().ContainSingle();
        windows[0].Start.Should().Be(Start.AddMinutes(10));
        windows[0].End.Should().Be(Start.AddMinutes(29));
        windows[0].AsnNumber.Should().Be(3356);
    }

    [Fact]
    public void Short_total_loss_flap_stays_in_the_loss_pool()
    {
        // Two dark samples span only 60 s - a flap, below TransitUnreachableMinSeconds.
        var samples = TestSeries.Flat(Start, Hour, 12, 0.5)
            .WithSegment(Start.AddMinutes(10), Start.AddMinutes(12), 12, 0.5, lossPct: 100);

        Detect(samples).Should().BeEmpty();
    }

    [Fact]
    public void Lossy_but_reachable_transit_is_not_a_dark_window()
    {
        // A heavy but partial loss floor (40%) for a long stretch: still forwarding, so it
        // must keep feeding the access-layer loss pool.
        var samples = TestSeries.Flat(Start, Hour, 12, 0.5, lossPct: 40);

        Detect(samples).Should().BeEmpty();
    }

    [Fact]
    public void Monitoring_gap_inside_a_dark_run_does_not_split_it()
    {
        // Dark 10:00-10:10, a 4-min sample gap (console restart), dark again 10:14-10:24.
        var samples = TestSeries.Flat(Start, Hour, 12, 0.5)
            .WithSegment(Start.AddMinutes(10), Start.AddMinutes(25), 12, 0.5, lossPct: 100)
            .Where(s => s.Time < Start.AddMinutes(14) || s.Time >= Start.AddMinutes(18))
            .ToList();

        var windows = Detect(samples);

        windows.Should().ContainSingle();
        windows[0].Start.Should().Be(Start.AddMinutes(10));
        windows[0].End.Should().Be(Start.AddMinutes(24));
    }

    [Fact]
    public void Separate_episodes_stay_separate_windows()
    {
        // Two washouts an hour apart: each is its own window (and later its own path event).
        var samples = TestSeries.Flat(Start, TimeSpan.FromHours(3), 12, 0.5)
            .WithSegment(Start.AddMinutes(10), Start.AddMinutes(20), 12, 0.5, lossPct: 100)
            .WithSegment(Start.AddMinutes(90), Start.AddMinutes(105), 12, 0.5, lossPct: 100);

        var windows = Detect(samples);

        windows.Should().HaveCount(2);
        windows[0].Start.Should().Be(Start.AddMinutes(10));
        windows[1].Start.Should().Be(Start.AddMinutes(90));
    }

    [Fact]
    public void Merge_collapses_a_clusters_members_into_one_event()
    {
        var a = new TransitUnreachableDetector.DarkWindow("t1", 3356, "Lumen", Start.AddMinutes(10), Start.AddMinutes(30));
        var b = new TransitUnreachableDetector.DarkWindow("t2", 3356, "Lumen", Start.AddMinutes(12), Start.AddMinutes(33));

        var events = TransitUnreachableDetector.MergeByAsn(new[] { a, b }, Options);

        events.Should().ContainSingle();
        events[0].Start.Should().Be(Start.AddMinutes(10));
        events[0].End.Should().Be(Start.AddMinutes(33));
        events[0].TargetCount.Should().Be(2);
    }

    [Fact]
    public void Merge_keeps_distinct_episodes_and_distinct_asns_apart()
    {
        var early = new TransitUnreachableDetector.DarkWindow("t1", 3356, "Lumen", Start.AddMinutes(10), Start.AddMinutes(20));
        var late = new TransitUnreachableDetector.DarkWindow("t1", 3356, "Lumen", Start.AddMinutes(90), Start.AddMinutes(100));
        var other = new TransitUnreachableDetector.DarkWindow("t9", 1299, "Arelion", Start.AddMinutes(10), Start.AddMinutes(20));

        var events = TransitUnreachableDetector.MergeByAsn(new[] { early, late, other }, Options);

        events.Should().HaveCount(3);
        events.Count(e => e.AsnNumber == 3356).Should().Be(2);
        events.Count(e => e.AsnNumber == 1299).Should().Be(1);
    }
}
