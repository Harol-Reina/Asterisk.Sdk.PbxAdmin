using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Generates 10 sequential calls to extension 200 (1 per second) with 5 agents.
/// Validates that calls are distributed across multiple agents by checking that
/// queue_log CONNECT events reference different agent channels.
/// </summary>
public sealed class QueueDistributionScenario : ITestScenario
{
    public string Name => "queue-distribution";
    public string Description => "10 sequential calls to ext 200 at 1/s with 5 agents; validates round-robin distribution via distinct dstchannel in CDR";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<QueueDistributionScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation("[{Scenario}] Generating 10 sequential calls to extension 200", Name);

        try
        {
            for (int i = 0; i < 10; i++)
            {
                ct.ThrowIfCancellationRequested();
                var result = await context.CallGenerator.GenerateCallAsync("200", cancellationToken: ct);
                context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
                context.Metrics.RecordCallOriginated();

                if (result.Accepted)
                    logger.LogDebug("[{Scenario}] Call {N}/10 accepted: {CallId}", Name, i + 1, result.CallId);
                else
                    logger.LogWarning("[{Scenario}] Call {N}/10 rejected: {Error}", Name, i + 1, result.ErrorMessage);

                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{Scenario}] Call generation failed", Name);
        }

        logger.LogInformation("[{Scenario}] Waiting 60s for all calls to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(60), ct);

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
                    ValidatorName = "QueueDistributionScenario",
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

        // Additional distribution check: verify multiple distinct agents were assigned
        var snapshots = context.EventCapture.GetAllSnapshots();
        var distinctAgents = snapshots
            .Where(s => !string.IsNullOrEmpty(s.AgentChannel))
            .Select(s => s.AgentChannel!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool distributionOk = distinctAgents.Count > 1;
        results.Add(new Validation.ValidationResult
        {
            CallId = "distribution-check",
            ValidatorName = "QueueDistributionScenario",
            Passed = distributionOk,
            Checks =
            [
                new Validation.ValidationCheck
                {
                    CheckName = "MultipleAgentsUsed",
                    Passed = distributionOk,
                    Expected = ">1 distinct agent channel",
                    Actual = $"{distinctAgents.Count} distinct agent(s): {string.Join(", ", distinctAgents)}",
                    Message = distributionOk ? null : "All calls were handled by the same agent — queue distribution may not be working"
                }
            ]
        });

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
