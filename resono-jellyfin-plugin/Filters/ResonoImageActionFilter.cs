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
        private readonly ILogger<ResonoImageActionFilter> _logger;

        public ResonoImageActionFilter(
            ResonoItemCache cache,
            IHttpClientFactory httpClientFactory,
            ILogger<ResonoImageActionFilter> logger)
        {
            _cache = cache;
            _httpClientFactory = httpClientFactory;
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
                _logger.LogDebug(ex, "[Resono] Image filter passthrough on exception: {Message}", ex.Message);
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
            if (!_cache.TryGet(itemId, out var entry) || entry == null) return null;

            if (!string.IsNullOrEmpty(entry.ImageUrl))
            {
                return await FetchImageBytesOrRedirectAsync(entry.ImageUrl, ctx.HttpContext).ConfigureAwait(false);
            }

            // Dynamic artwork resolution for artists with missing image URL
            if (entry.Kind == "artist" && !string.IsNullOrEmpty(entry.Name))
            {
                try
                {
                    var cfg = Plugin.Instance?.Configuration;
                    var gatewayUrl = ResonoSearchActionFilter.GetEffectiveGatewayUrl(cfg?.GatewayUrl);
                    var client = _httpClientFactory.CreateClient();
                    var idParam = !string.IsNullOrEmpty(entry.SpotifyId) ? entry.SpotifyId : entry.Name;
                    var art = await client.GetFromJsonAsync<GatewayArtist>($"{gatewayUrl}/jellyfin/artist/{Uri.EscapeDataString(idParam)}", ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (art != null && !string.IsNullOrEmpty(art.ImageUrl))
                    {
                        entry.ImageUrl = art.ImageUrl;
                        _cache.Set(itemId, entry);
                        return await FetchImageBytesOrRedirectAsync(entry.ImageUrl, ctx.HttpContext).ConfigureAwait(false);
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
                return new FileContentResult(cached.Bytes, cached.ContentType);
            }

            try
            {
                var client = _httpClientFactory.CreateClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

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
                        return new FileContentResult(bytes, contentType);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Resono] Direct image byte download failed for {Url}: {Msg}", imageUrl, ex.Message);
            }

            // Fallback to HTTP 302 if direct bytes could not be fetched
            return new RedirectResult(imageUrl, permanent: false);
        }
    }
}
