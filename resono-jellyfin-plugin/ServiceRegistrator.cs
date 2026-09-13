using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Resono.Plugin.Filters;
using Resono.Plugin.Services;

namespace Resono.Plugin
{
    public class ServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
        {
            Plugin.ServerSystemId = host.SystemId;

            services.AddHttpClient();
            services.AddSingleton<ResonoItemCache>();

            services.Configure<MvcOptions>(opts =>
            {
                opts.Filters.Add<ResonoSearchActionFilter>();
                opts.Filters.Add<ResonoItemDetailActionFilter>();
                opts.Filters.Add<ResonoImageActionFilter>();
            });
        }
    }
}
