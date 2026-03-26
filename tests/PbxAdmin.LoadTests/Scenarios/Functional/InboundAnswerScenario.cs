using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Generates 3 inbound calls to extension 105 (loadtest queue → agent answers)
/// and validates that each call was answered, CDR is present, and CEL shows the
/// expected event sequence.
/// </summary>
public sealed class InboundAnswerScenario : ITestScenario
{
    public string Name => "inbound-answer";
    public string Description => "3 inbound calls to loadtest queue (ext 105) routed to agent; validates CDR=ANSWERED and CEL sequence";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<InboundAnswerScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation("[{Scenario}] Generating 3 inbound calls to extension 105 (loadtest queue)", Name);

        try
        {
            for (int i = 0; i < 3; i++)
            {
                ct.ThrowIfCancellationRequested();
                var result = await context.CallGenerator.GenerateCallAsync("105", cancellationToken: ct);
                context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
                context.Metrics.RecordCallOriginated();

                if (result.Accepted)
                    logger.LogDebug("[{Scenario}] Call {N}/3 accepted: {CallId}", Name, i + 1, result.CallId);
                else
                    logger.LogWarning("[{Scenario}] Call {N}/3 rejected: {Error}", Name, i + 1, result.ErrorMessage);

                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{Scenario}] Call generation failed", Name);
        }

        // Wait for ring + talk + hangup cycle to complete
        logger.LogInformation("[{Scenario}] Waiting 40s for calls to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(40), ct);

        context.TestEndTime = DateTime.UtcNow;
    }

    public async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        // Wait for Asterisk to flush CDR/CEL to PostgreSQL
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
                var queueEvents = snapshot.QueueName is not null
                    ? await context.QueueLogReader.GetQueueEventsForCallAsync(snapshot.CallId, ct)
                    : [];

                results.Add(SessionValidator.ValidateCall(snapshot, cdr));
                results.Add(EventSequenceValidator.ValidateEventSequence(snapshot, celEvents));
                results.Add(QueueValidator.ValidateQueueCall(snapshot, queueEvents));
            }
            catch (Exception ex)
            {
                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "InboundAnswerScenario",
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
