using Microsoft.Extensions.Options;

namespace DraxView.Services;

public sealed class EventOptions
{
    public int MaxRetained { get; set; } = 1000;
}

// In-memory, newest-first, bounded. Enough for a feel-for-effort build; the
// real thing wants a database, which doubles as event history for reports.
public sealed class EventStore
{
    private readonly LinkedList<DraxEvent> _events = new();
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
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<DraxEvent> Snapshot()
    {
        lock (_lock) return _events.ToList();
    }

    public void Clear()
    {
        lock (_lock) _events.Clear();
        Changed?.Invoke();
    }
}
