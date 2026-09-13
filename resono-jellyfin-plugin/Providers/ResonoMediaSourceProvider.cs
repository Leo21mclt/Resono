using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Resono.Plugin.Providers
{
    public class ResonoMediaSourceProvider : IMediaSourceProvider
    {
        private readonly ILogger<ResonoMediaSourceProvider> _logger;

        private readonly Resono.Plugin.Services.ResonoItemCache _cache;

        public ResonoMediaSourceProvider(
            Resono.Plugin.Services.ResonoItemCache cache,
            ILogger<ResonoMediaSourceProvider> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            var sources = new List<MediaSourceInfo>();

            if (item is Audio audio)
            {
                var gatewayUrl = Plugin.Instance?.Configuration.GatewayUrl?.TrimEnd('/') ?? "http://localhost:8080";
                string? streamUrl = null;

                if (_cache.TryGet(audio.Id, out var entry) && !string.IsNullOrEmpty(entry?.StreamUrl))
                {
                    streamUrl = entry.StreamUrl;
                }
                else if (audio.ProviderIds.TryGetValue("Spotify", out var spId) && !string.IsNullOrEmpty(spId))
                {
                    streamUrl = $"{gatewayUrl}/playback/spotify:track:{spId}";
                }
                else if (audio.ProviderIds.TryGetValue("Resono", out var resId) && !string.IsNullOrEmpty(resId))
                {
                    streamUrl = $"{gatewayUrl}/playback/{resId}";
                }

                if (!string.IsNullOrEmpty(streamUrl))
                {
                    _logger.LogInformation("Delivering Resono media source for track '{Name}' -> {StreamUrl}", audio.Name, streamUrl);

                    var sourceInfo = new MediaSourceInfo
                    {
                        Id = audio.Id.ToString(),
                        Path = streamUrl,
                        Protocol = MediaProtocol.Http,
                        Container = "mp3",
                        SupportsDirectPlay = false,
                        SupportsDirectStream = true,
                        SupportsTranscoding = true,
                        IsInfiniteStream = false,
                        Name = audio.Name
                    };

                    sources.Add(sourceInfo);
                }
            }

            return Task.FromResult<IEnumerable<MediaSourceInfo>>(sources);
        }

        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("Live stream open not required for HTTP audio media sources.");
        }
    }
}
