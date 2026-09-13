using System;
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Resono.Plugin.Services;

namespace Resono.Plugin.Filters
{
    public class ResonoItemDetailActionFilter : IAsyncActionFilter, IAsyncResultFilter
    {
        private readonly ResonoItemCache _cache;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ResonoItemDetailActionFilter> _logger;

        public ResonoItemDetailActionFilter(
            ResonoItemCache cache,
            IHttpClientFactory httpClientFactory,
            ILogger<ResonoItemDetailActionFilter> logger)
        {
            _cache = cache;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
        {
            var req = ctx.HttpContext.Request;
            var path = req.Path.Value ?? "";

            // 1. Single-Item Detail (/Items/{id}, /Users/{u}/Items/{id}, /Artists/{id})
            if (TryExtractSingleItemId(ctx, out var singleId) && _cache.TryGet(singleId, out var singleEntry) && singleEntry != null)
            {
                BaseItemDto dto = singleEntry.Kind switch
                {
                    "artist" => ResonoSearchActionFilter.BuildArtistDto(singleId, singleEntry),
                    "album" => ResonoSearchActionFilter.BuildAlbumDto(singleId, singleEntry),
                    _ => ResonoSearchActionFilter.BuildTrackDto(singleId, singleEntry)
                };
                ctx.Result = new OkObjectResult(dto);
                return;
            }

            // 2. Handle /Items?ParentId={virtualAlbumId} (Album tracklist)
            if (path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase) && req.Query.TryGetValue("ParentId", out var pIdVal))
            {
                if (Guid.TryParse(pIdVal.ToString(), out var parentId) && _cache.TryGet(parentId, out var parentEntry))
                {
                    if (parentEntry is { Kind: "album" })
                    {
                        var tracks = await FetchAlbumTracksAsync(parentEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                        if (tracks != null && tracks.Count > 0)
                        {
                            ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                            {
                                Items = tracks.ToArray(),
                                TotalRecordCount = tracks.Count
                            });
                            return;
                        }
                    }
                }
            }

            // 3. Handle Artist Detail Page (/Items?albumArtistIds=..., /Items?artistIds=..., /Items?contributingArtistIds=...)
            if (TryExtractGuidListFromQuery(req, "albumArtistIds", out var artistIds)
                || TryExtractGuidListFromQuery(req, "artistIds", out artistIds)
                || TryExtractGuidListFromQuery(req, "contributingArtistIds", out artistIds))
            {
                foreach (var artistGuid in artistIds)
                {
                    if (_cache.TryGet(artistGuid, out var artistEntry) && artistEntry is { Kind: "artist" })
                    {
                        var types = ExtractIncludeItemTypes(req);
                        bool wantsAlbum = types.Count == 0 || types.Contains("MusicAlbum");
                        bool wantsAudio = types.Contains("Audio");

                        if (wantsAlbum)
                        {
                            var albums = await FetchArtistAlbumsAsync(artistEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                            ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                            {
                                Items = albums.ToArray(),
                                TotalRecordCount = albums.Count
                            });
                            return;
                        }
                        if (wantsAudio)
                        {
                            var tracks = await FetchArtistTracksAsync(artistEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                            ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                            {
                                Items = tracks.ToArray(),
                                TotalRecordCount = tracks.Count
                            });
                            return;
                        }

                        ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                        {
                            Items = Array.Empty<BaseItemDto>(),
                            TotalRecordCount = 0
                        });
                        return;
                    }
                }
            }

            await next().ConfigureAwait(false);
        }

        public async Task OnResultExecutionAsync(ResultExecutingContext ctx, ResultExecutionDelegate next)
        {
            if (ctx.Result is NotFoundResult or NotFoundObjectResult || (ctx.Result is StatusCodeResult sc && sc.StatusCode == 404))
            {
                var route = ctx.RouteData.Values;
                string? idStr = null;
                foreach (var key in new[] { "itemId", "ItemId", "id", "Id" })
                {
                    if (route.TryGetValue(key, out var v) && v is not null)
                    {
                        idStr = v.ToString();
                        break;
                    }
                }

                if (idStr is not null && Guid.TryParse(idStr, out var itemId))
                {
                    if (_cache.TryGet(itemId, out var entry) && entry != null)
                    {
                        BaseItemDto dto = entry.Kind switch
                        {
                            "artist" => ResonoSearchActionFilter.BuildArtistDto(itemId, entry),
                            "album" => ResonoSearchActionFilter.BuildAlbumDto(itemId, entry),
                            _ => ResonoSearchActionFilter.BuildTrackDto(itemId, entry)
                        };
                        ctx.Result = new OkObjectResult(dto);
                    }
                }
            }

            await next().ConfigureAwait(false);
        }

        private static bool TryExtractSingleItemId(ActionExecutingContext ctx, out Guid id)
        {
            id = default;
            var route = ctx.RouteData.Values;
            var controller = route.TryGetValue("controller", out var c) ? c?.ToString() : null;
            if (!string.Equals(controller, "Items", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(controller, "UserLibrary", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(controller, "Artists", StringComparison.OrdinalIgnoreCase))
                return false;

            object? rv = null;
            foreach (var k in new[] { "itemId", "ItemId", "id", "Id" })
            {
                if (route.TryGetValue(k, out rv) && rv != null) break;
            }

            if (rv is null || !Guid.TryParse(rv.ToString(), out id)) return false;

            var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
            var idStr = id.ToString("N");
            var idWithDashes = id.ToString();
            if (!path.EndsWith(idStr, StringComparison.OrdinalIgnoreCase) && !path.EndsWith(idWithDashes, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static bool TryExtractGuidListFromQuery(HttpRequest req, string key, out List<Guid> ids)
        {
            ids = new List<Guid>();
            foreach (var k in new[] { key, char.ToUpperInvariant(key[0]) + key[1..] })
            {
                if (req.Query.TryGetValue(k, out var qs))
                {
                    foreach (var part in qs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (Guid.TryParse(part, out var g)) ids.Add(g);
                }
            }
            return ids.Count > 0;
        }

        private static HashSet<string> ExtractIncludeItemTypes(HttpRequest req)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "includeItemTypes", "IncludeItemTypes" })
            {
                if (req.Query.TryGetValue(key, out var vals))
                {
                    foreach (var v in vals.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        set.Add(v);
                }
            }
            return set;
        }

        private async Task<List<BaseItemDto>> FetchArtistAlbumsAsync(ResonoItemCache.Entry artistEntry, CancellationToken ct)
        {
            var list = new List<BaseItemDto>();
            if (string.IsNullOrWhiteSpace(artistEntry.Name)) return list;

            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = cfg.GatewayUrl?.TrimEnd('/') ?? "http://localhost:8080";
                var provider = !string.IsNullOrWhiteSpace(cfg.CatalogProvider) ? cfg.CatalogProvider : "apple";
                var fallback = !string.IsNullOrWhiteSpace(cfg.FallbackCatalogProvider) ? cfg.FallbackCatalogProvider : "deezer";
                var url = $"{gatewayUrl}/jellyfin/search?q={Uri.EscapeDataString(artistEntry.Name)}&limit=30&provider={Uri.EscapeDataString(provider)}&fallback={Uri.EscapeDataString(fallback)}";

                var client = _httpClientFactory.CreateClient();
                var searchData = await client.GetFromJsonAsync<GatewaySearchResponse>(url, ct).ConfigureAwait(false);
                if (searchData?.Albums != null)
                {
                    foreach (var al in searchData.Albums)
                    {
                        var id = ResonoItemCache.DeterministicGuid(al.Id ?? al.Name ?? Guid.NewGuid().ToString());
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "album",
                            Name = al.Name,
                            ArtistName = al.ArtistName ?? artistEntry.Name,
                            SpotifyId = al.Id,
                            ImageUrl = al.ImageUrl,
                            ArtistId = artistEntry.Id
                        };
                        _cache.Set(id, entry);
                        list.Add(ResonoSearchActionFilter.BuildAlbumDto(id, entry));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch artist albums for '{Artist}': {Message}", artistEntry.Name, ex.Message);
            }

            return list;
        }

        private async Task<List<BaseItemDto>> FetchArtistTracksAsync(ResonoItemCache.Entry artistEntry, CancellationToken ct)
        {
            var list = new List<BaseItemDto>();
            if (string.IsNullOrWhiteSpace(artistEntry.Name)) return list;

            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = cfg.GatewayUrl?.TrimEnd('/') ?? "http://localhost:8080";
                var provider = !string.IsNullOrWhiteSpace(cfg.CatalogProvider) ? cfg.CatalogProvider : "apple";
                var fallback = !string.IsNullOrWhiteSpace(cfg.FallbackCatalogProvider) ? cfg.FallbackCatalogProvider : "deezer";
                var url = $"{gatewayUrl}/jellyfin/search?q={Uri.EscapeDataString(artistEntry.Name)}&limit=30&provider={Uri.EscapeDataString(provider)}&fallback={Uri.EscapeDataString(fallback)}";

                var client = _httpClientFactory.CreateClient();
                var searchData = await client.GetFromJsonAsync<GatewaySearchResponse>(url, ct).ConfigureAwait(false);
                if (searchData?.Tracks != null)
                {
                    foreach (var t in searchData.Tracks)
                    {
                        var trackId = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                            ? g : ResonoItemCache.DeterministicGuid(t.Id ?? t.Name ?? Guid.NewGuid().ToString());

                        var streamUrl = !string.IsNullOrEmpty(t.StreamUrl)
                            ? $"{gatewayUrl}{t.StreamUrl}"
                            : $"{gatewayUrl}/playback/{t.Id}";

                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "track",
                            Name = t.Name,
                            ArtistName = t.ArtistName ?? artistEntry.Name,
                            AlbumName = t.AlbumName,
                            SpotifyId = t.Id,
                            ImageUrl = t.ImageUrl ?? artistEntry.ImageUrl,
                            DurationMs = t.DurationMs,
                            TrackNumber = t.TrackNumber,
                            DiscNumber = t.DiscNumber,
                            StreamUrl = streamUrl,
                            ArtistId = artistEntry.Id
                        };
                        _cache.Set(trackId, entry);
                        list.Add(ResonoSearchActionFilter.BuildTrackDto(trackId, entry));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch artist tracks for '{Artist}': {Message}", artistEntry.Name, ex.Message);
            }

            return list;
        }

        private async Task<List<BaseItemDto>?> FetchAlbumTracksAsync(ResonoItemCache.Entry albumEntry, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(albumEntry.SpotifyId)) return null;

            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = cfg.GatewayUrl?.TrimEnd('/') ?? "http://localhost:8080";
                var url = $"{gatewayUrl}/jellyfin/album/{Uri.EscapeDataString(albumEntry.SpotifyId)}";

                var client = _httpClientFactory.CreateClient();
                var albumData = await client.GetFromJsonAsync<GatewayAlbumResponse>(url, ct).ConfigureAwait(false);
                if (albumData?.Tracks == null) return null;

                var result = new List<BaseItemDto>();
                foreach (var t in albumData.Tracks)
                {
                    var trackId = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                        ? g : ResonoItemCache.DeterministicGuid(t.Id ?? t.Name ?? Guid.NewGuid().ToString());

                    var streamUrl = !string.IsNullOrEmpty(t.StreamUrl)
                        ? $"{gatewayUrl}{t.StreamUrl}"
                        : $"{gatewayUrl}/playback/{t.Id}";

                    var entry = new ResonoItemCache.Entry
                    {
                        Kind = "track",
                        Name = t.Name,
                        ArtistName = t.ArtistName ?? albumEntry.ArtistName,
                        AlbumName = albumEntry.Name,
                        SpotifyId = t.Id,
                        ImageUrl = t.ImageUrl ?? albumEntry.ImageUrl,
                        DurationMs = t.DurationMs,
                        TrackNumber = t.TrackNumber,
                        DiscNumber = t.DiscNumber,
                        StreamUrl = streamUrl,
                        AlbumId = albumEntry.Id,
                        ArtistId = albumEntry.ArtistId
                    };
                    _cache.Set(trackId, entry);
                    result.Add(ResonoSearchActionFilter.BuildTrackDto(trackId, entry));
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch album tracks from Resono Gateway: {Message}", ex.Message);
                return null;
            }
        }
    }

    public class GatewayAlbumResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("tracks")]
        public List<GatewayTrack>? Tracks { get; set; }
    }
}
