using PbxAdmin.LoadTests.Validation.Layer2.Repositories;

namespace PbxAdmin.LoadTests.Validation.Layer2;

public sealed class CelReader
{
    private static readonly HashSet<string> BridgeEventTypes = new(StringComparer.Ordinal)
    {
        "BRIDGE_ENTER",
        "BRIDGE_EXIT"
    };

    private readonly ICelReadRepository _repository;

    public CelReader(ICelReadRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Returns all CEL events for a call ordered by event time.
    /// </summary>
    public async Task<List<CelRecord>> GetEventSequenceAsync(
        string linkedId, CancellationToken ct = default)
    {
        var events = await _repository.GetByLinkedIdAsync(linkedId, ct);
        return events.OrderBy(e => e.EventTime).ToList();
    }

    /// <summary>
    /// Returns only BRIDGE_ENTER and BRIDGE_EXIT events for a call, ordered by event time.
    /// </summary>
    public async Task<List<CelRecord>> GetBridgeEventsAsync(
        string linkedId, CancellationToken ct = default)
    {
        var events = await _repository.GetByLinkedIdAsync(linkedId, ct);
        return events
            .Where(e => BridgeEventTypes.Contains(e.EventType))
            .OrderBy(e => e.EventTime)
            .ToList();
    }
}
