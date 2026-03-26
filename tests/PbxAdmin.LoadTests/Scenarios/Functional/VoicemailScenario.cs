using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Generates 1 call to extension 200 when no agents are available to answer.
/// The call should ring out and fall through to voicemail. Validates that the
/// CDR exists (voicemail answers = ANSWERED or NO ANSWER) and that CEL shows
/// the VoiceMail application.
/// </summary>
public sealed class VoicemailScenario : ITestScenario
{
    public string Name => "voicemail";
    public string Description => "1 call to ext 200 with no agents; expects ring timeout and voicemail fallback; validates CDR and CEL VoiceMail app";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<VoicemailScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation("[{Scenario}] Generating 1 call to extension 200 (voicemail fallback expected)", Name);

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

        // Wait for ring timeout (20s) + voicemail greeting + caller hangs up
        logger.LogInformation("[{Scenario}] Waiting 40s for ring timeout and voicemail to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(40), ct);

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

                // Voicemail-specific: CEL should contain APP_START for VoiceMail
                bool voicemailAppFound = celEvents.Any(e =>
                    string.Equals(e.EventType, "APP_START", StringComparison.OrdinalIgnoreCase)
                    && e.AppName.Contains("VoiceMail", StringComparison.OrdinalIgnoreCase));

                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "VoicemailScenario",
                    Passed = voicemailAppFound || celEvents.Count == 0,
                    Checks =
                    [
                        new Validation.ValidationCheck
                        {
                            CheckName = "VoicemailAppStartFound",
                            Passed = voicemailAppFound || celEvents.Count == 0,
                            Expected = "APP_START with VoiceMail in CEL",
                            Actual = voicemailAppFound ? "VoiceMail APP_START found" : celEvents.Count == 0 ? "No CEL (offline)" : "VoiceMail APP_START missing",
                            Message = (!voicemailAppFound && celEvents.Count > 0) ? "CEL has no VoiceMail APP_START — call may not have reached voicemail" : null
                        }
                    ]
                });
            }
            catch (Exception ex)
            {
                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "VoicemailScenario",
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
