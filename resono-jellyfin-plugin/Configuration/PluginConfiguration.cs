using MediaBrowser.Model.Plugins;

namespace Resono.Plugin.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string GatewayUrl { get; set; } = "http://localhost:8080";
        public string CatalogProvider { get; set; } = "deezer";
        public string FallbackCatalogProvider { get; set; } = "apple";
        public string PrimaryPlaybackSource { get; set; } = "deezer";
        public string FallbackPlaybackSource { get; set; } = "soulseek";
        public string DeezerArl { get; set; } = "";
        public bool EnableDirectPlay { get; set; } = true;
        public bool EnableSearchInjection { get; set; } = true;
        public bool EnableSearchCache { get; set; } = true;
        public bool EnableVirtualPlaylists { get; set; } = true;
        public bool EnableSyncedLyrics { get; set; } = true;
        public string ChartCountryCode { get; set; } = "PE";
        public int SearchLimit { get; set; } = 20;
        public int PrefetchBufferTracks { get; set; } = 2;

        public PluginConfiguration()
        {
        }
    }
}
