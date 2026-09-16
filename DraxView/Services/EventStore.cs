using Microsoft.Extensions.Options;

namespace DraxView.Services;

public sealed class EventOptions
{
    public int MaxRetained { get; set; } = 1000;
}

// Two views of the same feed. The log is every event, newest first, bounded.
// The active list is current state, the way AMX shows it: an ON event raises a
// condition, the matching OFF (a device clearing, or the panel sending its
// clears after a reset) removes it. Enough for a feel-for-effort build; the
// real thing wants a database, which doubles as event history for reports.
public sealed class EventStore
{
    private readonly LinkedList<DraxEvent> _events = new();
    private readonly Dictionary<string, DraxEvent> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly int _max;
    private long _seq;

    public event Action? Changed;

    public EventStore(IOptions<EventOptions> options)
    {
        _max = Math.Max(10, options.Value.MaxRetained);
    }

    public long TotalReceived => Interlocked.Read(ref _seq);

    public void Add(string json)
    {
        var ev = DraxEvent.Parse(Interlocked.Increment(ref _seq), json);
        if (ev == null) return;
        lock (_lock)
        {
            _events.AddFirst(ev);
            while (_events.Count > _max) _events.RemoveLast();

            if (ev.On) _active[ev.ConditionKey] = ev;
            else _active.Remove(ev.ConditionKey);
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<DraxEvent> Snapshot()
    {
        lock (_lock) return _events.ToList();
    }

    // Conditions still raised, newest first.
    public IReadOnlyList<DraxEvent> ActiveSnapshot()
    {
        lock (_lock) return _active.Values.OrderByDescending(e => e.Seq).ToList();
    }

    public int ActiveCount
    {
        get { lock (_lock) return _active.Count; }
    }

    // Clears the log only. The active list is what the panel says is raised;
    // it comes down when the panel clears it, not from a button here.
    public void Clear()
    {
        lock (_lock) _events.Clear();
        Changed?.Invoke();
    }
}
