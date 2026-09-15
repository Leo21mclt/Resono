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
                if (entry.AlbumId.HasValue && !string.IsNullOrEmpty(entry.AlbumName))
                {
                    RegisterAlbum(entry.AlbumId.Value, entry.AlbumName, entry.ArtistName);
                }

                if (entry.ArtistId.HasValue && !string.IsNullOrEmpty(entry.ArtistName))
                {
                    RegisterArtist(entry.ArtistId.Value, entry.ArtistName);
                }

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
                    ParentId = entry.AlbumId ?? Guid.Empty,
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

        public bool RegisterAlbum(Guid id, string? albumName, string? artistName)
        {
            if (string.IsNullOrWhiteSpace(albumName)) return false;
            if (_registered.ContainsKey(id)) return false;

            try
            {
                var album = new MusicAlbum
                {
                    Id = id,
                    Name = albumName,
                    Artists = !string.IsNullOrEmpty(artistName) ? new[] { artistName } : Array.Empty<string>(),
                    AlbumArtists = !string.IsNullOrEmpty(artistName) ? new[] { artistName } : Array.Empty<string>(),
                    DateCreated = DateTime.UtcNow,
                    IsVirtualItem = false
                };

                _lib.RegisterItem(album);
                _registered[id] = albumName;
                _log.LogInformation("[Resono] Registered synthetic MusicAlbum {Id}: {Album} - {Artist}",
                    id, albumName, artistName ?? "(unknown artist)");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[Resono] RegisterAlbum failed for {Id}", id);
                return false;
            }
        }

        public bool RegisterArtist(Guid id, string? artistName)
        {
            if (string.IsNullOrWhiteSpace(artistName)) return false;
            if (_registered.ContainsKey(id)) return false;

            try
            {
                var artist = new MusicArtist
                {
                    Id = id,
                    Name = artistName,
                    DateCreated = DateTime.UtcNow,
                    IsVirtualItem = false
                };

                _lib.RegisterItem(artist);
                _registered[id] = artistName;
                _log.LogInformation("[Resono] Registered synthetic MusicArtist {Id}: {Artist}", id, artistName);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[Resono] RegisterArtist failed for {Id}", id);
                return false;
            }
        }
    }
}
