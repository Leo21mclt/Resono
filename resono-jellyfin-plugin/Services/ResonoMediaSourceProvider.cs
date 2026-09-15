using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Resono.Plugin.Services
{
    public class ResonoMediaSourceProvider : IMediaSourceProvider
    {
        public string Name => "Resono Playback";

        public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            if (item is Audio audio && (audio.Path?.StartsWith("/data/music/resono/") == true || audio.Tags?.Contains("ResonoVirtual") == true))
            {
                var idStr = audio.Id.ToString("N");
                var src = new MediaSourceInfo
                {
                    Id = idStr,
                    Path = $"/data/music/resono/{idStr}.mp3",
                    Protocol = MediaProtocol.File,
                    IsRemote = false,
                    Type = MediaSourceType.Default,
                    Name = audio.Name ?? "Track",
                    Container = "mp3",
                    SupportsTranscoding = true,
                    SupportsDirectStream = true,
                    SupportsDirectPlay = true,
                    RequiresOpening = false,
                    RequiresClosing = false,
                    RunTimeTicks = audio.RunTimeTicks,
                    Bitrate = 320000,
                    DefaultAudioStreamIndex = 0,
                    MediaStreams = new List<MediaStream>
                    {
                        new MediaStream
                        {
                            Codec = "mp3",
                            Type = MediaStreamType.Audio,
                            Index = 0,
                            IsDefault = true,
                            BitRate = 320000,
                            SampleRate = 44100,
                            Channels = 2,
                            ChannelLayout = "stereo"
                        }
                    },
                    RequiredHttpHeaders = new Dictionary<string, string>()
                };
                return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { src });
            }

            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
            => Task.FromResult<ILiveStream>(null!);
    }
}
