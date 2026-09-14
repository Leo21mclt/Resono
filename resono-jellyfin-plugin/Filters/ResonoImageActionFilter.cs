using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Resono.Plugin.Services;

namespace Resono.Plugin.Filters
{
    public class ResonoImageActionFilter : IAsyncActionFilter
    {
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

                bool isImageRoute = path.IndexOf("/Images/", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("/Images", StringComparison.OrdinalIgnoreCase) >= 0
                    || string.Equals(controller, "Image", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(controller, "Images", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(controller, "ItemImage", StringComparison.OrdinalIgnoreCase);

                if (!isImageRoute)
                {
                    await next().ConfigureAwait(false);
                    return;
                }

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
                    if (_cache.TryGet(itemId, out var entry) && entry != null)
                    {
                        if (!string.IsNullOrEmpty(entry.ImageUrl))
                        {
                            ctx.Result = new RedirectResult(entry.ImageUrl);
                            return;
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
                                    ctx.Result = new RedirectResult(entry.ImageUrl);
                                    return;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Dynamic artist art resolution failed for {Name}", entry.Name);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Resono image filter passthrough: {Message}", ex.Message);
            }

            await next().ConfigureAwait(false);
        }
    }
}
