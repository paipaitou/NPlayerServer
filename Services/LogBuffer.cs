namespace NPlayerServer.Services;

public sealed class LogBuffer
{
    private const int MaxEntries = 1000;
    private readonly object _gate = new();
    private readonly Queue<string> _items = new();

    public event Action<string>? EntryAdded;

    public void Add(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (_gate)
        {
            _items.Enqueue(line);
            while (_items.Count > MaxEntries) _items.Dequeue();
        }

        EntryAdded?.Invoke(line);
    }
}
