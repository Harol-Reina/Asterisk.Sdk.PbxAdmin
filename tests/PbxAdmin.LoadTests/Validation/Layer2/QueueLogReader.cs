using PbxAdmin.LoadTests.Validation.Layer2.Repositories;

namespace PbxAdmin.LoadTests.Validation.Layer2;

public sealed class QueueLogReader
{
    private readonly IQueueLogRepository _repository;

    public QueueLogReader(IQueueLogRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Returns all queue log events for a given call ID, ordered by time.
    /// </summary>
    public Task<List<QueueLogRecord>> GetQueueEventsForCallAsync(
        string callId, CancellationToken ct = default)
        => _repository.GetByCallIdAsync(callId, ct);

    /// <summary>
    /// Computes SLA statistics for a queue over a time window.
    /// <paramref name="slaThresholdSecs"/> is the maximum wait time (in seconds)
    /// that still counts as within-SLA. The wait time is parsed from Data1 of
    /// CONNECT events, which Asterisk populates as the hold time in seconds.
    /// </summary>
    public async Task<QueueSlaStats> GetQueueSlaAsync(
        string queueName,
        DateTime from,
        DateTime to,
        int slaThresholdSecs,
        CancellationToken ct = default)
    {
        var events = await _repository.GetByQueueAndTimeRangeAsync(queueName, from, to, ct);

        int offered = events.Count(e => string.Equals(e.Event, "ENTERQUEUE", StringComparison.Ordinal));
        var connectEvents = events
            .Where(e => string.Equals(e.Event, "CONNECT", StringComparison.Ordinal))
            .ToList();
        int answered = connectEvents.Count;
        int abandoned = events.Count(e => string.Equals(e.Event, "ABANDON", StringComparison.Ordinal));

        int withinSla = 0;
        double totalWaitSecs = 0.0;

        foreach (var connect in connectEvents)
        {
            if (int.TryParse(connect.Data1, out int waitSecs))
            {
                totalWaitSecs += waitSecs;
                if (waitSecs <= slaThresholdSecs)
                    withinSla++;
            }
        }

        double avgWaitSecs = answered > 0 ? totalWaitSecs / answered : 0.0;

        return new QueueSlaStats
        {
            QueueName = queueName,
            Offered = offered,
            Answered = answered,
            Abandoned = abandoned,
            WithinSla = withinSla,
            AvgWaitSecs = avgWaitSecs
        };
    }
}
