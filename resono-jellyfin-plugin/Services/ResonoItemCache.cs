using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Resono.Plugin.Services
{
    public class ResonoItemCache
    {
        public class Entry
        {
            public Guid Id { get; set; }
            public string Kind { get; set; } = "track";
            public string? Name { get; set; }
            public string? ArtistName { get; set; }
            public string? AlbumName { get; set; }
            public string? SpotifyId { get; set; }
            public string? CanonicalId { get; set; }
            public string? ImageUrl { get; set; }
            public int? DurationMs { get; set; }
            public int? TrackNumber { get; set; }
            public int? DiscNumber { get; set; }
            public string? StreamUrl { get; set; }
            public Guid? AlbumId { get; set; }
            public Guid? ArtistId { get; set; }
            internal long LastAccessedTicks { get; set; } = DateTime.UtcNow.Ticks;
        }

        private readonly ConcurrentDictionary<Guid, Entry> _items = new();
        private const int MaxItems = 10000;
        private const int EvictionBatchSize = 2000;
        private int _evictionRunning;

        public int Count => _items.Count;

        public void Set(Guid id, Entry entry)
        {
            entry.Id = id;
            entry.LastAccessedTicks = DateTime.UtcNow.Ticks;
            _items[id] = entry;

            if (_items.Count > MaxItems && Interlocked.CompareExchange(ref _evictionRunning, 1, 0) == 0)
            {
                try { EvictOldest(); } finally { Interlocked.Exchange(ref _evictionRunning, 0); }
            }
        }

        public bool TryGet(Guid id, out Entry? entry)
        {
            if (_items.TryGetValue(id, out entry))
            {
                entry.LastAccessedTicks = DateTime.UtcNow.Ticks;
                return true;
            }
            return false;
        }

        private void EvictOldest()
        {
            var toEvict = _items
                .OrderBy(kv => kv.Value.LastAccessedTicks)
                .Take(EvictionBatchSize)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in toEvict)
            {
                _items.TryRemove(key, out _);
            }
        }

        public static Guid DeterministicGuid(string input)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
            return new Guid(hash);
        }
    }
}
