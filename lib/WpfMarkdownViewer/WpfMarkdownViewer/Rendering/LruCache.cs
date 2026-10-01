namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// A small thread-safe LRU cache so the in-memory image and Mermaid caches stay bounded over a long session
/// instead of growing without limit. Evicts the least-recently-used entry once <c>capacity</c> is exceeded.
/// </summary>
internal sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new(); // most-recent at the front

    private sealed class Entry
    {
        public required TKey Key { get; init; }
        public required TValue Value { get; set; }
    }

    public LruCache(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(_capacity);
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value.Value = value;
                _order.Remove(existing);
                _order.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<Entry>(new Entry { Key = key, Value = value });
            _order.AddFirst(node);
            _map[key] = node;

            if (_map.Count > _capacity)
            {
                var lru = _order.Last!;
                _order.RemoveLast();
                _map.Remove(lru.Value.Key);
            }
        }
    }

    internal int CountForTest
    {
        get { lock (_gate) return _map.Count; }
    }
}
