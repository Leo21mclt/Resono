using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Resono.Plugin.Services
{
    public class ResonoItemCache
    {
        public class Entry
        {
            public Guid Id { get; set; }
            public string Kind { get; set; } = "track"; // "track" | "album" | "artist"
            public string? Name { get; set; }
            public string? ArtistName { get; set; }
            public string? AlbumName { get; set; }
            public string? SpotifyId { get; set; }
            public string? ImageUrl { get; set; }
            public int? DurationMs { get; set; }
            public int? TrackNumber { get; set; }
            public int? DiscNumber { get; set; }
            public string? StreamUrl { get; set; }
            public Guid? AlbumId { get; set; }
            public Guid? ArtistId { get; set; }
        }

        private readonly ConcurrentDictionary<Guid, Entry> _items = new();

        public int Count => _items.Count;

        public void Set(Guid id, Entry entry)
        {
            entry.Id = id;
            _items[id] = entry;
        }

        public bool TryGet(Guid id, out Entry? entry)
        {
            return _items.TryGetValue(id, out entry);
        }

        public static Guid DeterministicGuid(string input)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
            return new Guid(hash);
        }
    }
}
