using System;
using System.Net.Http;
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
                var route = ctx.RouteData.Values;
                var controller = route.TryGetValue("controller", out var c) ? c?.ToString() : null;
                if (!string.Equals(controller, "Image", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(controller, "Images", StringComparison.OrdinalIgnoreCase))
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

                if (idStr is not null && Guid.TryParse(idStr, out var itemId))
                {
                    if (_cache.TryGet(itemId, out var entry) && !string.IsNullOrEmpty(entry?.ImageUrl))
                    {
                        var cfg = Plugin.Instance?.Configuration;
                        var gatewayUrl = cfg?.GatewayUrl?.TrimEnd('/') ?? "http://localhost:8080";
                        var proxyUrl = $"{gatewayUrl}/jellyfin/image?url={Uri.EscapeDataString(entry.ImageUrl)}";

                        var client = _httpClientFactory.CreateClient();
                        var imageBytes = await client.GetByteArrayAsync(proxyUrl).ConfigureAwait(false);

                        ctx.HttpContext.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                        ctx.Result = new FileContentResult(imageBytes, "image/jpeg");
                        return;
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
