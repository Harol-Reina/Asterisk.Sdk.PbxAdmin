using System.Text.Json;
using FluentAssertions;
using PbxAdmin.LoadTests.Metrics;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.Tests.LoadTests;

public sealed class ReportGeneratorTests
{
    private static readonly DateTime BaseTime = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);

    private static ValidationReport BuildReport(int passed = 10, int failed = 0) =>
        new()
        {
            TestStart = BaseTime,
            TestEnd = BaseTime.AddMinutes(5),
            Duration = TimeSpan.FromMinutes(5),
            TotalCalls = 20,
            TotalChecks = passed + failed,
            PassedChecks = passed,
            FailedChecks = failed,
            Results = [],
            SdkBugsFound = []
        };

    private static MetricsSummary BuildMetrics() =>
        new()
        {
            CallsOriginated = 20,
            CallsAnswered = 18,
            CallsFailed = 2,
            PeakConcurrentCalls = 5,
            CallsPerMinute = 4.0,
            Elapsed = TimeSpan.FromMinutes(5)
        };

    // ── WriteJsonReport ───────────────────────────────────────────────────────

    [Fact]
    public void WriteJsonReport_ShouldCreateValidJson()
    {
        var report = BuildReport();
        var metrics = BuildMetrics();
        var path = Path.Combine(Path.GetTempPath(), $"pbxadmin-test-{Guid.NewGuid():N}.json");

        try
        {
            ReportGenerator.WriteJsonReport(report, metrics, path);

            File.Exists(path).Should().BeTrue();
            var content = File.ReadAllText(path);
            content.Should().NotBeNullOrWhiteSpace();

            // Must parse as valid JSON without throwing
            var doc = JsonDocument.Parse(content);
            doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WriteJsonReport_ShouldContainMetricsFields()
    {
        var report = BuildReport();
        var metrics = BuildMetrics();
        var path = Path.Combine(Path.GetTempPath(), $"pbxadmin-test-{Guid.NewGuid():N}.json");

        try
        {
            ReportGenerator.WriteJsonReport(report, metrics, path);

            var content = File.ReadAllText(path);
            var doc = JsonDocument.Parse(content);

            doc.RootElement.TryGetProperty("metrics", out var metricsEl).Should().BeTrue();
            metricsEl.GetProperty("callsOriginated").GetInt32().Should().Be(20);
            metricsEl.GetProperty("callsAnswered").GetInt32().Should().Be(18);
            metricsEl.GetProperty("callsFailed").GetInt32().Should().Be(2);
            metricsEl.GetProperty("peakConcurrentCalls").GetInt32().Should().Be(5);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WriteJsonReport_ShouldContainValidationFields()
    {
        var report = BuildReport(passed: 8, failed: 2);
        var metrics = BuildMetrics();
        var path = Path.Combine(Path.GetTempPath(), $"pbxadmin-test-{Guid.NewGuid():N}.json");

        try
        {
            ReportGenerator.WriteJsonReport(report, metrics, path);

            var content = File.ReadAllText(path);
            var doc = JsonDocument.Parse(content);

            doc.RootElement.TryGetProperty("validation", out var valEl).Should().BeTrue();
            valEl.GetProperty("passedChecks").GetInt32().Should().Be(8);
            valEl.GetProperty("failedChecks").GetInt32().Should().Be(2);
            valEl.GetProperty("totalChecks").GetInt32().Should().Be(10);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── WriteConsoleReport ────────────────────────────────────────────────────

    [Fact]
    public void WriteConsoleReport_ShouldNotThrow()
    {
        var report = BuildReport();
        var metrics = BuildMetrics();

        var act = () => ReportGenerator.WriteConsoleReport(report, metrics);

        act.Should().NotThrow();
    }

    [Fact]
    public void WriteConsoleReport_ShouldNotThrow_WhenThereAreFailures()
    {
        var report = new ValidationReport
        {
            TestStart = BaseTime,
            TestEnd = BaseTime.AddMinutes(5),
            Duration = TimeSpan.FromMinutes(5),
            TotalCalls = 10,
            TotalChecks = 5,
            PassedChecks = 3,
            FailedChecks = 2,
            Results =
            [
                new ValidationResult
                {
                    CallId = "loadtest-000001",
                    ValidatorName = "SessionValidator",
                    Passed = false,
                    Checks =
                    [
                        new ValidationCheck
                        {
                            CheckName = "CdrExists",
                            Passed = false,
                            Expected = "CDR present",
                            Actual = "CDR missing",
                            Message = "No CDR written for call loadtest-000001"
                        }
                    ]
                }
            ],
            SdkBugsFound = ["Bug: Hangup event missing for ANSWERED calls"]
        };
        var metrics = BuildMetrics();

        var act = () => ReportGenerator.WriteConsoleReport(report, metrics);

        act.Should().NotThrow();
    }
}
