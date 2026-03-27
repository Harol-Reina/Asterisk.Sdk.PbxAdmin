using FluentAssertions;
using PbxAdmin.LoadTests.Sdk;

namespace PbxAdmin.Tests.LoadTests.Sdk;

public sealed class LiveStateSummaryTests
{
    private static LiveStateSample BuildSample(int sdkChannels, int asteriskChannels) => new()
    {
        Timestamp = DateTime.UtcNow,
        SdkChannelCount = sdkChannels,
        AsteriskChannelCount = asteriskChannels
    };

    [Fact]
    public void Compute_ShouldReturnPassedWithZeroSamples_WhenEmpty()
    {
        var result = LiveStateSummary.Compute([]);

        result.TotalSamples.Should().Be(0);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public void Compute_ShouldPassWithZeroDrift_WhenAllSamplesMatch()
    {
        var samples = new List<LiveStateSample>
        {
            BuildSample(10, 10),
            BuildSample(5, 5),
            BuildSample(0, 0)
        };

        var result = LiveStateSummary.Compute(samples);

        result.TotalSamples.Should().Be(3);
        result.SamplesWithinTolerance.Should().Be(3);
        result.MaxDrift.Should().Be(0);
        result.AverageDrift.Should().Be(0);
        result.DriftRate.Should().Be(0);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public void Compute_ShouldFail_WhenDriftRateExceedsFivePercent()
    {
        // 4 out of 10 samples have drift > 2 → 40% drift rate
        var samples = new List<LiveStateSample>
        {
            BuildSample(10, 10), // drift 0
            BuildSample(10, 10), // drift 0
            BuildSample(10, 10), // drift 0
            BuildSample(10, 10), // drift 0
            BuildSample(10, 10), // drift 0
            BuildSample(10, 10), // drift 0
            BuildSample(10, 15), // drift 5 → outside tolerance
            BuildSample(10, 17), // drift 7 → outside tolerance
            BuildSample(10, 14), // drift 4 → outside tolerance
            BuildSample(10, 13), // drift 3 → outside tolerance
        };

        var result = LiveStateSummary.Compute(samples);

        result.TotalSamples.Should().Be(10);
        result.SamplesWithinTolerance.Should().Be(6);
        result.DriftRate.Should().Be(40);
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public void Compute_ShouldPass_WhenDriftRateJustBelowThreshold()
    {
        // 1 out of 21 samples has drift > 2 → ~4.76% drift rate (below 5%)
        var samples = new List<LiveStateSample>();
        for (int i = 0; i < 20; i++)
            samples.Add(BuildSample(10, 10)); // 20 within tolerance
        samples.Add(BuildSample(10, 15)); // 1 outside tolerance

        var result = LiveStateSummary.Compute(samples);

        result.TotalSamples.Should().Be(21);
        result.SamplesWithinTolerance.Should().Be(20);
        result.DriftRate.Should().BeApproximately(100.0 / 21.0, 0.01);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public void Compute_ShouldTrackMaxDrift_Correctly()
    {
        var samples = new List<LiveStateSample>
        {
            BuildSample(10, 10), // drift 0
            BuildSample(10, 12), // drift 2 → within tolerance
            BuildSample(10, 18), // drift 8 → max
            BuildSample(10, 11), // drift 1 → within tolerance
        };

        var result = LiveStateSummary.Compute(samples);

        result.MaxDrift.Should().Be(8);
        result.AverageDrift.Should().BeApproximately(2.75, 0.01);
    }

    [Fact]
    public void ChannelDrift_ShouldBeAbsoluteDifference()
    {
        var sample = BuildSample(sdkChannels: 3, asteriskChannels: 7);

        sample.ChannelDrift.Should().Be(4);
    }

    [Fact]
    public void WithinTolerance_ShouldBeTrue_WhenDriftAtMostTwo()
    {
        BuildSample(10, 10).WithinTolerance.Should().BeTrue(); // drift 0
        BuildSample(10, 11).WithinTolerance.Should().BeTrue(); // drift 1
        BuildSample(10, 12).WithinTolerance.Should().BeTrue(); // drift 2
        BuildSample(10, 13).WithinTolerance.Should().BeFalse(); // drift 3
    }
}
