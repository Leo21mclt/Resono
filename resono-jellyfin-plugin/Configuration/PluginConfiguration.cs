using MediaBrowser.Model.Plugins;

namespace Resono.Plugin.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string GatewayUrl { get; set; } = "http://localhost:8080";
        public bool EnableDirectPlay { get; set; } = true;
        public bool EnableSearchInjection { get; set; } = true;
        public int SearchLimit { get; set; } = 20;
        public int PrefetchBufferTracks { get; set; } = 2;

        public PluginConfiguration()
        {
        }
    }
}
