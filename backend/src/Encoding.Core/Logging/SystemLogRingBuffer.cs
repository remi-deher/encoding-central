using Encoding.Contracts.Logging;

namespace Encoding.Core.Logging;

/// <summary>
/// Tampon circulaire en mémoire thread-safe conservant les N derniers événements de log (2 000 par défaut).
/// Zéro I/O disque, lecture instantanée.
/// </summary>
public sealed class SystemLogRingBuffer
{
    private readonly int _capacity;
    private readonly SystemLogEntry?[] _buffer;
    private readonly object _sync = new();
    private int _head; // Index où sera écrit le prochain élément
    private int _count; // Nombre d'éléments actuellement présents
    private long _totalWritten; // Compteur absolu d'événements ajoutés

    public const int DefaultCapacity = 2000;

    public SystemLogRingBuffer(int capacity = DefaultCapacity)
    {
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
        _buffer = new SystemLogEntry[_capacity];
    }

    public int Capacity => _capacity;
    public int Count { get { lock (_sync) return _count; } }
    public long TotalWritten { get { lock (_sync) return _totalWritten; } }

    public void Add(SystemLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_sync)
        {
            _buffer[_head] = entry;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity)
                _count++;
            _totalWritten++;
        }
    }

    public IReadOnlyList<SystemLogEntry> Query(SystemLogFilterQuery? query = null)
    {
        query ??= new SystemLogFilterQuery();
        var limit = Math.Clamp(query.Limit <= 0 ? 100 : query.Limit, 1, 1000);

        List<SystemLogEntry> matched = new();

        lock (_sync)
        {
            if (_count == 0)
                return Array.Empty<SystemLogEntry>();

            // Parcourt du plus récent au plus ancien (antichronologique)
            // L'élément le plus récent est à (_head - 1 + _capacity) % _capacity
            for (var i = 0; i < _count; i++)
            {
                var idx = (_head - 1 - i + _capacity * 2) % _capacity;
                var entry = _buffer[idx];
                if (entry is null)
                    continue;

                if (query.MinLevel.HasValue && entry.Level < query.MinLevel.Value)
                    continue;

                if (!string.IsNullOrWhiteSpace(query.Category) &&
                    !entry.Category.StartsWith(query.Category.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (query.NodeId.HasValue && entry.NodeId != query.NodeId.Value)
                    continue;

                if (query.Since.HasValue && entry.Timestamp <= query.Since.Value)
                    continue;

                if (!string.IsNullOrWhiteSpace(query.Search))
                {
                    var term = query.Search.Trim();
                    var matchMessage = entry.Message.Contains(term, StringComparison.OrdinalIgnoreCase);
                    var matchException = entry.Exception != null && entry.Exception.Contains(term, StringComparison.OrdinalIgnoreCase);
                    if (!matchMessage && !matchException)
                        continue;
                }

                matched.Add(entry);
                if (matched.Count >= limit)
                    break;
            }
        }

        return matched;
    }

    public IReadOnlyList<string> GetCategories()
    {
        lock (_sync)
        {
            if (_count == 0)
                return Array.Empty<string>();

            HashSet<string> categories = new(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _count; i++)
            {
                var entry = _buffer[i];
                if (entry != null && !string.IsNullOrWhiteSpace(entry.Category))
                {
                    categories.Add(entry.Category);
                }
            }

            var list = categories.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_buffer, 0, _capacity);
            _head = 0;
            _count = 0;
        }
    }
}
