using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Generates calls across different dispositions (answered, failed) and validates
/// that the SDK CallSession data matches CDR records in the database.
/// Phase 1: 5 answered calls to ext 105 (queue → agent answers)
/// Phase 2: 2 failed calls to ext 999 (invalid destination)
/// </summary>
public sealed class SdkSessionAccuracyScenario : ITestScenario
{
    public string Name => "sdk-session-accuracy";
    public string Description => "Validates SDK CallSession accuracy against CDR for answered and failed calls";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkSessionAccuracyScenario>();
        context.TestStartTime = DateTime.UtcNow;

        if (context.SdkRuntime is null || context.SessionCapture is null)
        {
            logger.LogError("[{Scenario}] SDK infrastructure not available — cannot run", Name);
            throw new InvalidOperationException("SdkRuntime and SessionCapture are required for this scenario");
        }

        // Phase 1: 5 answered calls to ext 105 (loadtest queue)
        logger.LogInformation("[{Scenario}] Phase 1: Generating 5 answered calls to extension 105", Name);
        try
        {
            for (int i = 0; i < 5; i++)
            {
                ct.ThrowIfCancellationRequested();
                var result = await context.CallGenerator.GenerateCallAsync("105", cancellationToken: ct);
                context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
                context.Metrics.RecordCallOriginated();

                if (result.Accepted)
                    logger.LogDebug("[{Scenario}] Phase 1 call {N}/5 accepted: {CallId}", Name, i + 1, result.CallId);
                else
                    logger.LogWarning("[{Scenario}] Phase 1 call {N}/5 rejected: {Error}", Name, i + 1, result.ErrorMessage);

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{Scenario}] Phase 1 call generation failed", Name);
        }

        // Wait for answered calls to complete (ring + talk + hangup)
        logger.LogInformation("[{Scenario}] Waiting 45s for Phase 1 calls to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(45), ct);

        // Phase 2: 2 failed calls to ext 999 (invalid destination)
        logger.LogInformation("[{Scenario}] Phase 2: Generating 2 failed calls to extension 999", Name);
        try
        {
            for (int i = 0; i < 2; i++)
            {
                ct.ThrowIfCancellationRequested();
                var result = await context.CallGenerator.GenerateCallAsync("999", cancellationToken: ct);
                context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
                context.Metrics.RecordCallOriginated();

                if (result.Accepted)
                    logger.LogDebug("[{Scenario}] Phase 2 call {N}/2 accepted: {CallId}", Name, i + 1, result.CallId);
                else
                    logger.LogWarning("[{Scenario}] Phase 2 call {N}/2 rejected: {Error}", Name, i + 1, result.ErrorMessage);

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{Scenario}] Phase 2 call generation failed", Name);
        }

        // Wait for failed calls to complete
        logger.LogInformation("[{Scenario}] Waiting 15s for Phase 2 calls to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        // Stop session capture
        if (context.SessionCapture is not null)
        {
            await context.SessionCapture.StopAsync();
            logger.LogInformation(
                "[{Scenario}] SessionCapture stopped. Captured={Count}",
                Name, context.SessionCapture.CompletedSessionCount);
        }

        context.TestEndTime = DateTime.UtcNow;
    }

    public async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        // Wait for Asterisk to flush CDR to PostgreSQL
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        var results = new List<ValidationResult>();

        if (context.SessionCapture is null)
        {
            results.Add(new ValidationResult
            {
                CallId = "sdk-session-accuracy",
                ValidatorName = nameof(SdkSessionAccuracyScenario),
                Passed = false,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "SessionCaptureAvailable",
                        Passed = false,
                        Message = "SessionCapture was not available — SDK runtime is required for this scenario"
                    }
                ]
            });

            return BuildReport(context, results);
        }

        var sessions = context.SessionCapture.GetCompletedSessions();

        // Validate each captured session against its CDR
        foreach (var session in sessions)
        {
            try
            {
                var cdr = session.CallerNumber is not null
                    ? await context.CdrReader.GetCallBySrcAsync(session.CallerNumber, context.TestStartTime, ct)
                    : null;

                results.Add(SessionValidator.ValidateSession(session, cdr));
            }
            catch (Exception ex)
            {
                results.Add(new ValidationResult
                {
                    CallId = session.SessionId,
                    ValidatorName = nameof(SdkSessionAccuracyScenario),
                    Passed = false,
                    Checks =
                    [
                        new ValidationCheck
                        {
                            CheckName = "ValidationException",
                            Passed = false,
                            Message = ex.Message
                        }
                    ]
                });
            }
        }

        // Aggregate check: enough sessions were tracked
        const int expectedMinSessions = 7;
        bool allTracked = sessions.Count >= expectedMinSessions;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkSessionAccuracyScenario),
            Passed = allTracked,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "AllSessionsTracked",
                    Passed = allTracked,
                    Expected = $">= {expectedMinSessions}",
                    Actual = sessions.Count.ToString(),
                    Message = allTracked ? null : $"Expected at least {expectedMinSessions} sessions but captured {sessions.Count}"
                }
            ]
        });

        return BuildReport(context, results);
    }

    private static ValidationReport BuildReport(TestContext context, List<ValidationResult> results) => new()
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
