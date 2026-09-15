using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Net;
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
        private readonly ResonoLibraryRegistrar _registrar;
        private readonly ResonoRecentlyPlayedTracker _recentlyPlayed;
        private readonly IAuthorizationContext _authContext;
        private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
        private readonly MediaBrowser.Controller.Session.ISessionManager _sessionManager;
        private readonly ILogger<ResonoItemDetailActionFilter> _logger;

        private static readonly ConcurrentDictionary<string, (DateTime Expires, List<BaseItemDto> Items)> _artistAlbumsCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (DateTime Expires, List<BaseItemDto> Items)> _artistTopTracksCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<Guid, (DateTime Expires, List<BaseItemDto> Items)> _albumTracksCache = new();
        private static readonly ConcurrentDictionary<string, (DateTime Expires, List<BaseItemDto> Items)> _chartTracksCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan MetadataCacheDuration = TimeSpan.FromMinutes(10);

        public ResonoItemDetailActionFilter(
            ResonoItemCache cache,
            IHttpClientFactory httpClientFactory,
            ResonoLibraryRegistrar registrar,
            ResonoRecentlyPlayedTracker recentlyPlayed,
            IAuthorizationContext authContext,
            MediaBrowser.Controller.Library.ILibraryManager libraryManager,
            MediaBrowser.Controller.Session.ISessionManager sessionManager,
            ILogger<ResonoItemDetailActionFilter> logger)
        {
            _cache = cache;
            _httpClientFactory = httpClientFactory;
            _registrar = registrar;
            _recentlyPlayed = recentlyPlayed;
            _authContext = authContext;
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
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

            // 0.1 Audio Stream Proxy (/Audio/{id}/..., /Items/{id}/File, /Items/{id}/Download)
            if (IsAudioStreamRoute(ctx, out var streamItemId))
            {
                _cache.TryGet(streamItemId, out var streamEntry);

                var cfg = Plugin.Instance?.Configuration;
                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg?.GatewayUrl);

                // If not found in memory cache, attempt metadata lookup from Gateway
                if (streamEntry == null)
                {
                    try
                    {
                        var metaClient = _httpClientFactory.CreateClient();
                        var trackInfo = await metaClient.GetFromJsonAsync<GatewayTrack>($"{gatewayUrl}/jellyfin/track/{streamItemId:N}", ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                        if (trackInfo != null)
                        {
                            var albId = !string.IsNullOrEmpty(trackInfo.AlbumId) ? ResonoItemCache.StubGuid("dz-album", trackInfo.AlbumId) : (Guid?)null;
                            var artId = !string.IsNullOrEmpty(trackInfo.ArtistId) ? ResonoItemCache.StubGuid("dz-artist", trackInfo.ArtistId) : (Guid?)null;

                            streamEntry = new ResonoItemCache.Entry
                            {
                                Kind = "track",
                                Name = trackInfo.Name,
                                ArtistName = trackInfo.ArtistName,
                                AlbumName = trackInfo.AlbumName,
                                SpotifyId = trackInfo.Id,
                                CanonicalId = trackInfo.CanonicalId,
                                ImageUrl = trackInfo.ImageUrl,
                                DurationMs = trackInfo.DurationMs,
                                TrackNumber = trackInfo.TrackNumber,
                                DiscNumber = trackInfo.DiscNumber,
                                StreamUrl = !string.IsNullOrEmpty(trackInfo.StreamUrl) ? $"{gatewayUrl}{trackInfo.StreamUrl}" : $"{gatewayUrl}/playback/{trackInfo.Id}",
                                AlbumId = albId,
                                ArtistId = artId
                            };
                            _cache.Set(streamItemId, streamEntry);

                            if (albId.HasValue && !string.IsNullOrEmpty(trackInfo.AlbumName))
                            {
                                _cache.Set(albId.Value, new ResonoItemCache.Entry
                                {
                                    Kind = "album",
                                    Name = trackInfo.AlbumName,
                                    ArtistName = trackInfo.ArtistName,
                                    SpotifyId = trackInfo.AlbumId,
                                    ImageUrl = trackInfo.ImageUrl,
                                    ArtistId = artId
                                });
                            }
                        }
                    }
                    catch { }
                }

                if (streamEntry != null)
                {
                    _registrar.RegisterTrack(streamItemId, streamEntry);

                    var rawPath = req.Path.Value ?? string.Empty;
                    bool isUniversal = rawPath.IndexOf("/universal", StringComparison.OrdinalIgnoreCase) > 0;
                    bool wantsRedirect = isUniversal &&
                        req.Query.TryGetValue("EnableRedirection", out var redirVal) &&
                        string.Equals(redirVal.ToString(), "true", StringComparison.OrdinalIgnoreCase);

                    if (wantsRedirect)
                    {
                        var redirTarget = $"/Audio/{streamItemId:N}/main.mp3";
                        if (req.Query.TryGetValue("ApiKey", out var ak) || req.Query.TryGetValue("api_key", out ak))
                        {
                            redirTarget += $"?api_key={Uri.EscapeDataString(ak.ToString())}";
                        }
                        _logger.LogInformation("[Resono] /Audio universal redirect: {Id} -> {Target}", streamItemId, redirTarget);
                        ctx.Result = new RedirectResult(redirTarget, permanent: false);
                        return;
                    }

                    _ = NudgePlaybackStartAsync(streamItemId);

                    // Record play in recently played tracker for Home screen
                    try
                    {
                        var auth = await _authContext.GetAuthorizationInfo(ctx.HttpContext.Request).ConfigureAwait(false);
                        _recentlyPlayed.RecordPlay(auth?.UserId ?? Guid.Empty, streamItemId, streamEntry.AlbumId);
                    }
                    catch { }

                    var playId = !string.IsNullOrEmpty(streamEntry.CanonicalId)
                        ? streamEntry.CanonicalId
                        : (!string.IsNullOrEmpty(streamEntry.SpotifyId) ? streamEntry.SpotifyId : streamItemId.ToString("N"));
                    var targetUrl = !string.IsNullOrEmpty(streamEntry.StreamUrl) ? streamEntry.StreamUrl : $"{gatewayUrl}/playback/{playId}";

                    await ProxyAudioStreamAsync(ctx.HttpContext, targetUrl, cfg?.DeezerArl).ConfigureAwait(false);
                    ctx.Result = new EmptyResult();
                    return;
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
                ResonoItemCache.Entry? simEntry = null;
                if (TryExtractGuidFromPath(path, out var simId))
                {
                    _cache.TryGet(simId, out simEntry);
                }
                if (simEntry == null && ctx.RouteData.Values.TryGetValue("name", out var simName) && simName != null)
                {
                    _cache.TryGetArtistByName(simName.ToString()!, out simEntry);
                }

                if (simEntry is { Kind: "artist" })
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

            // 4. Dedicated Artist Lookup: GET /Artists/{name} or GET /Artists/{id}
            var route = ctx.RouteData.Values;
            var controller = route.TryGetValue("controller", out var ctl) ? ctl?.ToString() : null;
            if (string.Equals(controller, "Artists", StringComparison.OrdinalIgnoreCase))
            {
                object? artistParam = null;
                if (route.TryGetValue("name", out artistParam) || route.TryGetValue("id", out artistParam))
                {
                    var paramStr = artistParam?.ToString();
                    if (!string.IsNullOrWhiteSpace(paramStr) && !string.Equals(paramStr, "AlbumArtists", StringComparison.OrdinalIgnoreCase))
                    {
                        ResonoItemCache.Entry? artEntry = null;
                        Guid artistGuid = default;

                        if (Guid.TryParse(paramStr, out artistGuid))
                        {
                            _cache.TryGet(artistGuid, out artEntry);
                        }
                        else
                        {
                            var unescaped = Uri.UnescapeDataString(paramStr);
                            if (_cache.TryGetArtistByName(unescaped, out artEntry))
                            {
                                artistGuid = artEntry!.Id;
                            }
                            else
                            {
                                artistGuid = ResonoItemCache.StubGuid("dz-artist", unescaped);
                            }
                        }

                        if (artEntry == null)
                        {
                            // Fetch from Gateway
                            try
                            {
                                var cfg = Plugin.Instance?.Configuration;
                                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg?.GatewayUrl);
                                var metaClient = _httpClientFactory.CreateClient();
                                var cleanParam = Uri.UnescapeDataString(paramStr);
                                var artInfo = await metaClient.GetFromJsonAsync<GatewayArtist>($"{gatewayUrl}/jellyfin/artist/{Uri.EscapeDataString(cleanParam)}", ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                                if (artInfo != null && !string.IsNullOrEmpty(artInfo.Name))
                                {
                                    artEntry = new ResonoItemCache.Entry
                                    {
                                        Kind = "artist",
                                        Name = artInfo.Name,
                                        SpotifyId = artInfo.Id,
                                        ImageUrl = artInfo.ImageUrl,
                                        Id = artistGuid
                                    };
                                    _cache.Set(artistGuid, artEntry);
                                }
                            }
                            catch { }
                        }

                        if (artEntry != null)
                        {
                            ctx.Result = new OkObjectResult(ResonoSearchActionFilter.BuildArtistDto(artistGuid, artEntry));
                            return;
                        }
                    }
                }
            }

            // 5. Single-Item Detail (/Items/{id}, /Users/{u}/Items/{id}, /Playlists/{id})
            if (TryExtractSingleItemId(ctx, out var singleId))
            {
                _cache.TryGet(singleId, out var singleEntry);
                if (singleEntry == null)
                {
                    singleEntry = await TryResolveItemFromGatewayAsync(singleId, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                }

                if (singleEntry != null)
                {
                    _logger.LogInformation("[Resono] Serving Single-Item Detail for {Id} ({Kind}: '{Name}')", singleId, singleEntry.Kind, singleEntry.Name);
                    BaseItemDto dto = singleEntry.Kind switch
                    {
                        "artist" => ResonoSearchActionFilter.BuildArtistDto(singleId, singleEntry),
                        "album" => ResonoSearchActionFilter.BuildAlbumDto(singleId, singleEntry),
                        "playlist" => ResonoSearchActionFilter.BuildPlaylistDto(singleId, singleEntry),
                        _ => ResonoSearchActionFilter.BuildTrackDto(singleId, singleEntry)
                    };
                    ctx.Result = new OkObjectResult(dto);
                    return;
                }
            }

            // 6. Virtual Playlist Tracks (/Playlists/{id}/Items)
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

            // 7. Handle /Items queries
            var cleanPath = path.TrimEnd('/');
            if (cleanPath.EndsWith("/Items", StringComparison.OrdinalIgnoreCase))
            {
                // (a0) Query by specific IDs parameter (/Items?ids=... or /Users/{u}/Items?ids=...)
                if (TryExtractGuidListFromQuery(req, "ids", out var specificIds) || TryExtractGuidListFromQuery(req, "Ids", out specificIds))
                {
                    var matchingItems = new List<BaseItemDto>();
                    foreach (var sId in specificIds)
                    {
                        if (!_cache.TryGet(sId, out var sEntry) || sEntry == null)
                        {
                            sEntry = await TryResolveItemFromGatewayAsync(sId, CancellationToken.None).ConfigureAwait(false);
                        }
                        if (sEntry != null)
                        {
                            BaseItemDto dto = sEntry.Kind switch
                            {
                                "artist" => ResonoSearchActionFilter.BuildArtistDto(sId, sEntry),
                                "album" => ResonoSearchActionFilter.BuildAlbumDto(sId, sEntry),
                                "playlist" => ResonoSearchActionFilter.BuildPlaylistDto(sId, sEntry),
                                _ => ResonoSearchActionFilter.BuildTrackDto(sId, sEntry)
                            };
                            matchingItems.Add(dto);
                        }
                    }
                    if (matchingItems.Count > 0)
                    {
                        ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                        {
                            Items = matchingItems.ToArray(),
                            TotalRecordCount = matchingItems.Count
                        });
                        return;
                    }
                }

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
                            var types = ResonoSearchActionFilter.ExtractIncludeItemTypes(req.HttpContext);
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

                // (c) Artist Profile queries (/Items?albumArtistIds=..., /Items?artistIds=..., /Items?contributingArtistIds=..., /Items?artists=...)
                List<Guid>? artistIds = null;
                if (!TryExtractGuidListFromQuery(req, "albumArtistIds", out artistIds)
                    && !TryExtractGuidListFromQuery(req, "artistIds", out artistIds)
                    && !TryExtractGuidListFromQuery(req, "contributingArtistIds", out artistIds))
                {
                    foreach (var key in new[] { "artists", "Artists", "artist", "Artist" })
                    {
                        if (req.Query.TryGetValue(key, out var artNames) && !string.IsNullOrEmpty(artNames.ToString()))
                        {
                            var artName = artNames.ToString().Split(',')[0].Trim();
                            if (!_cache.TryGetArtistByName(artName, out var namedEntry))
                            {
                                var namedGuid = ResonoItemCache.StubGuid("dz-artist", artName);
                                namedEntry = new ResonoItemCache.Entry
                                {
                                    Kind = "artist",
                                    Name = artName,
                                    Id = namedGuid
                                };
                                _cache.Set(namedGuid, namedEntry);
                            }
                            artistIds = new List<Guid> { namedEntry!.Id };
                            break;
                        }
                    }
                }

                if (artistIds != null && artistIds.Count > 0)
                {
                    foreach (var artistGuid in artistIds)
                    {
                        if (!_cache.TryGet(artistGuid, out var artistEntry) || artistEntry == null)
                        {
                            artistEntry = new ResonoItemCache.Entry
                            {
                                Kind = "artist",
                                Id = artistGuid,
                                Name = null
                            };
                        }

                        var types = ResonoSearchActionFilter.ExtractIncludeItemTypes(req.HttpContext);
                        bool wantsAlbum = types.Contains("MusicAlbum");
                        bool wantsAudio = types.Contains("Audio") || (!wantsAlbum && types.Count == 0);

                        if (wantsAlbum)
                        {
                            var albums = await FetchArtistAlbumsAsync(artistEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                            if (albums != null && albums.Count > 0)
                            {
                                ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                                {
                                    Items = albums.ToArray(),
                                    TotalRecordCount = albums.Count
                                });
                                return;
                            }
                        }
                        if (wantsAudio)
                        {
                            var tracks = await FetchArtistTopTracksAsync(artistEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
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
            }

            await next().ConfigureAwait(false);
        }

        public async Task OnResultExecutionAsync(ResultExecutingContext ctx, ResultExecutionDelegate next)
        {
            var req = ctx.HttpContext.Request;
            var path = req.Path.Value ?? string.Empty;

            // Handle Home / Latest Media queries: GET /Items/Latest or GET /Users/{userId}/Items/Latest
            if (path.IndexOf("/Items/Latest", StringComparison.OrdinalIgnoreCase) >= 0 && ctx.Result is ObjectResult latestOr)
            {
                if (latestOr.Value is BaseItemDto[] emptyArray && emptyArray.Length == 0)
                {
                    var chartTracks = await FetchChartTracksAsync("global", ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (chartTracks.Count > 0)
                    {
                        latestOr.Value = chartTracks.Take(20).ToArray();
                    }
                }
                else if (latestOr.Value is QueryResult<BaseItemDto> emptyQr && emptyQr.TotalRecordCount == 0)
                {
                    var chartTracks = await FetchChartTracksAsync("global", ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (chartTracks.Count > 0)
                    {
                        emptyQr.Items = chartTracks.Take(20).ToArray();
                        emptyQr.TotalRecordCount = emptyQr.Items.Count;
                    }
                }
            }

            await next().ConfigureAwait(false);
        }

        private static bool IsAudioStreamRoute(ActionExecutingContext ctx, out Guid id)
        {
            id = default;
            var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;

            // 1. /Audio/{id}/... (universal, stream, stream.mp3, main.mp3, etc.)
            if (path.StartsWith("/Audio/", StringComparison.OrdinalIgnoreCase))
            {
                return TryExtractGuidFromPath(path, out id);
            }

            // 2. /Items/{id}/File or /Items/{id}/Download (used by Finer, Discrete, and native iOS clients)
            if (path.StartsWith("/Items/", StringComparison.OrdinalIgnoreCase)
                && (path.EndsWith("/File", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/Download", StringComparison.OrdinalIgnoreCase)))
            {
                return TryExtractGuidFromPath(path, out id);
            }

            return false;
        }

        private async Task ProxyAudioStreamAsync(HttpContext httpCtx, string targetUrl, string? deezerArl)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = Timeout.InfiniteTimeSpan;
                var req = httpCtx.Request;
                bool isHead = HttpMethods.IsHead(req.Method);
                var currentUrl = targetUrl;
                HttpResponseMessage? forwardResp = null;

                for (int hop = 0; hop < 5; hop++)
                {
                    var forwardReq = new HttpRequestMessage(isHead ? HttpMethod.Head : HttpMethod.Get, currentUrl);
                    if (!string.IsNullOrWhiteSpace(deezerArl))
                    {
                        forwardReq.Headers.TryAddWithoutValidation("X-Deezer-Arl", deezerArl.Trim());
                    }
                    if (req.Headers.TryGetValue("Range", out var rangeVal) && !string.IsNullOrWhiteSpace(rangeVal))
                    {
                        forwardReq.Headers.TryAddWithoutValidation("Range", rangeVal.ToString());
                    }

                    forwardResp = await client.SendAsync(forwardReq, HttpCompletionOption.ResponseHeadersRead, httpCtx.RequestAborted).ConfigureAwait(false);
                    if ((int)forwardResp.StatusCode is 301 or 302 or 303 or 307 or 308 && forwardResp.Headers.Location != null)
                    {
                        var nextUri = forwardResp.Headers.Location;
                        if (!nextUri.IsAbsoluteUri)
                        {
                            nextUri = new Uri(new Uri(currentUrl), nextUri);
                        }
                        currentUrl = nextUri.ToString();
                        forwardResp.Dispose();
                        forwardResp = null;
                        continue;
                    }
                    break;
                }

                if (forwardResp != null)
                {
                    using (forwardResp)
                    {
                        var httpResp = httpCtx.Response;
                        httpResp.StatusCode = (int)forwardResp.StatusCode;
                        httpResp.ContentType = forwardResp.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";

                        var contentLength = forwardResp.Content.Headers.ContentLength;
                        if (contentLength.HasValue)
                        {
                            httpResp.ContentLength = contentLength.Value;
                        }

                        if (forwardResp.Headers.Location != null)
                        {
                            httpResp.Headers["Location"] = forwardResp.Headers.Location.ToString();
                        }
                        if (forwardResp.Headers.TryGetValues("Accept-Ranges", out var ar))
                        {
                            httpResp.Headers["Accept-Ranges"] = string.Join(",", ar);
                        }
                        else
                        {
                            httpResp.Headers["Accept-Ranges"] = "bytes";
                        }
                        if (forwardResp.Content.Headers.TryGetValues("Content-Range", out var cr))
                        {
                            httpResp.Headers["Content-Range"] = string.Join(",", cr);
                        }

                        if (!isHead)
                        {
                            await using var srcStream = await forwardResp.Content.ReadAsStreamAsync(httpCtx.RequestAborted).ConfigureAwait(false);
                            if (contentLength.HasValue)
                            {
                                await CopyExactlyAsync(srcStream, httpResp.Body, contentLength.Value, httpCtx.RequestAborted).ConfigureAwait(false);
                            }
                            else
                            {
                                await srcStream.CopyToAsync(httpResp.Body, 64 * 1024, httpCtx.RequestAborted).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Resono] Failed to proxy audio stream: {Message}", ex.Message);
            }
        }

        private static async Task CopyExactlyAsync(System.IO.Stream src, System.IO.Stream dst, long length, CancellationToken ct)
        {
            const int bufSize = 64 * 1024;
            var buf = new byte[bufSize];
            long remaining = length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(remaining, bufSize);
                var read = await src.ReadAsync(buf.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
            }
        }

        private Task NudgePlaybackStartAsync(Guid trackId)
        {
            try
            {
                foreach (var s in _sessionManager.Sessions)
                {
                    var info = new MediaBrowser.Model.Session.PlaybackStartInfo
                    {
                        ItemId = trackId,
                        SessionId = s.Id,
                        MediaSourceId = trackId.ToString("N"),
                        CanSeek = true,
                        IsPaused = false,
                        IsMuted = false,
                        PlayMethod = MediaBrowser.Model.Session.PlayMethod.DirectStream,
                        PositionTicks = 0,
                    };
                    _ = _sessionManager.OnPlaybackStart(info);
                }
            }
            catch { }
            return Task.CompletedTask;
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

            var path = ctx.HttpContext.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
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

        private async Task<List<BaseItemDto>> FetchArtistAlbumsAsync(ResonoItemCache.Entry artistEntry, CancellationToken ct)
        {
            var cacheKey = !string.IsNullOrEmpty(artistEntry.SpotifyId) ? artistEntry.SpotifyId : (artistEntry.Name ?? "");
            if (!string.IsNullOrEmpty(cacheKey) && _artistAlbumsCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expires)
            {
                return cached.Items;
            }

            var list = new List<BaseItemDto>();
            var cfg = Plugin.Instance!.Configuration;
            var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg.GatewayUrl);
            var client = _httpClientFactory.CreateClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var fetchToken = timeoutCts.Token;

            if (string.IsNullOrWhiteSpace(artistEntry.Name) && !string.IsNullOrEmpty(artistEntry.SpotifyId))
            {
                try
                {
                    var art = await client.GetFromJsonAsync<GatewayArtist>($"{gatewayUrl}/jellyfin/artist/{Uri.EscapeDataString(artistEntry.SpotifyId)}", fetchToken).ConfigureAwait(false);
                    if (art != null && !string.IsNullOrEmpty(art.Name))
                    {
                        artistEntry.Name = art.Name;
                        artistEntry.ImageUrl = art.ImageUrl ?? artistEntry.ImageUrl;
                        _cache.Set(artistEntry.Id, artistEntry);
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(artistEntry.Name)) return list;

            try
            {
                var idParam = !string.IsNullOrEmpty(artistEntry.SpotifyId) ? artistEntry.SpotifyId : artistEntry.Name;
                var url = $"{gatewayUrl}/jellyfin/artist/{Uri.EscapeDataString(idParam ?? "")}/albums?name={Uri.EscapeDataString(artistEntry.Name ?? "")}";
                var res = await client.GetFromJsonAsync<GatewayArtistAlbumsResponse>(url, fetchToken).ConfigureAwait(false);
                if (res?.Albums != null && res.Albums.Count > 0)
                {
                    foreach (var al in res.Albums)
                    {
                        var id = ResonoItemCache.StubGuid("dz-album", al.Id ?? al.Name ?? Guid.NewGuid().ToString());
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
                        _registrar.RegisterAlbum(id, al.Name ?? "", al.ArtistName ?? artistEntry.Name);
                        list.Add(ResonoSearchActionFilter.BuildAlbumDto(id, entry));
                    }
                }
                else
                {
                    var searchUrl = $"{gatewayUrl}/jellyfin/search?q={Uri.EscapeDataString(artistEntry.Name ?? "")}&limit=30";
                    var searchData = await client.GetFromJsonAsync<GatewaySearchResponse>(searchUrl, fetchToken).ConfigureAwait(false);
                    if (searchData?.Albums != null)
                    {
                        foreach (var al in searchData.Albums)
                        {
                            var id = ResonoItemCache.StubGuid("dz-album", al.Id ?? al.Name ?? Guid.NewGuid().ToString());
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
                            _registrar.RegisterAlbum(id, al.Name ?? "", al.ArtistName ?? artistEntry.Name);
                            list.Add(ResonoSearchActionFilter.BuildAlbumDto(id, entry));
                        }
                    }
                }

                if (list.Count > 0 && !string.IsNullOrEmpty(cacheKey))
                {
                    _artistAlbumsCache[cacheKey] = (DateTime.UtcNow.Add(MetadataCacheDuration), list);
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
            var cacheKey = !string.IsNullOrEmpty(artistEntry.SpotifyId) ? artistEntry.SpotifyId : (artistEntry.Name ?? "");
            if (!string.IsNullOrEmpty(cacheKey) && _artistTopTracksCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expires)
            {
                return cached.Items;
            }

            var list = new List<BaseItemDto>();
            var cfg = Plugin.Instance!.Configuration;
            var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg.GatewayUrl);
            var client = _httpClientFactory.CreateClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var fetchToken = timeoutCts.Token;

            if (string.IsNullOrWhiteSpace(artistEntry.Name) && !string.IsNullOrEmpty(artistEntry.SpotifyId))
            {
                try
                {
                    var art = await client.GetFromJsonAsync<GatewayArtist>($"{gatewayUrl}/jellyfin/artist/{Uri.EscapeDataString(artistEntry.SpotifyId)}", fetchToken).ConfigureAwait(false);
                    if (art != null && !string.IsNullOrEmpty(art.Name))
                    {
                        artistEntry.Name = art.Name;
                        artistEntry.ImageUrl = art.ImageUrl ?? artistEntry.ImageUrl;
                        _cache.Set(artistEntry.Id, artistEntry);
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(artistEntry.Name)) return list;

            try
            {
                var idParam = !string.IsNullOrEmpty(artistEntry.SpotifyId) ? artistEntry.SpotifyId : artistEntry.Name;
                var url = $"{gatewayUrl}/jellyfin/artist/{Uri.EscapeDataString(idParam ?? "")}/top?name={Uri.EscapeDataString(artistEntry.Name ?? "")}";
                var res = await client.GetFromJsonAsync<GatewayArtistTopResponse>(url, fetchToken).ConfigureAwait(false);
                if (res?.Tracks != null && res.Tracks.Count > 0)
                {
                    foreach (var t in res.Tracks)
                    {
                        var trackId = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                            ? g : ResonoItemCache.StubGuid("dz-track", t.Id ?? t.Name ?? Guid.NewGuid().ToString());

                        var albumId = !string.IsNullOrEmpty(t.AlbumId)
                            ? ResonoItemCache.StubGuid("dz-album", t.AlbumId)
                            : (!string.IsNullOrEmpty(t.AlbumName) ? ResonoItemCache.StubGuid("dz-album", t.AlbumName + (t.ArtistName ?? "")) : (Guid?)null);

                        if (albumId.HasValue && !string.IsNullOrEmpty(t.AlbumName))
                        {
                            if (!_cache.TryGet(albumId.Value, out _))
                            {
                                _cache.Set(albumId.Value, new ResonoItemCache.Entry
                                {
                                    Kind = "album",
                                    Name = t.AlbumName,
                                    ArtistName = t.ArtistName ?? artistEntry.Name,
                                    SpotifyId = t.AlbumId,
                                    ImageUrl = t.ImageUrl ?? artistEntry.ImageUrl,
                                    ArtistId = artistEntry.Id
                                });
                            }
                        }

                        var streamUrl = !string.IsNullOrEmpty(t.StreamUrl) ? $"{gatewayUrl}{t.StreamUrl}" : $"{gatewayUrl}/playback/{t.Id}";
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "track",
                            Name = t.Name,
                            ArtistName = t.ArtistName ?? artistEntry.Name,
                            AlbumName = t.AlbumName,
                            SpotifyId = t.Id,
                            CanonicalId = t.CanonicalId,
                            ImageUrl = t.ImageUrl ?? artistEntry.ImageUrl,
                            DurationMs = t.DurationMs,
                            TrackNumber = t.TrackNumber,
                            DiscNumber = t.DiscNumber,
                            StreamUrl = streamUrl,
                            AlbumId = albumId,
                            ArtistId = artistEntry.Id
                        };
                        _cache.Set(trackId, entry);
                        _registrar.RegisterTrack(trackId, entry);

                        list.Add(ResonoSearchActionFilter.BuildTrackDto(trackId, entry));
                    }
                }
                else
                {
                    var searchUrl = $"{gatewayUrl}/jellyfin/search?q={Uri.EscapeDataString(artistEntry.Name ?? "")}&limit=15";
                    var searchData = await client.GetFromJsonAsync<GatewaySearchResponse>(searchUrl, fetchToken).ConfigureAwait(false);
                    if (searchData?.Tracks != null)
                    {
                        foreach (var t in searchData.Tracks)
                        {
                            var trackId = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                                ? g : ResonoItemCache.StubGuid("dz-track", t.Id ?? t.Name ?? Guid.NewGuid().ToString());

                            var albumId = !string.IsNullOrEmpty(t.AlbumId)
                                ? ResonoItemCache.StubGuid("dz-album", t.AlbumId)
                                : (!string.IsNullOrEmpty(t.AlbumName) ? ResonoItemCache.StubGuid("dz-album", t.AlbumName + (t.ArtistName ?? "")) : (Guid?)null);

                            if (albumId.HasValue && !string.IsNullOrEmpty(t.AlbumName))
                            {
                                if (!_cache.TryGet(albumId.Value, out _))
                                {
                                    _cache.Set(albumId.Value, new ResonoItemCache.Entry
                                    {
                                        Kind = "album",
                                        Name = t.AlbumName,
                                        ArtistName = t.ArtistName ?? artistEntry.Name,
                                        SpotifyId = t.AlbumId,
                                        ImageUrl = t.ImageUrl ?? artistEntry.ImageUrl,
                                        ArtistId = artistEntry.Id
                                    });
                                }
                            }

                            var streamUrl = !string.IsNullOrEmpty(t.StreamUrl) ? $"{gatewayUrl}{t.StreamUrl}" : $"{gatewayUrl}/playback/{t.Id}";
                            var entry = new ResonoItemCache.Entry
                            {
                                Kind = "track",
                                Name = t.Name,
                                ArtistName = t.ArtistName ?? artistEntry.Name,
                                AlbumName = t.AlbumName,
                                SpotifyId = t.Id,
                                CanonicalId = t.CanonicalId,
                                ImageUrl = t.ImageUrl ?? artistEntry.ImageUrl,
                                DurationMs = t.DurationMs,
                                TrackNumber = t.TrackNumber,
                                DiscNumber = t.DiscNumber,
                                StreamUrl = streamUrl,
                                AlbumId = albumId,
                                ArtistId = artistEntry.Id
                            };
                            _cache.Set(trackId, entry);
                            _registrar.RegisterTrack(trackId, entry);
                            list.Add(ResonoSearchActionFilter.BuildTrackDto(trackId, entry));
                        }
                    }
                }

                if (list.Count > 0 && !string.IsNullOrEmpty(cacheKey))
                {
                    _artistTopTracksCache[cacheKey] = (DateTime.UtcNow.Add(MetadataCacheDuration), list);
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
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var res = await client.GetFromJsonAsync<GatewayArtistSimilarResponse>(url, timeoutCts.Token).ConfigureAwait(false);
                if (res?.Artists != null)
                {
                    foreach (var a in res.Artists)
                    {
                        var id = ResonoItemCache.StubGuid("dz-artist", a.Id ?? a.Name ?? Guid.NewGuid().ToString());
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "artist",
                            Name = a.Name,
                            SpotifyId = a.Id,
                            ImageUrl = a.ImageUrl,
                            Id = id
                        };
                        _cache.Set(id, entry);
                        if (!string.IsNullOrEmpty(a.Name))
                        {
                            _registrar.RegisterArtist(id, a.Name);
                        }
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
            if (_chartTracksCache.TryGetValue(chartId, out var cached) && DateTime.UtcNow < cached.Expires)
            {
                return cached.Items;
            }

            var list = new List<BaseItemDto>();
            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var url = $"{gatewayUrl}/jellyfin/charts/{Uri.EscapeDataString(chartId)}/tracks";

                var client = _httpClientFactory.CreateClient();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var res = await client.GetFromJsonAsync<GatewayArtistTopResponse>(url, timeoutCts.Token).ConfigureAwait(false);
                if (res?.Tracks != null)
                {
                    foreach (var t in res.Tracks)
                    {
                        var trackId = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                            ? g : ResonoItemCache.StubGuid("dz-track", t.Id ?? t.Name ?? Guid.NewGuid().ToString());

                        var albumId = !string.IsNullOrEmpty(t.AlbumId)
                            ? ResonoItemCache.StubGuid("dz-album", t.AlbumId)
                            : (!string.IsNullOrEmpty(t.AlbumName) ? ResonoItemCache.StubGuid("dz-album", t.AlbumName + (t.ArtistName ?? "")) : (Guid?)null);

                        var artistId = !string.IsNullOrEmpty(t.ArtistId)
                            ? ResonoItemCache.StubGuid("dz-artist", t.ArtistId)
                            : (!string.IsNullOrEmpty(t.ArtistName) ? ResonoItemCache.StubGuid("dz-artist", t.ArtistName) : (Guid?)null);

                        if (albumId.HasValue && !string.IsNullOrEmpty(t.AlbumName))
                        {
                            if (!_cache.TryGet(albumId.Value, out _))
                            {
                                _cache.Set(albumId.Value, new ResonoItemCache.Entry
                                {
                                    Kind = "album",
                                    Name = t.AlbumName,
                                    ArtistName = t.ArtistName,
                                    SpotifyId = t.AlbumId,
                                    ImageUrl = t.ImageUrl,
                                    ArtistId = artistId
                                });
                            }
                            _registrar.RegisterAlbum(albumId.Value, t.AlbumName, t.ArtistName);
                        }

                        if (artistId.HasValue && !string.IsNullOrEmpty(t.ArtistName))
                        {
                            if (!_cache.TryGet(artistId.Value, out _))
                            {
                                _cache.Set(artistId.Value, new ResonoItemCache.Entry
                                {
                                    Kind = "artist",
                                    Name = t.ArtistName,
                                    SpotifyId = t.ArtistId,
                                    ImageUrl = t.ImageUrl,
                                    Id = artistId.Value
                                });
                            }
                            _registrar.RegisterArtist(artistId.Value, t.ArtistName);
                        }

                        var streamUrl = !string.IsNullOrEmpty(t.StreamUrl) ? $"{gatewayUrl}{t.StreamUrl}" : $"{gatewayUrl}/playback/{t.Id}";
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "track",
                            Name = t.Name,
                            ArtistName = t.ArtistName,
                            AlbumName = t.AlbumName,
                            SpotifyId = t.Id,
                            CanonicalId = t.CanonicalId,
                            ImageUrl = t.ImageUrl,
                            DurationMs = t.DurationMs,
                            TrackNumber = t.TrackNumber,
                            DiscNumber = t.DiscNumber,
                            StreamUrl = streamUrl,
                            AlbumId = albumId,
                            ArtistId = artistId
                        };
                        _cache.Set(trackId, entry);
                        _registrar.RegisterTrack(trackId, entry);
                        list.Add(ResonoSearchActionFilter.BuildTrackDto(trackId, entry));
                    }
                }

                if (list.Count > 0)
                {
                    _chartTracksCache[chartId] = (DateTime.UtcNow.Add(MetadataCacheDuration), list);
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
            if (_albumTracksCache.TryGetValue(albumEntry.Id, out var cached) && DateTime.UtcNow < cached.Expires)
            {
                return cached.Items;
            }

            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var client = _httpClientFactory.CreateClient();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var fetchToken = timeoutCts.Token;

                string? albumIdParam = albumEntry.SpotifyId;
                if (string.IsNullOrEmpty(albumIdParam) && !string.IsNullOrEmpty(albumEntry.Name))
                {
                    var query = $"{albumEntry.ArtistName} {albumEntry.Name}".Trim();
                    var searchUrl = $"{gatewayUrl}/jellyfin/search?q={Uri.EscapeDataString(query)}&limit=5";
                    var searchRes = await client.GetFromJsonAsync<GatewaySearchResponse>(searchUrl, fetchToken).ConfigureAwait(false);
                    var matched = searchRes?.Albums?.FirstOrDefault(a =>
                        string.Equals(a.Name, albumEntry.Name, StringComparison.OrdinalIgnoreCase));
                    if (matched != null && !string.IsNullOrEmpty(matched.Id))
                    {
                        albumIdParam = matched.Id;
                        albumEntry.SpotifyId = matched.Id;
                    }
                }

                if (string.IsNullOrEmpty(albumIdParam)) return null;

                var url = $"{gatewayUrl}/jellyfin/album/{Uri.EscapeDataString(albumIdParam)}";
                var albumData = await client.GetFromJsonAsync<GatewayAlbumResponse>(url, fetchToken).ConfigureAwait(false);
                if (albumData?.Tracks == null) return null;

                var result = new List<BaseItemDto>();
                foreach (var t in albumData.Tracks)
                {
                    var trackId = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                        ? g : ResonoItemCache.StubGuid("dz-track", t.Id ?? t.Name ?? Guid.NewGuid().ToString());

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
                        CanonicalId = t.CanonicalId,
                        ImageUrl = t.ImageUrl ?? albumEntry.ImageUrl,
                        DurationMs = t.DurationMs,
                        TrackNumber = t.TrackNumber,
                        DiscNumber = t.DiscNumber,
                        StreamUrl = streamUrl,
                        AlbumId = albumEntry.Id,
                        ArtistId = albumEntry.ArtistId
                    };
                    _cache.Set(trackId, entry);
                    _registrar.RegisterTrack(trackId, entry);

                    result.Add(ResonoSearchActionFilter.BuildTrackDto(trackId, entry));
                }

                if (result.Count > 0)
                {
                    _albumTracksCache[albumEntry.Id] = (DateTime.UtcNow.Add(MetadataCacheDuration), result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch album tracks from Resono Gateway: {Message}", ex.Message);
                return null;
            }
        }

        private async Task<ResonoItemCache.Entry?> TryResolveItemFromGatewayAsync(Guid id, CancellationToken ct)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg?.GatewayUrl);
                var client = _httpClientFactory.CreateClient();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var fetchToken = timeoutCts.Token;

                // 1. Try resolving as track
                try
                {
                    var track = await client.GetFromJsonAsync<GatewayTrack>($"{gatewayUrl}/jellyfin/track/{id:N}", fetchToken).ConfigureAwait(false);
                    if (track != null && !string.IsNullOrEmpty(track.Name))
                    {
                        var albId = !string.IsNullOrEmpty(track.AlbumId) ? ResonoItemCache.StubGuid("dz-album", track.AlbumId) : (Guid?)null;
                        var artId = !string.IsNullOrEmpty(track.ArtistId) ? ResonoItemCache.StubGuid("dz-artist", track.ArtistId) : (Guid?)null;
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "track",
                            Name = track.Name,
                            ArtistName = track.ArtistName,
                            AlbumName = track.AlbumName,
                            SpotifyId = track.Id,
                            CanonicalId = track.CanonicalId,
                            ImageUrl = track.ImageUrl,
                            DurationMs = track.DurationMs,
                            TrackNumber = track.TrackNumber,
                            DiscNumber = track.DiscNumber,
                            StreamUrl = !string.IsNullOrEmpty(track.StreamUrl) ? $"{gatewayUrl}{track.StreamUrl}" : $"{gatewayUrl}/playback/{track.Id}",
                            AlbumId = albId,
                            ArtistId = artId
                        };
                        _cache.Set(id, entry);
                        _registrar.RegisterTrack(id, entry);
                        return entry;
                    }
                }
                catch { }

                // 2. Try resolving as album
                try
                {
                    var alb = await client.GetFromJsonAsync<GatewayAlbumResponse>($"{gatewayUrl}/jellyfin/album/{id:N}", fetchToken).ConfigureAwait(false);
                    if (alb != null && !string.IsNullOrEmpty(alb.Name))
                    {
                        var artId = !string.IsNullOrEmpty(alb.ArtistName) ? ResonoItemCache.StubGuid("dz-artist", alb.ArtistName) : (Guid?)null;
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "album",
                            Name = alb.Name,
                            ArtistName = alb.ArtistName,
                            SpotifyId = alb.Id,
                            ImageUrl = alb.ImageUrl,
                            ArtistId = artId
                        };
                        _cache.Set(id, entry);
                        _registrar.RegisterAlbum(id, alb.Name, alb.ArtistName);
                        return entry;
                    }
                }
                catch { }

                // 3. Try resolving as artist
                try
                {
                    var art = await client.GetFromJsonAsync<GatewayArtist>($"{gatewayUrl}/jellyfin/artist/{id:N}", fetchToken).ConfigureAwait(false);
                    if (art != null && !string.IsNullOrEmpty(art.Name))
                    {
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "artist",
                            Name = art.Name,
                            SpotifyId = art.Id,
                            ImageUrl = art.ImageUrl,
                            Id = id
                        };
                        _cache.Set(id, entry);
                        _registrar.RegisterArtist(id, art.Name);
                        return entry;
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Resono] Dynamic item resolve failed for {Id}: {Msg}", id, ex.Message);
            }

            return null;
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
        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }
        [JsonPropertyName("artistId")]
        public string? ArtistId { get; set; }
        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }
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
