using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Resono.Plugin.Services;

namespace Resono.Plugin.Filters
{
    public class ResonoSearchActionFilter : IAsyncResultFilter
    {
        private static readonly ConcurrentDictionary<string, (DateTime Expires, GatewaySearchResponse Data)> _searchMemoryCache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> MusicTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Audio", "MusicAlbum", "MusicArtist", "AudioBook",
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ResonoItemCache _cache;
        private readonly ILogger<ResonoSearchActionFilter> _logger;

        public ResonoSearchActionFilter(
            IHttpClientFactory httpClientFactory,
            ResonoItemCache cache,
            ILogger<ResonoSearchActionFilter> logger)
        {
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _logger = logger;
        }

        public async Task OnResultExecutionAsync(ResultExecutingContext ctx, ResultExecutionDelegate next)
        {
            try
            {
                if (ShouldInject(ctx, out var searchTerm))
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.HttpContext.RequestAborted);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    await TryAugmentSearchAsync(ctx, searchTerm!, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resono search response augmentation failed: {Message}", ex.Message);
            }

            await next().ConfigureAwait(false);
        }

        private bool ShouldInject(ResultExecutingContext ctx, out string? term)
        {
            term = null;
            var cfg = Plugin.Instance?.Configuration;
            if (cfg is null || !cfg.EnableSearchInjection) return false;

            var controller = ctx.RouteData.Values.TryGetValue("controller", out var c) ? c?.ToString() : null;
            if (!string.Equals(controller, "Items", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(controller, "Search", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(controller, "Artists", StringComparison.OrdinalIgnoreCase))
                return false;

            term = ExtractSearchTerm(ctx.HttpContext);
            if (string.IsNullOrWhiteSpace(term) || term.Length < 2) return false;

            if (string.Equals(controller, "Artists", StringComparison.OrdinalIgnoreCase))
                return true;

            var types = ExtractIncludeItemTypes(ctx.HttpContext);
            if (types.Count > 0 && !types.Any(t => MusicTypes.Contains(t))) return false;

            return true;
        }

        private static string? ExtractSearchTerm(HttpContext http)
        {
            var q = http.Request.Query;
            if (q.TryGetValue("searchTerm", out var st) && !string.IsNullOrWhiteSpace(st)) return st.ToString();
            if (q.TryGetValue("SearchTerm", out var st2) && !string.IsNullOrWhiteSpace(st2)) return st2.ToString();
            if (q.TryGetValue("nameStartsWithOrGreater", out var sw) && !string.IsNullOrWhiteSpace(sw)) return sw.ToString();
            return null;
        }

        private static HashSet<string> ExtractIncludeItemTypes(HttpContext http)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var q = http.Request.Query;
            foreach (var key in new[] { "includeItemTypes", "IncludeItemTypes" })
            {
                if (q.TryGetValue(key, out var vals))
                {
                    foreach (var v in vals.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        set.Add(v);
                }
            }
            return set;
        }

        private async Task TryAugmentSearchAsync(ResultExecutingContext ctx, string term, CancellationToken ct)
        {
            if (ctx.Result is not ObjectResult or || or.Value is null) return;

            var cfg = Plugin.Instance!.Configuration;
            var gatewayUrl = cfg.GatewayUrl?.TrimEnd('/') ?? "http://localhost:8080";
            var provider = !string.IsNullOrWhiteSpace(cfg.CatalogProvider) ? cfg.CatalogProvider : "apple";
            var fallback = !string.IsNullOrWhiteSpace(cfg.FallbackCatalogProvider) ? cfg.FallbackCatalogProvider : "deezer";
            var limit = cfg.SearchLimit > 0 ? cfg.SearchLimit : 20;

            GatewaySearchResponse? searchData = null;
            var cacheKey = $"{provider}:{fallback}:{limit}:{term.Trim().ToLowerInvariant()}";

            if (cfg.EnableSearchCache && _searchMemoryCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expires)
            {
                searchData = cached.Data;
            }
            else
            {
                var url = $"{gatewayUrl}/jellyfin/search?q={Uri.EscapeDataString(term)}&limit={limit}&provider={Uri.EscapeDataString(provider)}&fallback={Uri.EscapeDataString(fallback)}";
                var client = _httpClientFactory.CreateClient();
                searchData = await client.GetFromJsonAsync<GatewaySearchResponse>(url, ct).ConfigureAwait(false);
                if (searchData != null && cfg.EnableSearchCache)
                {
                    _searchMemoryCache[cacheKey] = (DateTime.UtcNow.AddMinutes(15), searchData);
                }
            }

            if (searchData is null) return;

            switch (or.Value)
            {
                case QueryResult<BaseItemDto> qr:
                    AugmentItems(qr, searchData, gatewayUrl);
                    break;
                case SearchHintResult sr:
                    or.Value = AugmentHints(sr, searchData);
                    break;
            }
        }

        private void AugmentItems(QueryResult<BaseItemDto> qr, GatewaySearchResponse data, string gatewayUrl)
        {
            var existingIds = qr.Items.Select(i => i.Id).ToHashSet();
            var additions = new List<BaseItemDto>();

            // 1. Add Artists
            if (data.Artists != null)
            {
                foreach (var a in data.Artists)
                {
                    var id = ResonoItemCache.DeterministicGuid(a.Id ?? a.Name ?? Guid.NewGuid().ToString());
                    if (existingIds.Add(id))
                    {
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "artist",
                            Name = a.Name,
                            SpotifyId = a.Id,
                            ImageUrl = a.ImageUrl
                        };
                        _cache.Set(id, entry);
                        additions.Add(BuildArtistDto(id, entry));
                    }
                }
            }

            // 2. Add Albums
            if (data.Albums != null)
            {
                foreach (var al in data.Albums)
                {
                    var id = ResonoItemCache.DeterministicGuid(al.Id ?? al.Name ?? Guid.NewGuid().ToString());
                    var artistId = !string.IsNullOrEmpty(al.ArtistId) ? ResonoItemCache.DeterministicGuid(al.ArtistId) : (Guid?)null;
                    if (existingIds.Add(id))
                    {
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "album",
                            Name = al.Name,
                            ArtistName = al.ArtistName,
                            SpotifyId = al.Id,
                            ImageUrl = al.ImageUrl,
                            ArtistId = artistId
                        };
                        _cache.Set(id, entry);
                        additions.Add(BuildAlbumDto(id, entry));
                    }
                }
            }

            // 3. Add Tracks
            if (data.Tracks != null)
            {
                foreach (var t in data.Tracks)
                {
                    var id = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                        ? g : ResonoItemCache.DeterministicGuid(t.Id ?? t.Name ?? Guid.NewGuid().ToString());

                    var albumId = !string.IsNullOrEmpty(t.AlbumName)
                        ? ResonoItemCache.DeterministicGuid("spotify:album:" + (t.AlbumName + (t.ArtistName ?? "")))
                        : (Guid?)null;

                    var artistId = !string.IsNullOrEmpty(t.ArtistName)
                        ? ResonoItemCache.DeterministicGuid("spotify:artist:" + t.ArtistName)
                        : (Guid?)null;

                    if (existingIds.Add(id))
                    {
                        var streamUrl = !string.IsNullOrEmpty(t.StreamUrl)
                            ? $"{gatewayUrl}{t.StreamUrl}"
                            : $"{gatewayUrl}/playback/{t.Id}";

                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "track",
                            Name = t.Name,
                            ArtistName = t.ArtistName,
                            AlbumName = t.AlbumName,
                            SpotifyId = t.Id,
                            ImageUrl = t.ImageUrl,
                            DurationMs = t.DurationMs,
                            TrackNumber = t.TrackNumber,
                            DiscNumber = t.DiscNumber,
                            StreamUrl = streamUrl,
                            AlbumId = albumId,
                            ArtistId = artistId
                        };
                        _cache.Set(id, entry);
                        additions.Add(BuildTrackDto(id, entry));
                    }
                }
            }

            if (additions.Count > 0)
            {
                var combined = qr.Items.Concat(additions).ToArray();
                qr.Items = combined;
                qr.TotalRecordCount = combined.Length;
            }
        }

        private SearchHintResult AugmentHints(SearchHintResult sr, GatewaySearchResponse data)
        {
            var existingIds = (sr.SearchHints ?? Array.Empty<SearchHint>()).Select(h => h.Id).ToHashSet();
            var additions = new List<SearchHint>();

            if (data.Tracks != null)
            {
                foreach (var t in data.Tracks)
                {
                    var id = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                        ? g : ResonoItemCache.DeterministicGuid(t.Id ?? t.Name ?? Guid.NewGuid().ToString());

                    if (existingIds.Add(id))
                    {
                        additions.Add(new SearchHint
                        {
                            Id = id,
                            Name = t.Name,
                            Type = BaseItemKind.Audio,
                            Artists = !string.IsNullOrEmpty(t.ArtistName) ? new[] { t.ArtistName } : Array.Empty<string>(),
                            Album = t.AlbumName,
                            PrimaryImageTag = "resono-" + id.ToString("N"),
                            RunTimeTicks = (long)t.DurationMs * 10000,
                            IndexNumber = t.TrackNumber,
                        });
                    }
                }
            }

            if (additions.Count == 0) return sr;

            var combined = (sr.SearchHints ?? Array.Empty<SearchHint>()).Concat(additions).ToArray();
            return new SearchHintResult(combined, combined.Length);
        }

        public static BaseItemDto BuildArtistDto(Guid id, ResonoItemCache.Entry e)
        {
            var imageTag = "resono-" + id.ToString("N");
            return new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.Name ?? "(unknown artist)",
                Type = BaseItemKind.MusicArtist,
                MediaType = MediaType.Unknown,
                Tags = new[] { "ResonoVirtual" },
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, imageTag } },
                ImageBlurHashes = new Dictionary<ImageType, Dictionary<string, string>> { { ImageType.Primary, new() } },
                PrimaryImageAspectRatio = 1.0,
                IsFolder = true,
            };
        }

        public static BaseItemDto BuildAlbumDto(Guid id, ResonoItemCache.Entry e)
        {
            var imageTag = "resono-" + id.ToString("N");
            NameGuidPair[]? artistPair = null;
            if (!string.IsNullOrEmpty(e.ArtistName))
            {
                artistPair = new[] { new NameGuidPair { Name = e.ArtistName, Id = e.ArtistId ?? ResonoItemCache.DeterministicGuid("artist:" + e.ArtistName) } };
            }

            return new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.Name ?? "(unknown album)",
                Type = BaseItemKind.MusicAlbum,
                MediaType = MediaType.Unknown,
                Tags = new[] { "ResonoVirtual" },
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, imageTag } },
                ImageBlurHashes = new Dictionary<ImageType, Dictionary<string, string>> { { ImageType.Primary, new() } },
                PrimaryImageAspectRatio = 1.0,
                Artists = !string.IsNullOrEmpty(e.ArtistName) ? new[] { e.ArtistName } : null,
                AlbumArtist = e.ArtistName,
                AlbumArtists = artistPair ?? Array.Empty<NameGuidPair>(),
                ArtistItems = artistPair ?? Array.Empty<NameGuidPair>(),
                IsFolder = true,
            };
        }

        public static BaseItemDto BuildTrackDto(Guid id, ResonoItemCache.Entry e)
        {
            var imageTag = "resono-" + id.ToString("N");
            var albumImageTag = e.AlbumId.HasValue ? "resono-" + e.AlbumId.Value.ToString("N") : imageTag;

            NameGuidPair[]? artistPair = null;
            if (!string.IsNullOrEmpty(e.ArtistName))
            {
                artistPair = new[] { new NameGuidPair { Name = e.ArtistName, Id = e.ArtistId ?? ResonoItemCache.DeterministicGuid("artist:" + e.ArtistName) } };
            }

            var mediaSource = new MediaSourceInfo
            {
                Id = id.ToString("N"),
                Path = e.StreamUrl ?? "",
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                Name = e.Name ?? "Track",
                Container = "flac",
                SupportsTranscoding = true,
                SupportsDirectStream = true,
                SupportsDirectPlay = true,
                RunTimeTicks = e.DurationMs.HasValue ? (long)e.DurationMs.Value * 10000 : null,
                MediaStreams = new List<MediaBrowser.Model.Entities.MediaStream>(),
                RequiredHttpHeaders = new Dictionary<string, string>(),
            };

            return new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.Name ?? "(unknown track)",
                Type = BaseItemKind.Audio,
                MediaType = MediaType.Audio,
                Tags = new[] { "ResonoVirtual" },
                AlbumPrimaryImageTag = albumImageTag,
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, imageTag } },
                ImageBlurHashes = new Dictionary<ImageType, Dictionary<string, string>> { { ImageType.Primary, new() } },
                PrimaryImageAspectRatio = 1.0,
                AlbumId = e.AlbumId,
                ParentId = e.AlbumId,
                Artists = !string.IsNullOrEmpty(e.ArtistName) ? new[] { e.ArtistName } : null,
                ArtistItems = artistPair ?? Array.Empty<NameGuidPair>(),
                AlbumArtists = artistPair ?? Array.Empty<NameGuidPair>(),
                Album = e.AlbumName,
                AlbumArtist = e.ArtistName,
                IndexNumber = e.TrackNumber,
                ParentIndexNumber = e.DiscNumber,
                RunTimeTicks = e.DurationMs.HasValue ? (long)e.DurationMs.Value * 10000 : null,
                IsFolder = false,
                CanDownload = false,
                LocationType = LocationType.FileSystem,
                MediaSources = new[] { mediaSource },
            };
        }
    }

    public class GatewaySearchResponse
    {
        [JsonPropertyName("artists")]
        public List<GatewayArtist>? Artists { get; set; }

        [JsonPropertyName("albums")]
        public List<GatewayAlbum>? Albums { get; set; }

        [JsonPropertyName("tracks")]
        public List<GatewayTrack>? Tracks { get; set; }
    }

    public class GatewayArtist
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }
    }

    public class GatewayAlbum
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }
        [JsonPropertyName("artistId")]
        public string? ArtistId { get; set; }
        [JsonPropertyName("releaseDate")]
        public string? ReleaseDate { get; set; }
        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }
    }

    public class GatewayTrack
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        [JsonPropertyName("canonicalId")]
        public string? CanonicalId { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }
        [JsonPropertyName("albumName")]
        public string? AlbumName { get; set; }
        [JsonPropertyName("durationMs")]
        public int DurationMs { get; set; }
        [JsonPropertyName("trackNumber")]
        public int TrackNumber { get; set; }
        [JsonPropertyName("discNumber")]
        public int DiscNumber { get; set; }
        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }
        [JsonPropertyName("streamUrl")]
        public string? StreamUrl { get; set; }
    }
}
