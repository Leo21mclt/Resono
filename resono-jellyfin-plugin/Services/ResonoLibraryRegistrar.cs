using System;
using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Resono.Plugin.Services
{
    public class ResonoLibraryRegistrar
    {
        private readonly ILibraryManager _lib;
        private readonly ILogger<ResonoLibraryRegistrar> _log;
        private static readonly ConcurrentDictionary<Guid, string> _registered = new();

        public ResonoLibraryRegistrar(ILibraryManager lib, ILogger<ResonoLibraryRegistrar> log)
        {
            _lib = lib;
            _log = log;
        }

        public bool RegisterTrack(Guid id, ResonoItemCache.Entry entry)
        {
            if (entry.Kind != "track") return false;

            var fingerprint = $"{entry.Name}|{entry.ArtistName}|{entry.AlbumName}|{entry.DurationMs}";
            if (_registered.TryGetValue(id, out var prev) && prev == fingerprint)
                return false;

            try
            {
                var audio = new Audio
                {
                    Id = id,
                    Name = entry.Name ?? "(unknown track)",
                    Album = entry.AlbumName,
                    Artists = !string.IsNullOrEmpty(entry.ArtistName) ? new[] { entry.ArtistName } : Array.Empty<string>(),
                    AlbumArtists = !string.IsNullOrEmpty(entry.ArtistName) ? new[] { entry.ArtistName } : Array.Empty<string>(),
                    IndexNumber = entry.TrackNumber,
                    ParentIndexNumber = entry.DiscNumber,
                    RunTimeTicks = entry.DurationMs.HasValue ? (long)entry.DurationMs.Value * 10000 : null,
                    DateCreated = DateTime.UtcNow,
                    IsVirtualItem = false,
                    Path = $"/data/music/resono/{id:N}.mp3"
                };

                _lib.RegisterItem(audio);
                _registered[id] = fingerprint;
                _log.LogInformation("[Resono] Registered synthetic Audio for track {Id}: {Title} - {Artist}",
                    id, audio.Name, entry.ArtistName ?? "(unknown artist)");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[Resono] RegisterTrack failed for {Id}", id);
                _registered.TryRemove(id, out _);
                return false;
            }
        }
    }
}
