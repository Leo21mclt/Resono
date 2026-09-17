using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Resono.Plugin.Services
{
    public class ResonoFavoritesTracker
    {
        public class FavoriteItem
        {
            public Guid ItemId { get; set; }
            public string Kind { get; set; } = "track"; // "track", "album", "artist"
            public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        }

        private const int MaxPerUser = 2000;
        private readonly ConcurrentDictionary<Guid, List<FavoriteItem>> _byUser = new();
        private readonly ILogger<ResonoFavoritesTracker> _log;
        private readonly string? _persistFile;
        private readonly Timer? _saveTimer;
        private readonly object _saveLock = new();
        private int _dirty;
        private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true
        };

        public ResonoFavoritesTracker(ILogger<ResonoFavoritesTracker> log)
        {
            _log = log;
            try
            {
                var dir = Plugin.Instance?.DataFolderPath;
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    _persistFile = Path.Combine(dir, "resono-favorites.json");
                    Load();
                    _saveTimer = new Timer(_ => SaveIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Resono] Favorites tracker init failed; running in-memory only");
            }
        }

        public bool AddFavorite(Guid userId, Guid itemId, string kind = "track")
        {
            if (userId == Guid.Empty || itemId == Guid.Empty) return false;
            var list = _byUser.GetOrAdd(userId, _ => new List<FavoriteItem>());
            bool added = false;
            lock (list)
            {
                if (!list.Any(f => f.ItemId == itemId))
                {
                    list.Insert(0, new FavoriteItem { ItemId = itemId, Kind = kind, AddedAt = DateTime.UtcNow });
                    if (list.Count > MaxPerUser) list.RemoveRange(MaxPerUser, list.Count - MaxPerUser);
                    added = true;
                }
            }
            if (added) ScheduleSave();
            return added;
        }

        public bool RemoveFavorite(Guid userId, Guid itemId)
        {
            if (userId == Guid.Empty || itemId == Guid.Empty) return false;
            if (!_byUser.TryGetValue(userId, out var list)) return false;
            bool removed = false;
            lock (list)
            {
                removed = list.RemoveAll(f => f.ItemId == itemId) > 0;
            }
            if (removed) ScheduleSave();
            return removed;
        }

        public bool IsFavorite(Guid userId, Guid itemId)
        {
            if (userId == Guid.Empty || itemId == Guid.Empty) return false;
            if (!_byUser.TryGetValue(userId, out var list)) return false;
            lock (list)
            {
                return list.Any(f => f.ItemId == itemId);
            }
        }

        public IReadOnlyList<FavoriteItem> GetFavorites(Guid userId, string? kind = null)
        {
            if (userId == Guid.Empty)
            {
                var combined = new List<FavoriteItem>();
                foreach (var kv in _byUser)
                {
                    lock (kv.Value)
                    {
                        combined.AddRange(kv.Value);
                    }
                }
                var query = combined.AsEnumerable();
                if (!string.IsNullOrEmpty(kind))
                {
                    query = query.Where(f => string.Equals(f.Kind, kind, StringComparison.OrdinalIgnoreCase));
                }
                return query.OrderByDescending(f => f.AddedAt).DistinctBy(f => f.ItemId).ToList();
            }

            if (!_byUser.TryGetValue(userId, out var list)) return Array.Empty<FavoriteItem>();
            lock (list)
            {
                var query = list.AsEnumerable();
                if (!string.IsNullOrEmpty(kind))
                {
                    query = query.Where(f => string.Equals(f.Kind, kind, StringComparison.OrdinalIgnoreCase));
                }
                return query.OrderByDescending(f => f.AddedAt).ToList();
            }
        }

        private void Load()
        {
            if (_persistFile is null || !File.Exists(_persistFile)) return;
            try
            {
                var json = File.ReadAllText(_persistFile);
                if (string.IsNullOrWhiteSpace(json)) return;
                var loaded = JsonSerializer.Deserialize<Dictionary<Guid, List<FavoriteItem>>>(json, JsonOpts);
                if (loaded is null) return;
                foreach (var kv in loaded) _byUser[kv.Key] = kv.Value;
                _log.LogInformation("[Resono] Loaded {N} users' favorites from disk", loaded.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Resono] Failed to read favorites file '{P}'", _persistFile);
            }
        }

        private void ScheduleSave()
        {
            Interlocked.Exchange(ref _dirty, 1);
            try
            {
                _saveTimer?.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) { }
        }

        private void SaveIfDirty()
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
            if (_persistFile is null) return;

            lock (_saveLock)
            {
                try
                {
                    var snapshot = new Dictionary<Guid, List<FavoriteItem>>();
                    foreach (var kv in _byUser)
                    {
                        lock (kv.Value)
                        {
                            snapshot[kv.Key] = new List<FavoriteItem>(kv.Value);
                        }
                    }

                    var temp = _persistFile + ".tmp";
                    var json = JsonSerializer.Serialize(snapshot, JsonOpts);
                    File.WriteAllText(temp, json);
                    File.Move(temp, _persistFile, overwrite: true);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[Resono] Failed to persist favorites to '{P}'", _persistFile);
                    Interlocked.Exchange(ref _dirty, 1);
                }
            }
        }
    }
}
