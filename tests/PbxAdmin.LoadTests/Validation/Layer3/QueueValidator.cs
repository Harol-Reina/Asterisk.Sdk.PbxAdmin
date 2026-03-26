using PbxAdmin.LoadTests.Validation.Layer1;
using PbxAdmin.LoadTests.Validation.Layer2;

namespace PbxAdmin.LoadTests.Validation.Layer3;

/// <summary>
/// Compares SDK queue observations against queue_log records to detect discrepancies
/// in agent assignment, queue membership, and call outcome.
/// </summary>
public static class QueueValidator
{
    private const string EventEnterQueue = "ENTERQUEUE";
    private const string EventConnect = "CONNECT";
    private const string EventAbandon = "ABANDON";

    public static ValidationResult ValidateQueueCall(SdkSnapshot sdk, List<QueueLogRecord> queueEvents)
    {
        var checks = new List<ValidationCheck>();

        // Check 1: queue_log must have ENTERQUEUE for this call
        bool queueEntryExists = queueEvents.Any(e =>
            string.Equals(e.Event, EventEnterQueue, StringComparison.OrdinalIgnoreCase));
        checks.Add(new ValidationCheck
        {
            CheckName = "QueueEntryExists",
            Passed = queueEntryExists,
            Expected = $"{EventEnterQueue} event in queue_log",
            Actual = queueEntryExists ? $"{EventEnterQueue} found" : $"{EventEnterQueue} missing",
            Message = queueEntryExists ? null : $"queue_log has no {EventEnterQueue} for call {sdk.CallId} — call may not have entered the queue"
        });

        if (queueEvents.Count > 0)
        {
            // Check 2: SDK queue name must match queue_log queuename
            bool queueNameMatch = true;
            string? queueNameMessage = null;

            if (!string.IsNullOrEmpty(sdk.QueueName))
            {
                var firstQueueEntry = queueEvents.FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.QueueName));

                if (firstQueueEntry is not null && !string.IsNullOrEmpty(firstQueueEntry.QueueName))
                {
                    queueNameMatch = string.Equals(sdk.QueueName, firstQueueEntry.QueueName, StringComparison.OrdinalIgnoreCase);
                    if (!queueNameMatch)
                        queueNameMessage = $"SDK queue '{sdk.QueueName}' does not match queue_log queue '{firstQueueEntry.QueueName}'";
                }
            }

            checks.Add(new ValidationCheck
            {
                CheckName = "QueueNameMatch",
                Passed = queueNameMatch,
                Expected = sdk.QueueName ?? "(not set)",
                Actual = queueEvents.FirstOrDefault(e => !string.IsNullOrEmpty(e.QueueName))?.QueueName ?? "(not set)",
                Message = queueNameMessage
            });

            // Check 3: Agent channel must match the CONNECT agent (if answered)
            bool agentMatch = true;
            string? agentMessage = null;
            var connectEvent = queueEvents.FirstOrDefault(e =>
                string.Equals(e.Event, EventConnect, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(sdk.AgentChannel) && connectEvent is not null)
            {
                agentMatch = string.Equals(sdk.AgentChannel, connectEvent.Agent, StringComparison.OrdinalIgnoreCase);
                if (!agentMatch)
                    agentMessage = $"SDK agent '{sdk.AgentChannel}' does not match queue_log CONNECT agent '{connectEvent.Agent}'";
            }

            checks.Add(new ValidationCheck
            {
                CheckName = "AgentMatch",
                Passed = agentMatch,
                Expected = sdk.AgentChannel ?? "(not set)",
                Actual = connectEvent?.Agent ?? "(no CONNECT event)",
                Message = agentMessage
            });

            // Check 4: If SDK says ANSWERED, queue_log must have CONNECT event
            bool connectPresent = true;
            string? connectMessage = null;

            if (string.Equals(sdk.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase))
            {
                connectPresent = connectEvent is not null;
                if (!connectPresent)
                    connectMessage = "SDK disposition is ANSWERED but queue_log has no CONNECT event — agent assignment was not recorded";
            }

            checks.Add(new ValidationCheck
            {
                CheckName = "ConnectPresent",
                Passed = connectPresent,
                Expected = string.Equals(sdk.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase)
                    ? "CONNECT event in queue_log"
                    : "N/A (call not answered)",
                Actual = connectEvent is not null ? "CONNECT found" : "CONNECT missing",
                Message = connectMessage
            });

            // Check 5: If SDK says NO ANSWER, queue_log must have ABANDON event
            bool abandonDetected = true;
            string? abandonMessage = null;

            if (string.Equals(sdk.Disposition, "NO ANSWER", StringComparison.OrdinalIgnoreCase))
            {
                bool abandonPresent = queueEvents.Any(e =>
                    string.Equals(e.Event, EventAbandon, StringComparison.OrdinalIgnoreCase));
                abandonDetected = abandonPresent;
                if (!abandonDetected)
                    abandonMessage = "SDK disposition is NO ANSWER but queue_log has no ABANDON event — abandoned call was not recorded";
            }

            checks.Add(new ValidationCheck
            {
                CheckName = "AbandonDetected",
                Passed = abandonDetected,
                Expected = string.Equals(sdk.Disposition, "NO ANSWER", StringComparison.OrdinalIgnoreCase)
                    ? "ABANDON event in queue_log"
                    : "N/A (call not abandoned)",
                Actual = queueEvents.Any(e => string.Equals(e.Event, EventAbandon, StringComparison.OrdinalIgnoreCase))
                    ? "ABANDON found"
                    : "ABANDON missing",
                Message = abandonMessage
            });
        }

        bool allPassed = checks.All(c => c.Passed);

        return new ValidationResult
        {
            CallId = sdk.CallId,
            ValidatorName = nameof(QueueValidator),
            Passed = allPassed,
            Checks = checks
        };
    }
}
