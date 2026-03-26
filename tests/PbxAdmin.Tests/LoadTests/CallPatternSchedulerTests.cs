using FluentAssertions;
using PbxAdmin.LoadTests.CallGeneration;
using PbxAdmin.LoadTests.Configuration;

namespace PbxAdmin.Tests.LoadTests;

/// <summary>
/// Unit tests for the pure static logic methods in <see cref="CallPatternScheduler"/>.
/// Async lifecycle (StartAsync/StopAsync) is covered by integration tests against
/// the Docker stack. These tests focus on:
///   - Weighted scenario selection
///   - Destination mapping per scenario
///   - Call duration ranges per scenario
///   - Ramp-up target calculation
/// </summary>
public sealed class CallPatternSchedulerTests
{
    private static readonly Dictionary<string, int> DefaultMix = new()
    {
        ["NormalAnswer"] = 60,
        ["ShortCall"] = 10,
        ["LongCall"] = 5,
        ["Transfer"] = 5,
        ["Hold"] = 5,
        ["IvrNavigation"] = 5,
        ["NoAnswer"] = 3,
        ["Busy"] = 3,
        ["Voicemail"] = 2,
        ["Conference"] = 2
    };

    private static CallPatternOptions DefaultOptions() => new()
    {
        CallsPerMinute = 100,
        MaxConcurrentCalls = 300,
        DefaultCallDurationSecs = 180,
        MinCallDurationSecs = 30,
        MaxCallDurationSecs = 900,
        RampUpMinutes = 5,
        TestDurationMinutes = 60,
        ScenarioMix = new Dictionary<string, int>(DefaultMix)
    };

    // -------------------------------------------------------------------------
    // PickScenarioDestination
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("NormalAnswer", "105")]
    [InlineData("ShortCall", "105")]
    [InlineData("LongCall", "105")]
    [InlineData("Transfer", "105")]
    [InlineData("Hold", "105")]
    [InlineData("IvrNavigation", "105")]
    [InlineData("NoAnswer", "105")]
    [InlineData("Busy", "105")]
    [InlineData("Voicemail", "105")]
    public void PickScenarioDestination_ShouldReturnDefaultDestination_ForNonConferenceScenario(
        string scenario, string expected)
    {
        var destination = CallPatternScheduler.PickScenarioDestination(scenario);

        destination.Should().Be(expected);
    }

    [Fact]
    public void PickScenarioDestination_ShouldReturnConferenceRoom_ForConferenceScenario()
    {
        var destination = CallPatternScheduler.PickScenarioDestination("Conference");

        destination.Should().Be("801");
    }

    // -------------------------------------------------------------------------
    // PickScenario — weighted distribution
    // -------------------------------------------------------------------------

    [Fact]
    public void PickScenarioDestination_ShouldRespectWeights_OverManyTrials()
    {
        // Generate 1000 picks and verify distribution is within ±10% of expected weight
        const int trials = 1000;
        var random = new Random(42);
        var counts = DefaultMix.Keys.ToDictionary(k => k, _ => 0);
        int totalWeight = DefaultMix.Values.Sum();

        for (int i = 0; i < trials; i++)
        {
            var scenario = CallPatternScheduler.PickScenario(DefaultMix, random);
            counts[scenario]++;
        }

        foreach (var (scenario, weight) in DefaultMix)
        {
            double expectedFraction = (double)weight / totalWeight;
            double actualFraction = (double)counts[scenario] / trials;
            double tolerance = 0.10; // ±10%

            actualFraction.Should().BeApproximately(expectedFraction, tolerance,
                because: $"scenario '{scenario}' with weight {weight} should appear ~{expectedFraction:P0} of the time");
        }
    }

    [Fact]
    public void PickScenario_ShouldOnlyReturnKnownScenarios()
    {
        var random = new Random(99);

        for (int i = 0; i < 500; i++)
        {
            var scenario = CallPatternScheduler.PickScenario(DefaultMix, random);
            DefaultMix.Keys.Should().Contain(scenario);
        }
    }

    // -------------------------------------------------------------------------
    // PickCallDuration
    // -------------------------------------------------------------------------

    [Fact]
    public void PickCallDuration_ShouldReturnShortDuration_ForShortCallScenario()
    {
        var random = new Random(1);
        const int options = 100;

        for (int i = 0; i < options; i++)
        {
            int duration = CallPatternScheduler.PickCallDuration("ShortCall", 180, 30, 900, random);

            duration.Should().BeGreaterThanOrEqualTo(10)
                .And.BeLessThanOrEqualTo(30,
                    because: "ShortCall scenario should last 10–30 seconds");
        }
    }

    [Fact]
    public void PickCallDuration_ShouldReturnLongDuration_ForLongCallScenario()
    {
        var random = new Random(2);
        const int options = 100;

        for (int i = 0; i < options; i++)
        {
            int duration = CallPatternScheduler.PickCallDuration("LongCall", 180, 30, 900, random);

            duration.Should().BeGreaterThanOrEqualTo(600)
                .And.BeLessThanOrEqualTo(900,
                    because: "LongCall scenario should last 600–900 seconds");
        }
    }

    [Fact]
    public void PickCallDuration_ShouldReturnNormalRange_ForNormalAnswer()
    {
        // NormalAnswer: defaultDurationSecs ± 50%
        // default = 180 → range is [90, 270]
        const int defaultSecs = 180;
        int expectedLow = (int)(defaultSecs * 0.5);    // 90
        int expectedHigh = (int)(defaultSecs * 1.5);   // 270

        var random = new Random(3);

        for (int i = 0; i < 200; i++)
        {
            int duration = CallPatternScheduler.PickCallDuration("NormalAnswer", defaultSecs, 30, 900, random);

            duration.Should().BeGreaterThanOrEqualTo(expectedLow)
                .And.BeLessThanOrEqualTo(expectedHigh,
                    because: $"NormalAnswer should be within ±50% of {defaultSecs}s");
        }
    }

    [Theory]
    [InlineData("Transfer")]
    [InlineData("Hold")]
    [InlineData("IvrNavigation")]
    [InlineData("NoAnswer")]
    [InlineData("Busy")]
    [InlineData("Voicemail")]
    [InlineData("Conference")]
    public void PickCallDuration_ShouldReturnDefaultDuration_ForOtherScenarios(string scenario)
    {
        const int defaultSecs = 180;
        var random = new Random(4);

        int duration = CallPatternScheduler.PickCallDuration(scenario, defaultSecs, 30, 900, random);

        duration.Should().Be(defaultSecs,
            because: $"scenario '{scenario}' should use the default duration");
    }

    // -------------------------------------------------------------------------
    // CalculateRampTarget
    // -------------------------------------------------------------------------

    [Fact]
    public void CalculateRampTarget_ShouldBeZero_AtStart()
    {
        int result = CallPatternScheduler.CalculateRampTarget(
            elapsed: TimeSpan.Zero,
            targetConcurrent: 100,
            rampUpMinutes: 5);

        result.Should().Be(0);
    }

    [Fact]
    public void CalculateRampTarget_ShouldBeTarget_AfterRampUp()
    {
        int result = CallPatternScheduler.CalculateRampTarget(
            elapsed: TimeSpan.FromMinutes(5),
            targetConcurrent: 100,
            rampUpMinutes: 5);

        result.Should().Be(100);
    }

    [Fact]
    public void CalculateRampTarget_ShouldBeTarget_WhenElapsedExceedsRampUp()
    {
        int result = CallPatternScheduler.CalculateRampTarget(
            elapsed: TimeSpan.FromMinutes(10),
            targetConcurrent: 100,
            rampUpMinutes: 5);

        result.Should().Be(100);
    }

    [Fact]
    public void CalculateRampTarget_ShouldBeHalf_AtMidpoint()
    {
        // At 2.5 minutes (midpoint of 5-minute ramp), expect ~50
        int result = CallPatternScheduler.CalculateRampTarget(
            elapsed: TimeSpan.FromMinutes(2.5),
            targetConcurrent: 100,
            rampUpMinutes: 5);

        // Allow ±1 due to rounding
        result.Should().BeCloseTo(50, delta: 1);
    }

    [Fact]
    public void CalculateRampTarget_ShouldNeverExceedTarget_EdgeCases()
    {
        const int target = 200;
        const int rampMinutes = 3;

        // Edge: exactly at ramp boundary
        int atBoundary = CallPatternScheduler.CalculateRampTarget(
            TimeSpan.FromMinutes(rampMinutes), target, rampMinutes);
        atBoundary.Should().Be(target);

        // Edge: slightly past boundary
        int pastBoundary = CallPatternScheduler.CalculateRampTarget(
            TimeSpan.FromMinutes(rampMinutes + 0.001), target, rampMinutes);
        pastBoundary.Should().Be(target);

        // Edge: large elapsed time (well past ramp)
        int largElapsed = CallPatternScheduler.CalculateRampTarget(
            TimeSpan.FromHours(2), target, rampMinutes);
        largElapsed.Should().Be(target);

        // All results must not exceed target
        atBoundary.Should().BeLessThanOrEqualTo(target);
        pastBoundary.Should().BeLessThanOrEqualTo(target);
        largElapsed.Should().BeLessThanOrEqualTo(target);
    }

    [Fact]
    public void CalculateRampTarget_ShouldReturnTarget_WhenRampUpMinutesIsZero()
    {
        // Zero ramp-up means start at full capacity immediately
        int result = CallPatternScheduler.CalculateRampTarget(
            elapsed: TimeSpan.Zero,
            targetConcurrent: 50,
            rampUpMinutes: 0);

        result.Should().Be(50);
    }

    // -------------------------------------------------------------------------
    // ScenarioMix — coverage
    // -------------------------------------------------------------------------

    [Fact]
    public void ScenarioMix_ShouldCoverAllScenarios_WithPositiveWeights()
    {
        var options = DefaultOptions();

        string[] expectedScenarios =
        [
            "NormalAnswer", "ShortCall", "LongCall", "Transfer", "Hold",
            "IvrNavigation", "NoAnswer", "Busy", "Voicemail", "Conference"
        ];

        options.ScenarioMix.Should().HaveCount(expectedScenarios.Length);

        foreach (var scenario in expectedScenarios)
        {
            options.ScenarioMix.Should().ContainKey(scenario,
                because: $"scenario '{scenario}' should be present in the mix");

            options.ScenarioMix[scenario].Should().BeGreaterThan(0,
                because: $"scenario '{scenario}' should have a positive weight");
        }
    }

    [Fact]
    public void ScenarioMix_ShouldSumToOneHundred()
    {
        var options = DefaultOptions();

        int total = options.ScenarioMix.Values.Sum();

        total.Should().Be(100);
    }
}
