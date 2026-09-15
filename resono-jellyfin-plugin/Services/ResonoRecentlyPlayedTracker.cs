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
    public class ResonoRecentlyPlayedTracker
    {
        public class Play
        {
            public Guid TrackId { get; set; }
            public Guid? AlbumId { get; set; }
            public DateTime PlayedAt { get; set; }
        }

        private const int MaxPerUser = 50;
        private readonly ConcurrentDictionary<Guid, List<Play>> _byUser = new();
        private readonly ILogger<ResonoRecentlyPlayedTracker> _log;
        private readonly string? _persistFile;
        private readonly Timer? _saveTimer;
        private readonly object _saveLock = new();
        private int _dirty;
        private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);

        public ResonoRecentlyPlayedTracker(ILogger<ResonoRecentlyPlayedTracker> log)
        {
            _log = log;
            try
            {
                var dir = Plugin.Instance?.DataFolderPath;
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    _persistFile = Path.Combine(dir, "resono-recently-played.json");
                    Load();
                    _saveTimer = new Timer(_ => SaveIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Resono] Recently played init failed; running in-memory only");
            }
        }

        public void RecordPlay(Guid userId, Guid trackId, Guid? albumId)
        {
            if (userId == Guid.Empty) return;
            var list = _byUser.GetOrAdd(userId, _ => new List<Play>());
            lock (list)
            {
                list.RemoveAll(p => p.TrackId == trackId);
                list.Insert(0, new Play { TrackId = trackId, AlbumId = albumId, PlayedAt = DateTime.UtcNow });
                if (list.Count > MaxPerUser) list.RemoveRange(MaxPerUser, list.Count - MaxPerUser);
            }
            ScheduleSave();
        }

        public IReadOnlyList<(Guid TrackId, Guid? AlbumId, DateTime PlayedAt)> GetRecent(Guid userId, int limit)
        {
            if (!_byUser.TryGetValue(userId, out var list)) return Array.Empty<(Guid, Guid?, DateTime)>();
            lock (list)
            {
                return list.Take(limit).Select(p => (p.TrackId, p.AlbumId, p.PlayedAt)).ToArray();
            }
        }

        public IReadOnlyList<(Guid TrackId, Guid? AlbumId, DateTime PlayedAt)> GetAllRecent(int limit)
        {
            var combined = new List<Play>();
            foreach (var kv in _byUser)
            {
                lock (kv.Value)
                {
                    combined.AddRange(kv.Value);
                }
            }
            return combined
                .OrderByDescending(p => p.PlayedAt)
                .Take(limit)
                .Select(p => (p.TrackId, p.AlbumId, p.PlayedAt))
                .ToArray();
        }

        private void Load()
        {
            if (_persistFile is null || !File.Exists(_persistFile)) return;
            try
            {
                var json = File.ReadAllText(_persistFile);
                if (string.IsNullOrWhiteSpace(json)) return;
                var loaded = JsonSerializer.Deserialize<Dictionary<Guid, List<Play>>>(json, JsonOpts);
                if (loaded is null) return;
                foreach (var kv in loaded) _byUser[kv.Key] = kv.Value;
                _log.LogInformation("[Resono] Loaded {N} users' recent-plays from disk", loaded.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Resono] Load recent plays failed; starting fresh");
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
                    var snapshot = _byUser.ToDictionary(kv => kv.Key, kv => {
                        lock (kv.Value) { return kv.Value.ToList(); }
                    });
                    var tmp = _persistFile + ".tmp";
                    using (var fs = File.Create(tmp))
                    {
                        JsonSerializer.Serialize(fs, snapshot, JsonOpts);
                    }
                    File.Move(tmp, _persistFile, overwrite: true);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[Resono] Save recent plays failed");
                }
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };
    }
}
