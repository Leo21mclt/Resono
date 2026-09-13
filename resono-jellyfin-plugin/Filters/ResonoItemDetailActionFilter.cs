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

            // Handle /Items?ParentId={virtualAlbumId}
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

            await next().ConfigureAwait(false);
        }

        public async Task OnResultExecutionAsync(ResultExecutingContext ctx, ResultExecutionDelegate next)
        {
            // If Jellyfin returned 404 for /Users/{u}/Items/{id} or /Items/{id}
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
