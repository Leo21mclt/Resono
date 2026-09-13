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

        public ResonoMediaSourceProvider(ILogger<ResonoMediaSourceProvider> logger)
        {
            _logger = logger;
        }

        public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            var sources = new List<MediaSourceInfo>();

            if (item is Audio audio)
            {
                string? spotifyId = null;
                if (audio.ProviderIds.TryGetValue("Spotify", out var spId))
                {
                    spotifyId = spId;
                }
                else if (audio.ProviderIds.TryGetValue("Resono", out var resId))
                {
                    spotifyId = resId;
                }

                if (!string.IsNullOrEmpty(spotifyId))
                {
                    var gatewayUrl = Plugin.Instance?.Configuration.GatewayUrl?.TrimEnd('/') ?? "http://localhost:8080";
                    var streamUrl = $"{gatewayUrl}/playback/spotify:track:{spotifyId}";

                    _logger.LogInformation("Delivering Resono media source for track '{Name}' -> {StreamUrl}", audio.Name, streamUrl);

                    var sourceInfo = new MediaSourceInfo
                    {
                        Id = audio.Id.ToString(),
                        Path = streamUrl,
                        Protocol = MediaProtocol.Http,
                        Container = "mp3",
                        SupportsDirectPlay = true,
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
