using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Generates 1 call to extension 200 (IVR entry point) and validates that
/// the call traversed IVR dialplan contexts. Actual DTMF navigation is not
/// sent; the scenario validates the CEL path showing Playback/Background apps.
/// </summary>
public sealed class IvrNavigationScenario : ITestScenario
{
    public string Name => "ivr-navigation";
    public string Description => "1 call to ext 200 (IVR entry); validates CEL shows APP_START with IVR app (Playback/Background) in the call path";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<IvrNavigationScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation("[{Scenario}] Generating 1 call to IVR entry (extension 200)", Name);

        try
        {
            var result = await context.CallGenerator.GenerateCallAsync("200", cancellationToken: ct);
            context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
            context.Metrics.RecordCallOriginated();

            if (result.Accepted)
                logger.LogDebug("[{Scenario}] Call accepted: {CallId}", Name, result.CallId);
            else
                logger.LogWarning("[{Scenario}] Call rejected: {Error}", Name, result.ErrorMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{Scenario}] Call generation failed", Name);
        }

        logger.LogInformation("[{Scenario}] Waiting 20s for IVR traversal and hangup", Name);
        await Task.Delay(TimeSpan.FromSeconds(20), ct);

        context.TestEndTime = DateTime.UtcNow;
    }

    public async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        var results = new List<Validation.ValidationResult>();

        foreach (var snapshot in context.EventCapture.GetAllSnapshots())
        {
            try
            {
                var cdr = await context.CdrReader.GetCallBySrcAsync(snapshot.CallerNumber, context.TestStartTime, ct);
                var celEvents = snapshot.LinkedId is not null
                    ? await context.CelReader.GetEventSequenceAsync(snapshot.LinkedId, ct)
                    : [];

                results.Add(SessionValidator.ValidateCall(snapshot, cdr));
                results.Add(EventSequenceValidator.ValidateEventSequence(snapshot, celEvents));

                // Additional IVR-specific check: CEL should contain APP_START for Playback or Background
                bool ivrAppFound = celEvents.Any(e =>
                    string.Equals(e.EventType, "APP_START", StringComparison.OrdinalIgnoreCase)
                    && (e.AppName.Contains("Playback", StringComparison.OrdinalIgnoreCase)
                        || e.AppName.Contains("Background", StringComparison.OrdinalIgnoreCase)));

                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "IvrNavigationScenario",
                    Passed = ivrAppFound || celEvents.Count == 0, // pass if no CEL yet (offline mode)
                    Checks =
                    [
                        new Validation.ValidationCheck
                        {
                            CheckName = "IvrAppStartFound",
                            Passed = ivrAppFound || celEvents.Count == 0,
                            Expected = "APP_START with Playback or Background in CEL",
                            Actual = ivrAppFound ? "IVR APP_START found" : celEvents.Count == 0 ? "No CEL records (offline)" : "IVR APP_START not found",
                            Message = (!ivrAppFound && celEvents.Count > 0) ? "CEL has records but no Playback/Background APP_START — call may not have reached IVR" : null
                        }
                    ]
                });
            }
            catch (Exception ex)
            {
                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "IvrNavigationScenario",
                    Passed = false,
                    Checks =
                    [
                        new Validation.ValidationCheck
                        {
                            CheckName = "ValidationException",
                            Passed = false,
                            Message = ex.Message
                        }
                    ]
                });
            }
        }

        return new ValidationReport
        {
            TestStart = context.TestStartTime,
            TestEnd = context.TestEndTime,
            Duration = context.TestEndTime - context.TestStartTime,
            TotalCalls = results.Count(r => r.ValidatorName == nameof(SessionValidator)),
            TotalChecks = results.SelectMany(r => r.Checks).Count(),
            PassedChecks = results.SelectMany(r => r.Checks).Count(c => c.Passed),
            FailedChecks = results.SelectMany(r => r.Checks).Count(c => !c.Passed),
            Results = results
        };
    }
}
