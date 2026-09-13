using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Lyrics;
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

            // 0. PlaybackInfo (/Items/{id}/PlaybackInfo)
            if (path.IndexOf("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (TryExtractGuidFromPath(path, out var playId) && _cache.TryGet(playId, out var playEntry) && playEntry != null)
                {
                    var ms = ResonoSearchActionFilter.BuildMediaSource(playId, playEntry);
                    ctx.Result = new OkObjectResult(new
                    {
                        MediaSources = new[] { ms },
                        PlaySessionId = Guid.NewGuid().ToString("N")
                    });
                    return;
                }
            }

            // 0.1 Audio Stream / Universal Redirection (/Audio/{id}/...)
            if (path.StartsWith("/Audio/", StringComparison.OrdinalIgnoreCase))
            {
                if (TryExtractGuidFromPath(path, out var streamItemId) && _cache.TryGet(streamItemId, out var streamEntry) && streamEntry != null)
                {
                    // Universal audio endpoint redirection
                    if (path.IndexOf("/universal", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (req.Query.TryGetValue("EnableRedirection", out var redir) && string.Equals(redir.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                        {
                            var target = $"/Audio/{streamItemId:N}/main.mp3{req.QueryString.Value}";
                            ctx.Result = new RedirectResult(target, false);
                            return;
                        }
                    }

                    // Proxy audio stream directly through Jellyfin server
                    var cfg = Plugin.Instance?.Configuration;
                    var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg?.GatewayUrl);
                    var targetUrl = !string.IsNullOrEmpty(streamEntry.StreamUrl) ? streamEntry.StreamUrl : $"{gatewayUrl}/playback/{streamEntry.SpotifyId}";

                    try
                    {
                        var client = _httpClientFactory.CreateClient();
                        var forwardReq = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                        if (req.Headers.TryGetValue("Range", out var rangeVal))
                        {
                            forwardReq.Headers.TryAddWithoutValidation("Range", rangeVal.ToString());
                        }

                        using var forwardResp = await client.SendAsync(forwardReq, HttpCompletionOption.ResponseHeadersRead, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                        var httpResp = ctx.HttpContext.Response;
                        httpResp.StatusCode = (int)forwardResp.StatusCode;
                        httpResp.ContentType = forwardResp.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";

                        if (forwardResp.Content.Headers.ContentLength.HasValue)
                        {
                            httpResp.ContentLength = forwardResp.Content.Headers.ContentLength.Value;
                        }
                        if (forwardResp.Headers.TryGetValues("Accept-Ranges", out var ar))
                        {
                            httpResp.Headers["Accept-Ranges"] = string.Join(",", ar);
                        }
                        if (forwardResp.Content.Headers.TryGetValues("Content-Range", out var cr))
                        {
                            httpResp.Headers["Content-Range"] = string.Join(",", cr);
                        }

                        using var srcStream = await forwardResp.Content.ReadAsStreamAsync(ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                        await srcStream.CopyToAsync(httpResp.Body, 64 * 1024, ctx.HttpContext.RequestAborted).ConfigureAwait(false);

                        ctx.Result = new EmptyResult();
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        ctx.Result = new EmptyResult();
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to proxy audio stream for item {Id}: {Message}", streamItemId, ex.Message);
                    }
                }
            }

            // 1. Synced Karaoke Lyrics (/Audio/{id}/Lyrics or /Items/{id}/Lyrics)
            if (path.IndexOf("/Lyrics", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (TryExtractGuidFromPath(path, out var lyricItemId) && _cache.TryGet(lyricItemId, out var lyricEntry) && lyricEntry is { Kind: "track" })
                {
                    var lyricDto = await FetchSyncedLyricsAsync(lyricEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (lyricDto != null)
                    {
                        ctx.Result = new OkObjectResult(lyricDto);
                        return;
                    }
                }
            }

            // 2. Pin Favorite to Library (/Users/{u}/FavoriteItems/{id})
            if (path.IndexOf("/FavoriteItems/", StringComparison.OrdinalIgnoreCase) >= 0 && HttpMethods.IsPost(req.Method))
            {
                if (TryExtractGuidFromPath(path, out var favItemId) && _cache.TryGet(favItemId, out var favEntry) && favEntry is { Kind: "track" })
                {
                    _ = PinTrackAsync(favEntry.SpotifyId ?? favItemId.ToString());
                }
            }

            // 3. Similar Artists (/Artists/{id}/Similar or /Items/{id}/Similar)
            if (path.IndexOf("/Similar", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (TryExtractGuidFromPath(path, out var simId) && _cache.TryGet(simId, out var simEntry) && simEntry is { Kind: "artist" })
                {
                    var similar = await FetchArtistSimilarAsync(simEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                    ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                    {
                        Items = similar.ToArray(),
                        TotalRecordCount = similar.Count
                    });
                    return;
                }
            }

            // 4. Single-Item Detail (/Items/{id}, /Users/{u}/Items/{id}, /Artists/{id})
            if (TryExtractSingleItemId(ctx, out var singleId) && _cache.TryGet(singleId, out var singleEntry) && singleEntry != null)
            {
                BaseItemDto dto = singleEntry.Kind switch
                {
                    "artist" => ResonoSearchActionFilter.BuildArtistDto(singleId, singleEntry),
                    "album" => ResonoSearchActionFilter.BuildAlbumDto(singleId, singleEntry),
                    "playlist" => BuildPlaylistDto(singleId, singleEntry),
                    _ => ResonoSearchActionFilter.BuildTrackDto(singleId, singleEntry)
                };
                ctx.Result = new OkObjectResult(dto);
                return;
            }

            // 5. Virtual Playlist Tracks (/Playlists/{id}/Items)
            if (path.IndexOf("/Playlists/", StringComparison.OrdinalIgnoreCase) >= 0 && path.TrimEnd('/').EndsWith("/Items", StringComparison.OrdinalIgnoreCase))
            {
                if (TryExtractGuidFromPath(path, out var plId) && _cache.TryGet(plId, out var plEntry) && plEntry is { Kind: "playlist" })
                {
                    var tracks = await FetchChartTracksAsync(plEntry.SpotifyId ?? "global", ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                    ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                    {
                        Items = tracks.ToArray(),
                        TotalRecordCount = tracks.Count
                    });
                    return;
                }
            }

            // 6. Handle /Items queries
            if (path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase))
            {
                // (a) Album Tracklist via AlbumIds parameter (/Items?albumIds={id})
                if (TryExtractGuidListFromQuery(req, "albumIds", out var albIds) || TryExtractGuidListFromQuery(req, "albumId", out albIds))
                {
                    foreach (var albId in albIds)
                    {
                        if (_cache.TryGet(albId, out var albumEntry) && albumEntry is { Kind: "album" })
                        {
                            var tracks = await FetchAlbumTracksAsync(albumEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
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

                // (b) ParentId query (Albums, Artists, or Playlists)
                if ((req.Query.TryGetValue("ParentId", out var pIdVal) || req.Query.TryGetValue("parentId", out pIdVal)) && Guid.TryParse(pIdVal.ToString(), out var parentId))
                {
                    if (_cache.TryGet(parentId, out var parentEntry))
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
                        if (parentEntry is { Kind: "artist" })
                        {
                            var types = ExtractIncludeItemTypes(req);
                            if (types.Contains("MusicAlbum"))
                            {
                                var albums = await FetchArtistAlbumsAsync(parentEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                                ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                                {
                                    Items = albums.ToArray(),
                                    TotalRecordCount = albums.Count
                                });
                                return;
                            }

                            var tracks = await FetchArtistTopTracksAsync(parentEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                            ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                            {
                                Items = tracks.ToArray(),
                                TotalRecordCount = tracks.Count
                            });
                            return;
                        }
                        if (parentEntry is { Kind: "playlist" })
                        {
                            var tracks = await FetchChartTracksAsync(parentEntry.SpotifyId ?? "global", ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                            ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                            {
                                Items = tracks.ToArray(),
                                TotalRecordCount = tracks.Count
                            });
                            return;
                        }
                    }
                }

                // (c) Artist Profile queries (/Items?albumArtistIds=..., /Items?artistIds=..., /Items?contributingArtistIds=...)
                if (TryExtractGuidListFromQuery(req, "albumArtistIds", out var artistIds)
                    || TryExtractGuidListFromQuery(req, "artistIds", out artistIds)
                    || TryExtractGuidListFromQuery(req, "contributingArtistIds", out artistIds)
                    || TryExtractGuidListFromQuery(req, "albumArtistId", out artistIds)
                    || TryExtractGuidListFromQuery(req, "artistId", out artistIds))
                {
                    foreach (var artistGuid in artistIds)
                    {
                        if (_cache.TryGet(artistGuid, out var artistEntry) && artistEntry is { Kind: "artist" })
                        {
                            var types = ExtractIncludeItemTypes(req);
                            bool wantsAlbum = types.Contains("MusicAlbum");
                            bool wantsAudio = types.Contains("Audio") || (!wantsAlbum && types.Count == 0);

                            if (wantsAlbum && wantsAudio)
                            {
                                var albums = await FetchArtistAlbumsAsync(artistEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                                var tracks = await FetchArtistTopTracksAsync(artistEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                                var combined = albums.Concat(tracks).ToArray();
                                ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                                {
                                    Items = combined,
                                    TotalRecordCount = combined.Length
                                });
                                return;
                            }

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
                                var tracks = await FetchArtistTopTracksAsync(artistEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
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
            }

            await next().ConfigureAwait(false);
        }

        public async Task OnResultExecutionAsync(ResultExecutingContext ctx, ResultExecutionDelegate next)
        {
            var req = ctx.HttpContext.Request;
            var path = req.Path.Value ?? string.Empty;

            // Augment Playlists or Suggestions with virtual discovery charts
            var cfg = Plugin.Instance?.Configuration;
            if (cfg != null && cfg.EnableVirtualPlaylists && ctx.Result is ObjectResult or && or.Value is QueryResult<BaseItemDto> qr)
            {
                var types = ExtractIncludeItemTypes(req);
                bool isPlaylistsQuery = types.Contains("Playlist") || path.IndexOf("/Playlists", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isSuggestionsQuery = path.IndexOf("/Suggestions", StringComparison.OrdinalIgnoreCase) >= 0;

                bool hasSearchTerm = req.Query.ContainsKey("searchTerm") || req.Query.ContainsKey("SearchTerm") || req.Query.ContainsKey("nameStartsWithOrGreater");
                if (!hasSearchTerm && (isPlaylistsQuery || isSuggestionsQuery))
                {
                    try
                    {
                        var charts = await FetchChartsAsync(ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                        if (charts.Count > 0)
                        {
                            var existingIds = qr.Items.Select(i => i.Id).ToHashSet();
                            var toAdd = charts.Where(c => existingIds.Add(c.Id)).ToArray();
                            if (toAdd.Length > 0)
                            {
                                qr.Items = qr.Items.Concat(toAdd).ToArray();
                                qr.TotalRecordCount = qr.Items.Count;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to augment discovery charts: {Message}", ex.Message);
                    }
                }
            }

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
                            "playlist" => BuildPlaylistDto(itemId, entry),
                            _ => ResonoSearchActionFilter.BuildTrackDto(itemId, entry)
                        };
                        ctx.Result = new OkObjectResult(dto);
                    }
                }
            }

            await next().ConfigureAwait(false);
        }

        private static bool TryExtractGuidFromPath(string path, out Guid id)
        {
            id = default;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var s in segments)
            {
                var clean = s.IndexOf('.') >= 0 ? s.Split('.')[0] : s;
                if (Guid.TryParse(clean, out id)) return true;
            }
            return false;
        }

        private static bool TryExtractSingleItemId(ActionExecutingContext ctx, out Guid id)
        {
            id = default;
            var route = ctx.RouteData.Values;
            var controller = route.TryGetValue("controller", out var c) ? c?.ToString() : null;
            if (!string.Equals(controller, "Items", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(controller, "UserLibrary", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(controller, "Artists", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(controller, "Playlists", StringComparison.OrdinalIgnoreCase))
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

        public static BaseItemDto BuildPlaylistDto(Guid id, ResonoItemCache.Entry e)
        {
            var imageTag = "resono-" + id.ToString("N");
            return new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.Name ?? "(chart playlist)",
                Type = BaseItemKind.Playlist,
                MediaType = MediaType.Unknown,
                Tags = new[] { "ResonoVirtual" },
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, imageTag } },
                ImageBlurHashes = new Dictionary<ImageType, Dictionary<string, string>> { { ImageType.Primary, new() } },
                PrimaryImageAspectRatio = 1.0,
                IsFolder = true,
            };
        }

        private async Task<List<BaseItemDto>> FetchArtistAlbumsAsync(ResonoItemCache.Entry artistEntry, CancellationToken ct)
        {
            var list = new List<BaseItemDto>();
            if (string.IsNullOrWhiteSpace(artistEntry.Name)) return list;

            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var idParam = !string.IsNullOrEmpty(artistEntry.SpotifyId) ? artistEntry.SpotifyId : artistEntry.Name;
                var url = $"{gatewayUrl}/jellyfin/artist/{Uri.EscapeDataString(idParam)}/albums?name={Uri.EscapeDataString(artistEntry.Name ?? "")}";

                var client = _httpClientFactory.CreateClient();
                var res = await client.GetFromJsonAsync<GatewayArtistAlbumsResponse>(url, ct).ConfigureAwait(false);
                if (res?.Albums != null && res.Albums.Count > 0)
                {
                    foreach (var al in res.Albums)
                    {
                        var id = ResonoItemCache.DeterministicGuid(al.Id ?? al.Name ?? Guid.NewGuid().ToString());
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "album",
                            Name = al.Name,
                            ArtistName = !string.IsNullOrWhiteSpace(al.ArtistName) ? al.ArtistName : artistEntry.Name,
                            SpotifyId = al.Id,
                            ImageUrl = al.ImageUrl,
                            ArtistId = artistEntry.Id
                        };
                        _cache.Set(id, entry);
                        list.Add(ResonoSearchActionFilter.BuildAlbumDto(id, entry));
                    }
                }
                else
                {
                    var searchUrl = $"{gatewayUrl}/jellyfin/search?q={Uri.EscapeDataString(artistEntry.Name ?? "")}&limit=30";
                    var searchData = await client.GetFromJsonAsync<GatewaySearchResponse>(searchUrl, ct).ConfigureAwait(false);
                    if (searchData?.Albums != null)
                    {
                        foreach (var al in searchData.Albums)
                        {
                            var id = ResonoItemCache.DeterministicGuid(al.Id ?? al.Name ?? Guid.NewGuid().ToString());
                            var entry = new ResonoItemCache.Entry
                            {
                                Kind = "album",
                                Name = al.Name,
                                ArtistName = !string.IsNullOrWhiteSpace(al.ArtistName) ? al.ArtistName : artistEntry.Name,
                                SpotifyId = al.Id,
                                ImageUrl = al.ImageUrl,
                                ArtistId = artistEntry.Id
                            };
                            _cache.Set(id, entry);
                            list.Add(ResonoSearchActionFilter.BuildAlbumDto(id, entry));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch artist albums for '{Artist}': {Message}", artistEntry.Name, ex.Message);
            }

            return list;
        }

        private async Task<List<BaseItemDto>> FetchArtistTopTracksAsync(ResonoItemCache.Entry artistEntry, CancellationToken ct)
        {
            var list = new List<BaseItemDto>();
            if (string.IsNullOrWhiteSpace(artistEntry.Name)) return list;

            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var idParam = !string.IsNullOrEmpty(artistEntry.SpotifyId) ? artistEntry.SpotifyId : artistEntry.Name;
                var url = $"{gatewayUrl}/jellyfin/artist/{Uri.EscapeDataString(idParam ?? "")}/top?name={Uri.EscapeDataString(artistEntry.Name ?? "")}";

                var client = _httpClientFactory.CreateClient();
                var res = await client.GetFromJsonAsync<GatewayArtistTopResponse>(url, ct).ConfigureAwait(false);
                if (res?.Tracks != null && res.Tracks.Count > 0)
                {
                    foreach (var t in res.Tracks)
                    {
                        var trackId = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                            ? g : ResonoItemCache.DeterministicGuid(t.Id ?? t.Name ?? Guid.NewGuid().ToString());

                        var streamUrl = !string.IsNullOrEmpty(t.StreamUrl) ? $"{gatewayUrl}{t.StreamUrl}" : $"{gatewayUrl}/playback/{t.Id}";
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "track",
                            Name = t.Name,
                            ArtistName = !string.IsNullOrWhiteSpace(t.ArtistName) ? t.ArtistName : artistEntry.Name,
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
                else
                {
                    var searchUrl = $"{gatewayUrl}/jellyfin/search?q={Uri.EscapeDataString(artistEntry.Name ?? "")}&limit=15";
                    var searchData = await client.GetFromJsonAsync<GatewaySearchResponse>(searchUrl, ct).ConfigureAwait(false);
                    if (searchData?.Tracks != null)
                    {
                        foreach (var t in searchData.Tracks)
                        {
                            var trackId = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                                ? g : ResonoItemCache.DeterministicGuid(t.Id ?? t.Name ?? Guid.NewGuid().ToString());

                            var streamUrl = !string.IsNullOrEmpty(t.StreamUrl) ? $"{gatewayUrl}{t.StreamUrl}" : $"{gatewayUrl}/playback/{t.Id}";
                            var entry = new ResonoItemCache.Entry
                            {
                                Kind = "track",
                                Name = t.Name,
                                ArtistName = !string.IsNullOrWhiteSpace(t.ArtistName) ? t.ArtistName : artistEntry.Name,
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch top tracks for '{Artist}': {Message}", artistEntry.Name, ex.Message);
            }

            return list;
        }

        private async Task<List<BaseItemDto>> FetchArtistSimilarAsync(ResonoItemCache.Entry artistEntry, CancellationToken ct)
        {
            var list = new List<BaseItemDto>();
            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var idParam = !string.IsNullOrEmpty(artistEntry.SpotifyId) ? artistEntry.SpotifyId : artistEntry.Name;
                var url = $"{gatewayUrl}/jellyfin/artist/{Uri.EscapeDataString(idParam ?? "")}/similar?name={Uri.EscapeDataString(artistEntry.Name ?? "")}";

                var client = _httpClientFactory.CreateClient();
                var res = await client.GetFromJsonAsync<GatewayArtistSimilarResponse>(url, ct).ConfigureAwait(false);
                if (res?.Artists != null)
                {
                    foreach (var a in res.Artists)
                    {
                        var id = ResonoItemCache.DeterministicGuid(a.Id ?? a.Name ?? Guid.NewGuid().ToString());
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "artist",
                            Name = a.Name,
                            SpotifyId = a.Id,
                            ImageUrl = a.ImageUrl
                        };
                        _cache.Set(id, entry);
                        list.Add(ResonoSearchActionFilter.BuildArtistDto(id, entry));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch similar artists: {Message}", ex.Message);
            }
            return list;
        }

        private async Task<List<BaseItemDto>> FetchChartTracksAsync(string chartId, CancellationToken ct)
        {
            var list = new List<BaseItemDto>();
            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var url = $"{gatewayUrl}/jellyfin/charts/{Uri.EscapeDataString(chartId)}/tracks";

                var client = _httpClientFactory.CreateClient();
                var res = await client.GetFromJsonAsync<GatewayArtistTopResponse>(url, ct).ConfigureAwait(false);
                if (res?.Tracks != null)
                {
                    foreach (var t in res.Tracks)
                    {
                        var trackId = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                            ? g : ResonoItemCache.DeterministicGuid(t.Id ?? t.Name ?? Guid.NewGuid().ToString());

                        var streamUrl = !string.IsNullOrEmpty(t.StreamUrl) ? $"{gatewayUrl}{t.StreamUrl}" : $"{gatewayUrl}/playback/{t.Id}";
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
                            StreamUrl = streamUrl
                        };
                        _cache.Set(trackId, entry);
                        list.Add(ResonoSearchActionFilter.BuildTrackDto(trackId, entry));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch chart tracks: {Message}", ex.Message);
            }
            return list;
        }

        private async Task<List<BaseItemDto>?> FetchAlbumTracksAsync(ResonoItemCache.Entry albumEntry, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(albumEntry.SpotifyId)) return null;

            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg.GatewayUrl);
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

        private async Task<LyricDto?> FetchSyncedLyricsAsync(ResonoItemCache.Entry trackEntry, CancellationToken ct)
        {
            try
            {
                var cfg = Plugin.Instance!.Configuration;
                if (!cfg.EnableSyncedLyrics || string.IsNullOrEmpty(trackEntry.Name) || string.IsNullOrEmpty(trackEntry.ArtistName)) return null;

                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var durSec = trackEntry.DurationMs.HasValue ? trackEntry.DurationMs.Value / 1000 : 0;
                var url = $"{gatewayUrl}/jellyfin/lyrics?artist={Uri.EscapeDataString(trackEntry.ArtistName)}&title={Uri.EscapeDataString(trackEntry.Name)}&duration={durSec}";

                var client = _httpClientFactory.CreateClient();
                var data = await client.GetFromJsonAsync<LrcLibResponse>(url, ct).ConfigureAwait(false);
                if (data == null) return null;

                var lines = new List<LyricLine>();
                if (!string.IsNullOrWhiteSpace(data.SyncedLyrics))
                {
                    var regex = new Regex(@"\[(\d{1,2}):(\d{1,2})(?:\.(\d{1,3}))?\](.*)");
                    foreach (var rawLine in data.SyncedLyrics.Split('\n'))
                    {
                        var match = regex.Match(rawLine.Trim());
                        if (match.Success)
                        {
                            var min = int.Parse(match.Groups[1].Value);
                            var sec = int.Parse(match.Groups[2].Value);
                            var msStr = match.Groups[3].Value.PadRight(3, '0');
                            var ms = int.Parse(msStr);
                            var ticks = ((min * 60L + sec) * 1000L + ms) * 10000L;
                            var text = match.Groups[4].Value.Trim();
                            lines.Add(new LyricLine(text, ticks));
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(data.PlainLyrics))
                {
                    foreach (var rawLine in data.PlainLyrics.Split('\n'))
                    {
                        lines.Add(new LyricLine(rawLine.Trim(), 0));
                    }
                }

                if (lines.Count == 0) return null;

                return new LyricDto
                {
                    Metadata = new LyricMetadata
                    {
                        Artist = trackEntry.ArtistName,
                        Title = trackEntry.Name,
                        Album = trackEntry.AlbumName,
                        IsSynced = !string.IsNullOrWhiteSpace(data.SyncedLyrics)
                    },
                    Lyrics = lines
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch lyrics: {Message}", ex.Message);
                return null;
            }
        }

        private async Task<List<BaseItemDto>> FetchChartsAsync(CancellationToken ct)
        {
            var list = new List<BaseItemDto>();
            try
            {
                var cfg = Plugin.Instance!.Configuration;
                if (!cfg.EnableVirtualPlaylists) return list;

                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var country = !string.IsNullOrWhiteSpace(cfg.ChartCountryCode) ? cfg.ChartCountryCode : "PE";
                var url = $"{gatewayUrl}/jellyfin/charts?country={Uri.EscapeDataString(country)}";

                var client = _httpClientFactory.CreateClient();
                var data = await client.GetFromJsonAsync<GatewayChartsResponse>(url, ct).ConfigureAwait(false);
                if (data?.Charts != null)
                {
                    foreach (var c in data.Charts)
                    {
                        var chartId = ResonoItemCache.DeterministicGuid("chart:" + (c.Id ?? c.Name ?? Guid.NewGuid().ToString()));
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "playlist",
                            Name = c.Name,
                            SpotifyId = c.Id,
                            ImageUrl = c.ImageUrl
                        };
                        _cache.Set(chartId, entry);
                        list.Add(BuildPlaylistDto(chartId, entry));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch virtual chart playlists: {Message}", ex.Message);
            }
            return list;
        }

        private async Task PinTrackAsync(string trackId)
        {
            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var url = $"{gatewayUrl}/jellyfin/pin/{Uri.EscapeDataString(trackId)}";
                var client = _httpClientFactory.CreateClient();
                await client.PostAsync(url, null).ConfigureAwait(false);
            }
            catch { }
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

    public class GatewayArtistTopResponse
    {
        [JsonPropertyName("tracks")]
        public List<GatewayTrack>? Tracks { get; set; }
    }

    public class GatewayArtistAlbumsResponse
    {
        [JsonPropertyName("albums")]
        public List<GatewayAlbum>? Albums { get; set; }
    }

    public class GatewayArtistSimilarResponse
    {
        [JsonPropertyName("artists")]
        public List<GatewayArtist>? Artists { get; set; }
    }

    public class LrcLibResponse
    {
        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; set; }
        [JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; set; }
    }

    public class GatewayChartsResponse
    {
        [JsonPropertyName("charts")]
        public List<GatewayChartItem>? Charts { get; set; }
    }

    public class GatewayChartItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("description")]
        public string? Description { get; set; }
        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }
    }
}
