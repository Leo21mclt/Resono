using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Resono.Plugin.Providers
{
    public class ResonoSearchProvider : IRemoteSearchProvider<SongInfo>
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ResonoSearchProvider> _logger;

        public string Name => "Resono Virtual Music";

        public ResonoSearchProvider(IHttpClientFactory httpClientFactory, ILogger<ResonoSearchProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SongInfo searchInfo, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();
            var query = searchInfo.Name;
            if (string.IsNullOrWhiteSpace(query))
            {
                return results;
            }

            var gatewayUrl = Resono.Plugin.Filters.ResonoSearchActionFilter.GetEffectiveGatewayUrl(Plugin.Instance?.Configuration.GatewayUrl);
            var url = $"{gatewayUrl}/jellyfin/search?q={Uri.EscapeDataString(query)}";

            try
            {
                var client = _httpClientFactory.CreateClient();
                var response = await client.GetFromJsonAsync<GatewaySearchResponse>(url, cancellationToken);
                if (response?.Tracks != null)
                {
                    foreach (var track in response.Tracks)
                    {
                        var item = new RemoteSearchResult
                        {
                            Name = track.Name,
                            ImageUrl = track.ImageUrl,
                            SearchProviderName = Name
                        };

                        if (track.ProviderIds != null)
                        {
                            foreach (var kvp in track.ProviderIds)
                            {
                                item.SetProviderId(kvp.Key, kvp.Value);
                            }
                        }

                        if (!string.IsNullOrEmpty(track.CanonicalId))
                        {
                            item.SetProviderId("Resono", track.CanonicalId);
                        }

                        results.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to execute search against Resono Gateway: {Message}", ex.Message);
            }

            return results;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();
            return client.GetAsync(url, cancellationToken);
        }
    }

    public class GatewaySearchResponse
    {
        [JsonPropertyName("artists")]
        public List<GatewayArtistResult>? Artists { get; set; }

        [JsonPropertyName("albums")]
        public List<GatewayAlbumResult>? Albums { get; set; }

        [JsonPropertyName("tracks")]
        public List<GatewayTrackResult>? Tracks { get; set; }
    }

    public class GatewayArtistResult
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("providerIds")]
        public Dictionary<string, string>? ProviderIds { get; set; }
    }

    public class GatewayAlbumResult
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }

        [JsonPropertyName("releaseDate")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("providerIds")]
        public Dictionary<string, string>? ProviderIds { get; set; }
    }

    public class GatewayTrackResult
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("canonicalId")]
        public string? CanonicalId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }

        [JsonPropertyName("albumName")]
        public string? AlbumName { get; set; }

        [JsonPropertyName("durationMs")]
        public int DurationMs { get; set; }

        [JsonPropertyName("trackNumber")]
        public int TrackNumber { get; set; }

        [JsonPropertyName("discNumber")]
        public int DiscNumber { get; set; }

        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("streamUrl")]
        public string? StreamUrl { get; set; }

        [JsonPropertyName("providerIds")]
        public Dictionary<string, string>? ProviderIds { get; set; }
    }
}
