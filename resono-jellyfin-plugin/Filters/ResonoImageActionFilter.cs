using System;
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
        private readonly ILogger<ResonoImageActionFilter> _logger;

        public ResonoImageActionFilter(
            ResonoItemCache cache,
            ILogger<ResonoImageActionFilter> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        public Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
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

                if (!isImageRoute) return next();

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
                        // Direct 302 redirect to CDN (Apple Music / Deezer CDN)
                        // Browser downloads high-res artwork directly in <20ms
                        ctx.Result = new RedirectResult(entry.ImageUrl);
                        return Task.CompletedTask;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Resono image filter passthrough: {Message}", ex.Message);
            }

            return next();
        }
    }
}
