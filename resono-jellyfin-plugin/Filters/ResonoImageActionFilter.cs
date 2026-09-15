using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Resono.Plugin.Services;

namespace Resono.Plugin.Filters
{
    public class ResonoImageActionFilter : IAsyncActionFilter
    {
        private static readonly ConcurrentDictionary<string, (DateTime Added, byte[] Bytes, string ContentType)> _imageCache = new(StringComparer.OrdinalIgnoreCase);
        private const int MaxCachedImages = 400;

        private readonly ResonoItemCache _cache;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
        private readonly ILogger<ResonoImageActionFilter> _logger;

        public ResonoImageActionFilter(
            ResonoItemCache cache,
            IHttpClientFactory httpClientFactory,
            MediaBrowser.Controller.Library.ILibraryManager libraryManager,
            ILogger<ResonoImageActionFilter> logger)
        {
            _cache = cache;
            _httpClientFactory = httpClientFactory;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
        {
            try
            {
                var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
                var route = ctx.RouteData.Values;
                var controller = route.TryGetValue("controller", out var c) ? c?.ToString() : null;

                bool isImageRoute = path.IndexOf("/Images", StringComparison.OrdinalIgnoreCase) >= 0
                    || string.Equals(controller, "Image", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(controller, "Images", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(controller, "ItemImage", StringComparison.OrdinalIgnoreCase);

                if (!isImageRoute)
                {
                    await next().ConfigureAwait(false);
                    return;
                }

                // 1. Check if this is an /Artists/{name}/Images/... route (used by Discrete & iOS for artist profiles)
                string? artistName = null;
                if (route.TryGetValue("name", out var nVal) && nVal != null)
                {
                    artistName = nVal.ToString();
                }
                else if (route.TryGetValue("artistName", out var anVal) && anVal != null)
                {
                    artistName = anVal.ToString();
                }
                else if (path.StartsWith("/Artists/", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && !string.Equals(parts[1], "AlbumArtists", StringComparison.OrdinalIgnoreCase))
                    {
                        artistName = Uri.UnescapeDataString(parts[1]);
                    }
                }

                if (!string.IsNullOrEmpty(artistName) && !Guid.TryParse(artistName, out _))
                {
                    var artistResult = await ServeArtistImageByNameAsync(ctx, artistName).ConfigureAwait(false);
                    if (artistResult != null)
                    {
                        ctx.Result = artistResult;
                        return;
                    }
                }

                // 2. Standard /Items/{id}/Images/... or /Artists/{guid}/Images/...
                string? idStr = null;
                foreach (var key in new[] { "itemId", "ItemId", "id", "Id" })
                {
                    if (route.TryGetValue(key, out var v) && v is not null)
                    {
                        idStr = v.ToString();
                        break;
                    }
                }
                if (idStr is null && ctx.HttpContext.Request.RouteValues.TryGetValue("itemId", out var rv))
                {
                    idStr = rv?.ToString();
                }
                if (idStr is null)
                {
                    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var s in segments)
                    {
                        var clean = s.IndexOf('.') >= 0 ? s.Split('.')[0] : s;
                        if (Guid.TryParse(clean, out _)) { idStr = clean; break; }
                    }
                }

                if (idStr is not null && Guid.TryParse(idStr, out var itemId))
                {
                    _logger.LogInformation("[Resono] Received image request for ItemId: {Id} (Route: {Path})", itemId, path);
                    var itemResult = await ServeItemImageByIdAsync(ctx, itemId).ConfigureAwait(false);
                    if (itemResult != null)
                    {
                        ctx.Result = itemResult;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Resono] Image filter error on request: {Message}", ex.Message);
            }

            await next().ConfigureAwait(false);
        }

        private async Task<IActionResult?> ServeArtistImageByNameAsync(ActionExecutingContext ctx, string artistName)
        {
            try
            {
                if (!_cache.TryGetArtistByName(artistName, out var artEntry) || artEntry == null || string.IsNullOrEmpty(artEntry.ImageUrl))
                {
                    var cfg = Plugin.Instance?.Configuration;
                    var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg?.GatewayUrl);
                    var client = _httpClientFactory.CreateClient();
                    var art = await client.GetFromJsonAsync<GatewayArtist>($"{gatewayUrl}/jellyfin/artist/{Uri.EscapeDataString(artistName)}", ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (art != null && !string.IsNullOrEmpty(art.ImageUrl))
                    {
                        var artId = ResonoItemCache.StubGuid("dz-artist", artistName);
                        artEntry = new ResonoItemCache.Entry
                        {
                            Kind = "artist",
                            Name = artistName,
                            SpotifyId = art.Id,
                            ImageUrl = art.ImageUrl,
                            Id = artId
                        };
                        _cache.Set(artId, artEntry);
                    }
                }

                if (artEntry != null && !string.IsNullOrEmpty(artEntry.ImageUrl))
                {
                    _logger.LogInformation("[Resono] Serving artist image by name for '{Artist}'", artistName);
                    return await FetchImageBytesOrRedirectAsync(artEntry.ImageUrl, ctx.HttpContext).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Resono] Failed resolving artist image for '{Artist}': {Msg}", artistName, ex.Message);
            }

            return null;
        }

        private async Task<IActionResult?> ServeItemImageByIdAsync(ActionExecutingContext ctx, Guid itemId)
        {
            _cache.TryGet(itemId, out var entry);

            // 1. Direct hit in cache with valid ImageUrl
            if (entry != null && !string.IsNullOrEmpty(entry.ImageUrl))
            {
                _logger.LogInformation("[Resono] Serving image for known item {Id} ({Kind}: '{Name}')", itemId, entry.Kind, entry.Name);
                return await FetchImageBytesOrRedirectAsync(entry.ImageUrl, ctx.HttpContext).ConfigureAwait(false);
            }

            var cfg = Plugin.Instance?.Configuration;
            var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg?.GatewayUrl);
            var client = _httpClientFactory.CreateClient();

            // 1.5 Fallback to Jellyfin LibraryManager if item was registered in library
            if (entry == null || string.IsNullOrEmpty(entry.ImageUrl))
            {
                try
                {
                    var libItem = _libraryManager.GetItemById(itemId);
                    if (libItem is MediaBrowser.Controller.Entities.Audio.MusicAlbum albItem && !string.IsNullOrEmpty(albItem.Name))
                    {
                        var searchUrl = $"{gatewayUrl}/jellyfin/search?q={Uri.EscapeDataString(albItem.Name)}&limit=3";
                        var searchRes = await client.GetFromJsonAsync<GatewaySearchResponse>(searchUrl, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                        var match = searchRes?.Albums?.FirstOrDefault(a => string.Equals(a.Name, albItem.Name, StringComparison.OrdinalIgnoreCase)) ?? searchRes?.Albums?.FirstOrDefault();
                        if (match != null && !string.IsNullOrEmpty(match.ImageUrl))
                        {
                            entry = new ResonoItemCache.Entry
                            {
                                Kind = "album",
                                Name = albItem.Name,
                                ArtistName = albItem.AlbumArtist ?? albItem.Artists.FirstOrDefault(),
                                SpotifyId = match.Id,
                                ImageUrl = match.ImageUrl
                            };
                            _cache.Set(itemId, entry);
                            _logger.LogInformation("[Resono] Resolved artwork for library album '{Album}' ({Id}) via search", albItem.Name, itemId);
                            return await FetchImageBytesOrRedirectAsync(match.ImageUrl, ctx.HttpContext).ConfigureAwait(false);
                        }
                    }
                    else if (libItem is MediaBrowser.Controller.Entities.Audio.Audio audioItem && !string.IsNullOrEmpty(audioItem.Name))
                    {
                        var searchUrl = $"{gatewayUrl}/jellyfin/search?q={Uri.EscapeDataString(audioItem.Name)}&limit=3";
                        var searchRes = await client.GetFromJsonAsync<GatewaySearchResponse>(searchUrl, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                        var match = searchRes?.Tracks?.FirstOrDefault(t => string.Equals(t.Name, audioItem.Name, StringComparison.OrdinalIgnoreCase)) ?? searchRes?.Tracks?.FirstOrDefault();
                        if (match != null && !string.IsNullOrEmpty(match.ImageUrl))
                        {
                            entry = new ResonoItemCache.Entry
                            {
                                Kind = "track",
                                Name = audioItem.Name,
                                ArtistName = audioItem.Artists.FirstOrDefault(),
                                AlbumName = audioItem.Album,
                                SpotifyId = match.Id,
                                ImageUrl = match.ImageUrl
                            };
                            _cache.Set(itemId, entry);
                            _logger.LogInformation("[Resono] Resolved artwork for library audio '{Track}' ({Id}) via search", audioItem.Name, itemId);
                            return await FetchImageBytesOrRedirectAsync(match.ImageUrl, ctx.HttpContext).ConfigureAwait(false);
                        }
                    }
                }
                catch { }
            }

            // 2. Dynamic artwork resolution for artists
            if (entry != null && entry.Kind == "artist" && !string.IsNullOrEmpty(entry.Name))
            {
                try
                {
                    var idParam = !string.IsNullOrEmpty(entry.SpotifyId) ? entry.SpotifyId : entry.Name;
                    var art = await client.GetFromJsonAsync<GatewayArtist>($"{gatewayUrl}/jellyfin/artist/{Uri.EscapeDataString(idParam)}", ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (art != null && !string.IsNullOrEmpty(art.ImageUrl))
                    {
                        entry.ImageUrl = art.ImageUrl;
                        _cache.Set(itemId, entry);
                        _logger.LogInformation("[Resono] Resolved dynamic artist artwork for '{Artist}'", entry.Name);
                        return await FetchImageBytesOrRedirectAsync(entry.ImageUrl, ctx.HttpContext).ConfigureAwait(false);
                    }
                }
                catch { }
            }

            // 3. Dynamic artwork resolution for albums (missing from cache or missing ImageUrl)
            if (entry == null || entry.Kind == "album")
            {
                try
                {
                    var alb = await client.GetFromJsonAsync<GatewayAlbumResponse>($"{gatewayUrl}/jellyfin/album/{itemId:N}", ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (alb != null && !string.IsNullOrEmpty(alb.ImageUrl))
                    {
                        if (entry == null)
                        {
                            var artId = !string.IsNullOrEmpty(alb.ArtistName) ? ResonoItemCache.StubGuid("dz-artist", alb.ArtistName) : (Guid?)null;
                            entry = new ResonoItemCache.Entry
                            {
                                Kind = "album",
                                Name = alb.Name,
                                ArtistName = alb.ArtistName,
                                SpotifyId = alb.Id,
                                ImageUrl = alb.ImageUrl,
                                ArtistId = artId
                            };
                            _cache.Set(itemId, entry);
                        }
                        else
                        {
                            entry.ImageUrl = alb.ImageUrl;
                            _cache.Set(itemId, entry);
                        }
                        _logger.LogInformation("[Resono] Resolved dynamic album artwork for '{Album}' ({Id})", alb.Name, itemId);
                        return await FetchImageBytesOrRedirectAsync(alb.ImageUrl, ctx.HttpContext).ConfigureAwait(false);
                    }
                }
                catch { }
            }

            // 4. Dynamic artwork resolution for tracks (missing from cache or missing ImageUrl)
            if (entry == null || entry.Kind == "track")
            {
                try
                {
                    var track = await client.GetFromJsonAsync<GatewayTrack>($"{gatewayUrl}/jellyfin/track/{itemId:N}", ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (track != null && !string.IsNullOrEmpty(track.ImageUrl))
                    {
                        if (entry == null)
                        {
                            var albId = !string.IsNullOrEmpty(track.AlbumId) ? ResonoItemCache.StubGuid("dz-album", track.AlbumId) : (Guid?)null;
                            var artId = !string.IsNullOrEmpty(track.ArtistId) ? ResonoItemCache.StubGuid("dz-artist", track.ArtistId) : (Guid?)null;
                            entry = new ResonoItemCache.Entry
                            {
                                Kind = "track",
                                Name = track.Name,
                                ArtistName = track.ArtistName,
                                AlbumName = track.AlbumName,
                                SpotifyId = track.Id,
                                ImageUrl = track.ImageUrl,
                                AlbumId = albId,
                                ArtistId = artId
                            };
                            _cache.Set(itemId, entry);
                        }
                        else
                        {
                            entry.ImageUrl = track.ImageUrl;
                            _cache.Set(itemId, entry);
                        }
                        _logger.LogInformation("[Resono] Resolved dynamic track artwork for '{Track}' ({Id})", track.Name, itemId);
                        return await FetchImageBytesOrRedirectAsync(track.ImageUrl, ctx.HttpContext).ConfigureAwait(false);
                    }
                }
                catch { }
            }

            return null;
        }

        private async Task<IActionResult> FetchImageBytesOrRedirectAsync(string imageUrl, Microsoft.AspNetCore.Http.HttpContext httpContext)
        {
            // 1. Fast in-memory byte cache to avoid hitting upstream CDN on every thumbnail render
            if (_imageCache.TryGetValue(imageUrl, out var cached))
            {
                httpContext.Response.Headers["Cache-Control"] = "public, max-age=604800, immutable";
                _logger.LogInformation("[Resono] Delivered cached direct image bytes ({Bytes} bytes) for {Url}", cached.Bytes.Length, imageUrl);
                return new FileContentResult(cached.Bytes, cached.ContentType);
            }

            try
            {
                var client = _httpClientFactory.CreateClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(6));

                var resp = await client.GetAsync(imageUrl, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                    var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                    if (bytes.Length > 0)
                    {
                        if (_imageCache.Count > MaxCachedImages)
                        {
                            _imageCache.Clear();
                        }
                        _imageCache[imageUrl] = (DateTime.UtcNow, bytes, contentType);

                        httpContext.Response.Headers["Cache-Control"] = "public, max-age=604800, immutable";
                        _logger.LogInformation("[Resono] Downloaded and delivered direct image bytes ({Bytes} bytes) for {Url}", bytes.Length, imageUrl);
                        return new FileContentResult(bytes, contentType);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Resono] Direct image byte download failed for {Url}: {Msg}", imageUrl, ex.Message);
            }

            // Fallback to HTTP 302 if direct bytes could not be fetched
            _logger.LogInformation("[Resono] Fallback to HTTP 302 for {Url}", imageUrl);
            return new RedirectResult(imageUrl, permanent: false);
        }
    }
}
