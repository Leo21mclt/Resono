using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Resono.Plugin.Services;

namespace Resono.Plugin.Filters
{
    public class ResonoSearchActionFilter : IAsyncResultFilter
    {
        private static readonly ConcurrentDictionary<string, (DateTime Expires, GatewaySearchResponse Data)> _searchMemoryCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Lazy<Task<GatewaySearchResponse?>>> _inFlightSearches = new(StringComparer.OrdinalIgnoreCase);
        private static string? _cachedEffectiveGatewayUrl;

        public static string GetEffectiveGatewayUrl(string? configured)
        {
            if (!string.IsNullOrEmpty(_cachedEffectiveGatewayUrl)) return _cachedEffectiveGatewayUrl;

            var url = configured?.TrimEnd('/') ?? "http://localhost:8080";
            if (url.Contains("localhost") || url.Contains("127.0.0.1"))
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
                    var res = client.GetAsync("http://resono-gateway:8080/health").GetAwaiter().GetResult();
                    if (res.IsSuccessStatusCode)
                    {
                        _cachedEffectiveGatewayUrl = "http://resono-gateway:8080";
                        return _cachedEffectiveGatewayUrl;
                    }
                }
                catch { }
            }

            _cachedEffectiveGatewayUrl = url;
            return url;
        }

        private static readonly HashSet<string> MusicTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Audio", "MusicAlbum", "MusicArtist", "AudioBook", "Album", "Artist", "AlbumArtist", "Playlist", "Song"
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ResonoItemCache _cache;
        private readonly ResonoLibraryRegistrar _registrar;
        private readonly ResonoRecentlyPlayedTracker _recentlyPlayed;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<ResonoSearchActionFilter> _logger;

        public ResonoSearchActionFilter(
            IHttpClientFactory httpClientFactory,
            ResonoItemCache cache,
            ResonoLibraryRegistrar registrar,
            ResonoRecentlyPlayedTracker recentlyPlayed,
            IAuthorizationContext authContext,
            ILogger<ResonoSearchActionFilter> logger)
        {
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _registrar = registrar;
            _recentlyPlayed = recentlyPlayed;
            _authContext = authContext;
            _logger = logger;
        }

        public async Task OnResultExecutionAsync(ResultExecutingContext ctx, ResultExecutionDelegate next)
        {
            try
            {
                if (ShouldInject(ctx, out var searchTerm))
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.HttpContext.RequestAborted);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    await TryAugmentSearchAsync(ctx, searchTerm!, cts.Token).ConfigureAwait(false);
                }
                else if (ShouldAugmentRecentlyPlayed(ctx))
                {
                    TryAugmentRecentlyPlayed(ctx);
                }
                else if (ShouldAugmentPlaylists(ctx))
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.HttpContext.RequestAborted);
                    cts.CancelAfter(TimeSpan.FromSeconds(4));
                    await TryAugmentPlaylistsAsync(ctx, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resono search response augmentation failed: {Message}", ex.Message);
            }

            await next().ConfigureAwait(false);
        }

        private bool ShouldInject(ResultExecutingContext ctx, out string? term)
        {
            term = null;
            var cfg = Plugin.Instance?.Configuration;
            if (cfg is null || !cfg.EnableSearchInjection) return false;

            var controller = ctx.RouteData.Values.TryGetValue("controller", out var c) ? c?.ToString() : null;
            var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;

            bool isSupportedController = string.Equals(controller, "Items", StringComparison.OrdinalIgnoreCase)
                || string.Equals(controller, "Search", StringComparison.OrdinalIgnoreCase)
                || string.Equals(controller, "Artists", StringComparison.OrdinalIgnoreCase)
                || string.Equals(controller, "UserLibrary", StringComparison.OrdinalIgnoreCase);

            bool isSupportedPath = path.IndexOf("/Search", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("/Items", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("/Artists", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isSupportedController && !isSupportedPath)
                return false;

            term = ExtractSearchTerm(ctx.HttpContext);
            if (string.IsNullOrWhiteSpace(term) || term.Length < 2) return false;

            if (string.Equals(controller, "Artists", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/Artists", StringComparison.OrdinalIgnoreCase))
                return true;

            var types = ExtractIncludeItemTypes(ctx.HttpContext);
            if (types.Count > 0 && !types.Any(t => MusicTypes.Contains(t))) return false;

            return true;
        }

        private static string? ExtractSearchTerm(HttpContext http)
        {
            var q = http.Request.Query;
            foreach (var key in new[] { "searchTerm", "SearchTerm", "nameStartsWithOrGreater", "nameStartsWith", "NameStartsWith", "search", "Search", "q", "Q" })
            {
                if (q.TryGetValue(key, out var st) && !string.IsNullOrWhiteSpace(st)) return st.ToString();
            }
            return null;
        }

        public static HashSet<string> ExtractIncludeItemTypes(HttpContext http)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "includeItemTypes", "IncludeItemTypes" })
            {
                if (http.Request.Query.TryGetValue(key, out var val))
                {
                    foreach (var s in val.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        set.Add(s);
                    }
                }
            }
            return set;
        }

        private async Task TryAugmentSearchAsync(ResultExecutingContext ctx, string term, CancellationToken ct)
        {
            if (ctx.Result is not ObjectResult or || or.Value is null) return;

            var cfg = Plugin.Instance!.Configuration;
            var gatewayUrl = GetEffectiveGatewayUrl(cfg.GatewayUrl);
            var provider = !string.IsNullOrWhiteSpace(cfg.CatalogProvider) ? cfg.CatalogProvider : "deezer";
            var fallback = !string.IsNullOrWhiteSpace(cfg.FallbackCatalogProvider) ? cfg.FallbackCatalogProvider : "apple";
            var limit = cfg.SearchLimit > 0 ? cfg.SearchLimit : 20;

            GatewaySearchResponse? searchData = null;
            var cacheKey = $"{provider}:{fallback}:{limit}:{term.Trim().ToLowerInvariant()}";

            if (cfg.EnableSearchCache && _searchMemoryCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expires)
            {
                searchData = cached.Data;
            }
            else
            {
                var lazy = _inFlightSearches.GetOrAdd(cacheKey, key => new Lazy<Task<GatewaySearchResponse?>>(() => FetchGatewaySearchAsync(gatewayUrl, term, limit, provider, fallback, cfg.EnableSearchCache, cfg.DeezerArl, ct)));
                try
                {
                    searchData = await lazy.Value.ConfigureAwait(false);
                }
                finally
                {
                    _inFlightSearches.TryRemove(cacheKey, out _);
                }
            }

            if (searchData is null) return;

            switch (or.Value)
            {
                case QueryResult<BaseItemDto> qr:
                    AugmentItems(qr, searchData, gatewayUrl, ctx.HttpContext);
                    break;
                case SearchHintResult sr:
                    or.Value = AugmentHints(sr, searchData, gatewayUrl);
                    break;
            }
        }

        private async Task<GatewaySearchResponse?> FetchGatewaySearchAsync(string gatewayUrl, string term, int limit, string provider, string fallback, bool enableCache, string? deezerArl, CancellationToken ct)
        {
            try
            {
                var url = $"{gatewayUrl}/jellyfin/search?q={Uri.EscapeDataString(term)}&limit={limit}&provider={Uri.EscapeDataString(provider)}&fallback={Uri.EscapeDataString(fallback)}";
                var client = _httpClientFactory.CreateClient();
                if (!string.IsNullOrWhiteSpace(deezerArl))
                {
                    client.DefaultRequestHeaders.Remove("X-Deezer-Arl");
                    client.DefaultRequestHeaders.Add("X-Deezer-Arl", deezerArl.Trim());
                }
                var data = await client.GetFromJsonAsync<GatewaySearchResponse>(url, ct).ConfigureAwait(false);
                if (data != null && enableCache)
                {
                    var cacheKey = $"{provider}:{fallback}:{limit}:{term.Trim().ToLowerInvariant()}";
                    _searchMemoryCache[cacheKey] = (DateTime.UtcNow.AddMinutes(15), data);
                }
                return data;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query gateway search: {Message}", ex.Message);
                return null;
            }
        }

        private void AugmentItems(QueryResult<BaseItemDto> qr, GatewaySearchResponse data, string gatewayUrl, HttpContext httpCtx)
        {
            var existingIds = qr.Items.Select(i => i.Id).ToHashSet();
            var additions = new List<BaseItemDto>();

            var requestedTypes = ExtractIncludeItemTypes(httpCtx);
            bool hasTypeFilter = requestedTypes.Count > 0;
            bool wantArtists = !hasTypeFilter || requestedTypes.Contains("MusicArtist") || requestedTypes.Contains("Artist");
            bool wantAlbums = !hasTypeFilter || requestedTypes.Contains("MusicAlbum");
            bool wantTracks = !hasTypeFilter || requestedTypes.Contains("Audio") || requestedTypes.Contains("Song");

            // CRITICAL FOR MANET:
            // When querying /Artists or /Artists/AlbumArtists, strictly isolate to MusicArtist only!
            // Never allow Audio or MusicAlbum to leak into an Artists endpoint, which crashes Manet's Swift decoder.
            var path = httpCtx.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/Artists", StringComparison.OrdinalIgnoreCase))
            {
                wantArtists = true;
                wantAlbums = false;
                wantTracks = false;
            }

            // 1. Add Artists
            if (wantArtists && data.Artists != null)
            {
                foreach (var a in data.Artists)
                {
                    var id = ResonoItemCache.StubGuid("dz-artist", a.Id ?? a.Name ?? Guid.NewGuid().ToString());
                    var entry = new ResonoItemCache.Entry
                    {
                        Kind = "artist",
                        Name = a.Name,
                        SpotifyId = a.Id,
                        ImageUrl = a.ImageUrl
                    };
                    _cache.Set(id, entry);

                    if (existingIds.Add(id))
                    {
                        additions.Add(BuildArtistDto(id, entry));
                    }
                }
            }

            // 2. Add Albums
            if (wantAlbums && data.Albums != null)
            {
                foreach (var al in data.Albums)
                {
                    var id = ResonoItemCache.StubGuid("dz-album", al.Id ?? al.Name ?? Guid.NewGuid().ToString());
                    var artistId = !string.IsNullOrEmpty(al.ArtistId)
                        ? ResonoItemCache.StubGuid("dz-artist", al.ArtistId)
                        : (!string.IsNullOrEmpty(al.ArtistName) ? ResonoItemCache.StubGuid("dz-artist", al.ArtistName) : (Guid?)null);

                    if (artistId.HasValue && !string.IsNullOrEmpty(al.ArtistName))
                    {
                        if (!_cache.TryGet(artistId.Value, out _))
                        {
                            _cache.Set(artistId.Value, new ResonoItemCache.Entry
                            {
                                Kind = "artist",
                                Name = al.ArtistName,
                                SpotifyId = al.ArtistId,
                                ImageUrl = al.ImageUrl
                            });
                        }
                    }

                    var entry = new ResonoItemCache.Entry
                    {
                        Kind = "album",
                        Name = al.Name,
                        ArtistName = al.ArtistName,
                        SpotifyId = al.Id,
                        ImageUrl = al.ImageUrl,
                        ArtistId = artistId
                    };
                    _cache.Set(id, entry);

                    if (existingIds.Add(id))
                    {
                        additions.Add(BuildAlbumDto(id, entry));
                    }
                }
            }

            // 3. Add Tracks
            if (wantTracks && data.Tracks != null)
            {
                foreach (var t in data.Tracks)
                {
                    var id = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                        ? g : ResonoItemCache.StubGuid("dz-track", t.Id ?? t.Name ?? Guid.NewGuid().ToString());

                    var albumId = !string.IsNullOrEmpty(t.AlbumId)
                        ? ResonoItemCache.StubGuid("dz-album", t.AlbumId)
                        : (!string.IsNullOrEmpty(t.AlbumName) ? ResonoItemCache.StubGuid("dz-album", t.AlbumName + (t.ArtistName ?? "")) : (Guid?)null);

                    var artistId = !string.IsNullOrEmpty(t.ArtistId)
                        ? ResonoItemCache.StubGuid("dz-artist", t.ArtistId)
                        : (!string.IsNullOrEmpty(t.ArtistName) ? ResonoItemCache.StubGuid("dz-artist", t.ArtistName) : (Guid?)null);

                    // Ensure Album is cached with cover art so Discrete never gets 404 for album covers
                    if (albumId.HasValue && !string.IsNullOrEmpty(t.AlbumName))
                    {
                        if (!_cache.TryGet(albumId.Value, out _))
                        {
                            _cache.Set(albumId.Value, new ResonoItemCache.Entry
                            {
                                Kind = "album",
                                Name = t.AlbumName,
                                ArtistName = t.ArtistName,
                                SpotifyId = t.AlbumId,
                                ImageUrl = t.ImageUrl,
                                ArtistId = artistId
                            });
                        }
                    }

                    // Ensure Artist is cached with image
                    if (artistId.HasValue && !string.IsNullOrEmpty(t.ArtistName))
                    {
                        if (!_cache.TryGet(artistId.Value, out _))
                        {
                            _cache.Set(artistId.Value, new ResonoItemCache.Entry
                            {
                                Kind = "artist",
                                Name = t.ArtistName,
                                SpotifyId = t.ArtistId,
                                ImageUrl = t.ImageUrl,
                                Id = artistId.Value
                            });
                        }
                    }

                    if (existingIds.Add(id))
                    {
                        var streamUrl = !string.IsNullOrEmpty(t.StreamUrl)
                            ? $"{gatewayUrl}{t.StreamUrl}"
                            : $"{gatewayUrl}/playback/{t.Id}";

                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "track",
                            Name = t.Name,
                            ArtistName = t.ArtistName,
                            AlbumName = t.AlbumName,
                            SpotifyId = t.Id,
                            CanonicalId = t.CanonicalId,
                            ImageUrl = t.ImageUrl,
                            DurationMs = t.DurationMs,
                            TrackNumber = t.TrackNumber,
                            DiscNumber = t.DiscNumber,
                            StreamUrl = streamUrl,
                            AlbumId = albumId,
                            ArtistId = artistId
                        };
                        _cache.Set(id, entry);
                        _registrar.RegisterTrack(id, entry);
                        additions.Add(BuildTrackDto(id, entry));
                    }
                }
            }

            if (additions.Count > 0)
            {
                var combined = qr.Items.Concat(additions).ToArray();
                qr.Items = combined;
                qr.TotalRecordCount = combined.Length;
            }
        }

        private SearchHintResult AugmentHints(SearchHintResult sr, GatewaySearchResponse data, string gatewayUrl)
        {
            var existingIds = (sr.SearchHints ?? Array.Empty<SearchHint>()).Select(h => h.Id).ToHashSet();
            var additions = new List<SearchHint>();

            // 1. Artists
            if (data.Artists != null)
            {
                foreach (var a in data.Artists)
                {
                    var id = ResonoItemCache.StubGuid("dz-artist", a.Id ?? a.Name ?? Guid.NewGuid().ToString());
                    var entry = new ResonoItemCache.Entry
                    {
                        Kind = "artist",
                        Name = a.Name,
                        SpotifyId = a.Id,
                        ImageUrl = a.ImageUrl
                    };
                    _cache.Set(id, entry);

                    if (existingIds.Add(id))
                    {
                        additions.Add(new SearchHint
                        {
                            Id = id,
                            Name = a.Name,
                            Type = BaseItemKind.MusicArtist,
                            PrimaryImageTag = "resono-" + id.ToString("N")
                        });
                    }
                }
            }

            // 2. Albums
            if (data.Albums != null)
            {
                foreach (var al in data.Albums)
                {
                    var id = ResonoItemCache.StubGuid("dz-album", al.Id ?? al.Name ?? Guid.NewGuid().ToString());
                    var artistId = !string.IsNullOrEmpty(al.ArtistId)
                        ? ResonoItemCache.StubGuid("dz-artist", al.ArtistId)
                        : (!string.IsNullOrEmpty(al.ArtistName) ? ResonoItemCache.StubGuid("dz-artist", al.ArtistName) : (Guid?)null);

                    var entry = new ResonoItemCache.Entry
                    {
                        Kind = "album",
                        Name = al.Name,
                        ArtistName = al.ArtistName,
                        SpotifyId = al.Id,
                        ImageUrl = al.ImageUrl,
                        ArtistId = artistId
                    };
                    _cache.Set(id, entry);

                    if (existingIds.Add(id))
                    {
                        additions.Add(new SearchHint
                        {
                            Id = id,
                            Name = al.Name,
                            Type = BaseItemKind.MusicAlbum,
                            AlbumArtist = al.ArtistName,
                            PrimaryImageTag = "resono-" + id.ToString("N")
                        });
                    }
                }
            }

            // 3. Tracks
            if (data.Tracks != null)
            {
                foreach (var t in data.Tracks)
                {
                    var id = !string.IsNullOrEmpty(t.CanonicalId) && Guid.TryParse(t.CanonicalId, out var g)
                        ? g : ResonoItemCache.StubGuid("dz-track", t.Id ?? t.Name ?? Guid.NewGuid().ToString());

                    var albumId = !string.IsNullOrEmpty(t.AlbumId)
                        ? ResonoItemCache.StubGuid("dz-album", t.AlbumId)
                        : (!string.IsNullOrEmpty(t.AlbumName) ? ResonoItemCache.StubGuid("dz-album", t.AlbumName + (t.ArtistName ?? "")) : (Guid?)null);

                    var artistId = !string.IsNullOrEmpty(t.ArtistId)
                        ? ResonoItemCache.StubGuid("dz-artist", t.ArtistId)
                        : (!string.IsNullOrEmpty(t.ArtistName) ? ResonoItemCache.StubGuid("dz-artist", t.ArtistName) : (Guid?)null);

                    if (albumId.HasValue && !string.IsNullOrEmpty(t.AlbumName))
                    {
                        if (!_cache.TryGet(albumId.Value, out _))
                        {
                            _cache.Set(albumId.Value, new ResonoItemCache.Entry
                            {
                                Kind = "album",
                                Name = t.AlbumName,
                                ArtistName = t.ArtistName,
                                SpotifyId = t.AlbumId,
                                ImageUrl = t.ImageUrl,
                                ArtistId = artistId
                            });
                        }
                    }

                    var entry = new ResonoItemCache.Entry
                    {
                        Kind = "track",
                        Name = t.Name,
                        ArtistName = t.ArtistName,
                        AlbumName = t.AlbumName,
                        SpotifyId = t.Id,
                        CanonicalId = t.CanonicalId,
                        ImageUrl = t.ImageUrl,
                        DurationMs = t.DurationMs,
                        TrackNumber = t.TrackNumber,
                        DiscNumber = t.DiscNumber,
                        StreamUrl = !string.IsNullOrEmpty(t.StreamUrl) ? $"{gatewayUrl}{t.StreamUrl}" : $"{gatewayUrl}/playback/{t.Id}",
                        AlbumId = albumId,
                        ArtistId = artistId
                    };
                    _cache.Set(id, entry);
                    _registrar.RegisterTrack(id, entry);

                    if (existingIds.Add(id))
                    {
                        additions.Add(new SearchHint
                        {
                            Id = id,
                            Name = t.Name,
                            Type = BaseItemKind.Audio,
                            Artists = !string.IsNullOrEmpty(t.ArtistName) ? new[] { t.ArtistName } : Array.Empty<string>(),
                            Album = t.AlbumName,
                            AlbumArtist = t.ArtistName,
                            PrimaryImageTag = "resono-" + id.ToString("N"),
                            RunTimeTicks = (long)t.DurationMs * 10000,
                            IndexNumber = t.TrackNumber,
                        });
                    }
                }
            }

            if (additions.Count == 0) return sr;

            var combined = (sr.SearchHints ?? Array.Empty<SearchHint>()).Concat(additions).ToArray();
            return new SearchHintResult(combined, combined.Length);
        }

        private bool ShouldAugmentRecentlyPlayed(ResultExecutingContext ctx)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg is null || !cfg.EnableSearchInjection) return false;

            var http = ctx.HttpContext;
            var sortBy = (http.Request.Query.TryGetValue("sortBy", out var sb) ? sb.ToString()
                : http.Request.Query.TryGetValue("SortBy", out var sb2) ? sb2.ToString() : string.Empty);

            if (sortBy.IndexOf("DatePlayed", StringComparison.OrdinalIgnoreCase) < 0) return false;

            var types = ExtractIncludeItemTypes(http);
            if (types.Count == 0 || !types.Any(t => MusicTypes.Contains(t))) return false;

            return ctx.Result is ObjectResult { Value: QueryResult<BaseItemDto> };
        }

        private void TryAugmentRecentlyPlayed(ResultExecutingContext ctx)
        {
            if (ctx.Result is not ObjectResult or || or.Value is not QueryResult<BaseItemDto> qr) return;

            Guid userId = Guid.Empty;
            try
            {
                var auth = _authContext.GetAuthorizationInfo(ctx.HttpContext.Request).GetAwaiter().GetResult();
                if (auth?.UserId != null && auth.UserId != Guid.Empty) userId = auth.UserId;
            }
            catch { }

            var limit = 30;
            if (ctx.HttpContext.Request.Query.TryGetValue("Limit", out var lv) && int.TryParse(lv.ToString(), out var lp))
                limit = Math.Min(Math.Max(lp, 1), 50);

            var recent = userId != Guid.Empty
                ? _recentlyPlayed.GetRecent(userId, limit)
                : _recentlyPlayed.GetAllRecent(limit);

            if (recent.Count == 0) return;

            var existingIds = qr.Items.Select(i => i.Id).ToHashSet();
            var additions = new List<BaseItemDto>();

            var types = ExtractIncludeItemTypes(ctx.HttpContext);
            bool wantAlbum = types.Count == 0 || types.Contains("MusicAlbum");
            bool wantAudio = types.Count == 0 || types.Contains("Audio");

            foreach (var p in recent)
            {
                if (wantAlbum && p.AlbumId.HasValue && existingIds.Add(p.AlbumId.Value))
                {
                    if (_cache.TryGet(p.AlbumId.Value, out var alEntry) && alEntry is { Kind: "album" })
                    {
                        additions.Add(BuildAlbumDto(p.AlbumId.Value, alEntry));
                    }
                }
                if (wantAudio && existingIds.Add(p.TrackId))
                {
                    if (_cache.TryGet(p.TrackId, out var trEntry) && trEntry is { Kind: "track" })
                    {
                        _registrar.RegisterTrack(p.TrackId, trEntry);
                        additions.Add(BuildTrackDto(p.TrackId, trEntry));
                    }
                }
            }

            if (additions.Count > 0)
            {
                qr.Items = additions.Concat(qr.Items).ToArray();
                qr.TotalRecordCount = qr.Items.Length;
            }
        }

        private bool ShouldAugmentPlaylists(ResultExecutingContext ctx)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg is null || !cfg.EnableVirtualPlaylists) return false;
            if (ctx.Result is not ObjectResult { Value: QueryResult<BaseItemDto> qr }) return false;

            var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
            if (path.IndexOf("/UserViews", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("/Views", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            var types = ExtractIncludeItemTypes(ctx.HttpContext);
            if (types.Contains("Playlist") || path.IndexOf("/Playlists", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Discrete queries /Users/{userId}/Items?parentId={playlistsFolderGuid} without types
            // If the response contains playlist items, or has 0 items and parentId looks like playlists folder
            if (qr.Items.Any(i => i.Type == BaseItemKind.Playlist))
                return true;

            return false;
        }

        private async Task TryAugmentPlaylistsAsync(ResultExecutingContext ctx, CancellationToken ct)
        {
            if (ctx.Result is not ObjectResult or || or.Value is not QueryResult<BaseItemDto> qr) return;

            var charts = await FetchChartsAsync(ct).ConfigureAwait(false);
            if (charts.Count > 0)
            {
                var existingIds = qr.Items.Select(i => i.Id).ToHashSet();
                var toAdd = charts.Where(c => existingIds.Add(c.Id)).ToArray();
                if (toAdd.Length > 0)
                {
                    qr.Items = qr.Items.Concat(toAdd).ToArray();
                    qr.TotalRecordCount = qr.Items.Length;
                }
            }
        }

        public async Task<List<BaseItemDto>> FetchChartsAsync(CancellationToken ct)
        {
            var list = new List<BaseItemDto>();
            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var country = !string.IsNullOrWhiteSpace(cfg.ChartCountryCode) ? cfg.ChartCountryCode : "PE";
                var url = $"{gatewayUrl}/jellyfin/charts?country={Uri.EscapeDataString(country)}";

                var client = _httpClientFactory.CreateClient();
                var data = await client.GetFromJsonAsync<GatewayChartsResponse>(url, ct).ConfigureAwait(false);
                if (data?.Charts != null)
                {
                    foreach (var c in data.Charts)
                    {
                        var chartId = ResonoItemCache.StubGuid("chart", c.Id ?? c.Name ?? Guid.NewGuid().ToString());
                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "playlist",
                            Name = c.Name,
                            SpotifyId = c.Id,
                            ImageUrl = c.ImageUrl
                        };
                        _cache.Set(chartId, entry);
                        list.Add(BuildPlaylistDto(chartId, entry));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch virtual chart playlists: {Message}", ex.Message);
            }
            return list;
        }

        public static BaseItemDto BuildPlaylistDto(Guid id, ResonoItemCache.Entry e)
        {
            var imageTag = "resono-" + id.ToString("N");
            return new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.Name ?? "Chart Playlist",
                Type = BaseItemKind.Playlist,
                MediaType = MediaType.Audio,
                Tags = new[] { "ResonoVirtual", "Discovery" },
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, imageTag } },
                ImageBlurHashes = new Dictionary<ImageType, Dictionary<string, string>> { { ImageType.Primary, new() } },
                PrimaryImageAspectRatio = 1.0,
                ChildCount = 50,
                IsFolder = true,
                CanDelete = false,
                CanDownload = false,
                LocationType = LocationType.Virtual
            };
        }

        public static BaseItemDto BuildArtistDto(Guid id, ResonoItemCache.Entry e)
        {
            var imageTag = "resono-" + id.ToString("N");
            return new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.Name ?? "(unknown artist)",
                Type = BaseItemKind.MusicArtist,
                MediaType = MediaType.Unknown,
                Tags = new[] { "ResonoVirtual" },
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, imageTag } },
                ImageBlurHashes = new Dictionary<ImageType, Dictionary<string, string>> { { ImageType.Primary, new() } },
                PrimaryImageAspectRatio = 1.0,
                IsFolder = true,
                ChildCount = 50,
                SongCount = 50,
                AlbumCount = 20,
                LocationType = LocationType.Virtual,
            };
        }

        public static BaseItemDto BuildAlbumDto(Guid id, ResonoItemCache.Entry e)
        {
            var imageTag = "resono-" + id.ToString("N");
            NameGuidPair[]? artistPair = null;
            if (!string.IsNullOrEmpty(e.ArtistName))
            {
                artistPair = new[] { new NameGuidPair { Name = e.ArtistName, Id = e.ArtistId ?? ResonoItemCache.StubGuid("dz-artist", e.ArtistName) } };
            }

            return new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.Name ?? "(unknown album)",
                Type = BaseItemKind.MusicAlbum,
                MediaType = MediaType.Unknown,
                Tags = new[] { "ResonoVirtual" },
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, imageTag } },
                ImageBlurHashes = new Dictionary<ImageType, Dictionary<string, string>> { { ImageType.Primary, new() } },
                PrimaryImageAspectRatio = 1.0,
                Artists = !string.IsNullOrEmpty(e.ArtistName) ? new[] { e.ArtistName } : null,
                AlbumArtist = e.ArtistName,
                AlbumArtists = artistPair ?? Array.Empty<NameGuidPair>(),
                ArtistItems = artistPair ?? Array.Empty<NameGuidPair>(),
                IsFolder = true,
                LocationType = LocationType.Virtual,
            };
        }

        public static MediaSourceInfo BuildMediaSource(Guid id, ResonoItemCache.Entry e)
        {
            var idStr = id.ToString("N");
            return new MediaSourceInfo
            {
                Id = idStr,
                Path = $"/data/music/resono/{idStr}.mp3",
                Protocol = MediaProtocol.File,
                IsRemote = false,
                Type = MediaSourceType.Default,
                Name = e.Name ?? "Track",
                Container = "mp3",
                SupportsTranscoding = true,
                SupportsDirectStream = true,
                SupportsDirectPlay = true,
                RequiresOpening = false,
                RequiresClosing = false,
                RunTimeTicks = e.DurationMs.HasValue ? (long)e.DurationMs.Value * 10000 : null,
                Bitrate = 320000,
                DefaultAudioStreamIndex = 0,
                MediaStreams = new List<MediaBrowser.Model.Entities.MediaStream>
                {
                    new MediaBrowser.Model.Entities.MediaStream
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
        }

        public static BaseItemDto BuildTrackDto(Guid id, ResonoItemCache.Entry e)
        {
            var imageTag = "resono-" + id.ToString("N");
            var albumImageTag = e.AlbumId.HasValue ? "resono-" + e.AlbumId.Value.ToString("N") : imageTag;

            NameGuidPair[]? artistPair = null;
            if (!string.IsNullOrEmpty(e.ArtistName))
            {
                artistPair = new[] { new NameGuidPair { Name = e.ArtistName, Id = e.ArtistId ?? ResonoItemCache.StubGuid("dz-artist", e.ArtistName) } };
            }

            var mediaSource = BuildMediaSource(id, e);

            return new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.Name ?? "(unknown track)",
                Type = BaseItemKind.Audio,
                MediaType = MediaType.Audio,
                Tags = new[] { "ResonoVirtual" },
                AlbumPrimaryImageTag = albumImageTag,
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, imageTag } },
                ImageBlurHashes = new Dictionary<ImageType, Dictionary<string, string>> { { ImageType.Primary, new() } },
                PrimaryImageAspectRatio = 1.0,
                AlbumId = e.AlbumId,
                ParentId = e.AlbumId,
                Artists = !string.IsNullOrEmpty(e.ArtistName) ? new[] { e.ArtistName } : null,
                ArtistItems = artistPair ?? Array.Empty<NameGuidPair>(),
                AlbumArtists = artistPair ?? Array.Empty<NameGuidPair>(),
                Album = e.AlbumName,
                AlbumArtist = e.ArtistName,
                IndexNumber = e.TrackNumber,
                ParentIndexNumber = e.DiscNumber,
                RunTimeTicks = e.DurationMs.HasValue ? (long)e.DurationMs.Value * 10000 : null,
                IsFolder = false,
                CanDownload = false,
                LocationType = LocationType.FileSystem,
                MediaSources = new[] { mediaSource },
                MediaSourceCount = 1,
                Container = "mp3",
            };
        }
    }

    public class GatewaySearchResponse
    {
        [JsonPropertyName("artists")]
        public List<GatewayArtist>? Artists { get; set; }

        [JsonPropertyName("albums")]
        public List<GatewayAlbum>? Albums { get; set; }

        [JsonPropertyName("tracks")]
        public List<GatewayTrack>? Tracks { get; set; }
    }

    public class GatewayArtist
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }
    }

    public class GatewayAlbum
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }

        [JsonPropertyName("artistId")]
        public string? ArtistId { get; set; }

        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }
    }

    public class GatewayTrack
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("canonicalId")]
        public string? CanonicalId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }

        [JsonPropertyName("artistId")]
        public string? ArtistId { get; set; }

        [JsonPropertyName("albumName")]
        public string? AlbumName { get; set; }

        [JsonPropertyName("albumId")]
        public string? AlbumId { get; set; }

        [JsonPropertyName("durationMs")]
        public int? DurationMs { get; set; }

        [JsonPropertyName("trackNumber")]
        public int? TrackNumber { get; set; }

        [JsonPropertyName("discNumber")]
        public int? DiscNumber { get; set; }

        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("streamUrl")]
        public string? StreamUrl { get; set; }
    }
}
