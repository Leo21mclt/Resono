using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Resono.Plugin.Services
{
    public class ResonoItemCache
    {
        public class Entry
        {
            public Guid Id { get; set; }
            public string Kind { get; set; } = "track"; // "track" | "album" | "artist" | "playlist"
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
            public int? ProductionYear { get; set; }
            public DateTime? PremiereDate { get; set; }
            public List<string>? Genres { get; set; }
            public long LastAccessedTicks { get; set; } = DateTime.UtcNow.Ticks;
        }

        private static readonly Guid ResonoNamespace = Guid.Parse("76824f11-0cb1-4fa3-80b6-71d53344b5a3");
        private readonly ConcurrentDictionary<Guid, Entry> _items = new();
        private readonly ConcurrentDictionary<string, Guid> _artistsByName = new(StringComparer.OrdinalIgnoreCase);
        private const int MaxItems = 20000;
        private const int EvictionBatchSize = 3000;
        private int _evictionRunning;

        private readonly ILogger<ResonoItemCache>? _log;
        private readonly string? _persistFile;
        private readonly Timer? _saveTimer;
        private readonly object _saveLock = new();
        private int _dirty;
        private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);

        public int Count => _items.Count;

        public ResonoItemCache(ILogger<ResonoItemCache>? log = null)
        {
            _log = log;
            try
            {
                var dir = Plugin.Instance?.DataFolderPath;
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    _persistFile = Path.Combine(dir, "resono-cache.json");
                    Load();
                    _saveTimer = new Timer(_ => SaveIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "[Resono] Cache init failed; running in-memory only");
            }
        }

        public void Set(Guid id, Entry entry)
        {
            entry.Id = id;
            entry.LastAccessedTicks = DateTime.UtcNow.Ticks;
            _items[id] = entry;

            if (entry.Kind == "artist" && !string.IsNullOrWhiteSpace(entry.Name))
            {
                _artistsByName[entry.Name.Trim()] = id;
            }

            ScheduleSave();

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

        public bool TryGetArtistByName(string name, out Entry? entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (_artistsByName.TryGetValue(name.Trim(), out var id) && TryGet(id, out entry))
            {
                return true;
            }
            // Secondary scan
            var matched = _items.Values.FirstOrDefault(e => e.Kind == "artist" && string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                entry = matched;
                _artistsByName[name.Trim()] = matched.Id;
                return true;
            }
            return false;
        }

        public IReadOnlyDictionary<Guid, Entry> Snapshot() =>
            _items.ToDictionary(kv => kv.Key, kv => kv.Value);

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

        private void Load()
        {
            if (_persistFile is null || !File.Exists(_persistFile)) return;
            try
            {
                var json = File.ReadAllText(_persistFile);
                if (string.IsNullOrWhiteSpace(json)) return;
                var loaded = JsonSerializer.Deserialize<Dictionary<Guid, Entry>>(json, JsonOpts);
                if (loaded is null) return;
                foreach (var kv in loaded)
                {
                    _items[kv.Key] = kv.Value;
                    if (kv.Value.Kind == "artist" && !string.IsNullOrWhiteSpace(kv.Value.Name))
                    {
                        _artistsByName[kv.Value.Name.Trim()] = kv.Key;
                    }
                }
                _log?.LogInformation("[Resono] Loaded {N} items from disk cache", loaded.Count);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "[Resono] Cache load failed; starting fresh");
            }
        }

        private void ScheduleSave()
        {
            if (_saveTimer is null) return;
            Interlocked.Exchange(ref _dirty, 1);
            _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }

        private void SaveIfDirty()
        {
            if (_persistFile is null) return;
            lock (_saveLock)
            {
                if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
                try
                {
                    var snapshot = _items.ToDictionary(kv => kv.Key, kv => kv.Value);
                    var tmp = _persistFile + ".tmp";
                    using (var fs = File.Create(tmp))
                    {
                        JsonSerializer.Serialize(fs, snapshot, JsonOpts);
                    }
                    File.Move(tmp, _persistFile, overwrite: true);
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "[Resono] Cache save failed");
                }
            }
        }

        public static Guid StubGuid(string kind, string id)
        {
            var cleanId = id.Trim();
            var bytes = Encoding.UTF8.GetBytes($"{ResonoNamespace}|{kind}|{cleanId}");
            var hash = MD5.HashData(bytes);
            return new Guid(hash);
        }

        public static Guid DeterministicGuid(string input)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
            return new Guid(hash);
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };
    }
}
