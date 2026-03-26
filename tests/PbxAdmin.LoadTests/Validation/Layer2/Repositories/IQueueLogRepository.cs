namespace PbxAdmin.LoadTests.Validation.Layer2.Repositories;

public interface IQueueLogRepository
{
    Task<List<QueueLogRecord>> GetByCallIdAsync(string callId, CancellationToken ct = default);
    Task<List<QueueLogRecord>> GetByQueueAndTimeRangeAsync(string queueName, DateTime from, DateTime to, CancellationToken ct = default);
    Task<List<QueueLogRecord>> GetByTimeRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
    Task<int> GetCountByEventAsync(string eventName, DateTime from, DateTime to, CancellationToken ct = default);
}
