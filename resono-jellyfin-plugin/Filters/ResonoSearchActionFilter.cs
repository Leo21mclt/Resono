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
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dlna;
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
        private static readonly ConcurrentDictionary<string, (DateTime Expires, List<GatewayTrack> Tracks)> _chartTracksListCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (DateTime Expires, List<BaseItemDto> Charts)> _chartsCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (DateTime Expires, List<BaseItemDto> Albums)> _recommendedAlbumsCache = new(StringComparer.OrdinalIgnoreCase);
        private static string? _cachedEffectiveGatewayUrl;

        public static string GetEffectiveGatewayUrl(string? configured)
        {
            var env = Environment.GetEnvironmentVariable("RESONO_GATEWAY_URL");
            if (!string.IsNullOrWhiteSpace(env)) return env.TrimEnd('/');

            if (!string.IsNullOrWhiteSpace(configured) && !configured.Contains("localhost") && !configured.Contains("127.0.0.1"))
            {
                return configured.TrimEnd('/');
            }

            return "http://resono-gateway:8080";
        }

        private static readonly HashSet<string> MusicTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Audio", "MusicAlbum", "MusicArtist", "AudioBook", "Album", "Artist", "AlbumArtist", "Playlist", "Song"
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ResonoItemCache _cache;
        private readonly ResonoLibraryRegistrar _registrar;
        private readonly ResonoRecentlyPlayedTracker _recentlyPlayed;
        private readonly ResonoFavoritesTracker _favoritesTracker;
        private readonly IAuthorizationContext _authContext;
        private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
        private readonly MediaBrowser.Controller.Session.ISessionManager _sessionManager;
        private readonly ILogger<ResonoSearchActionFilter> _logger;

        public ResonoSearchActionFilter(
            IHttpClientFactory httpClientFactory,
            ResonoItemCache cache,
            ResonoLibraryRegistrar registrar,
            ResonoRecentlyPlayedTracker recentlyPlayed,
            ResonoFavoritesTracker favoritesTracker,
            IAuthorizationContext authContext,
            MediaBrowser.Controller.Library.ILibraryManager libraryManager,
            MediaBrowser.Controller.Session.ISessionManager sessionManager,
            ILogger<ResonoSearchActionFilter> logger)
        {
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _registrar = registrar;
            _recentlyPlayed = recentlyPlayed;
            _favoritesTracker = favoritesTracker;
            _authContext = authContext;
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
            _logger = logger;
        }

        public async Task OnResultExecutionAsync(ResultExecutingContext ctx, ResultExecutionDelegate next)
        {
            try
            {
                if (ShouldInject(ctx, out var searchTerm))
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.HttpContext.RequestAborted);
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    await TryAugmentSearchAsync(ctx, searchTerm!, cts.Token).ConfigureAwait(false);
                }
                else if (ShouldAugmentRecentlyPlayed(ctx))
                {
                    TryAugmentRecentlyPlayed(ctx);
                }
                else if (ShouldAugmentFavorites(ctx))
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.HttpContext.RequestAborted);
                    cts.CancelAfter(TimeSpan.FromSeconds(6));
                    await TryAugmentFavoritesAsync(ctx, cts.Token).ConfigureAwait(false);
                }
                else if (ShouldAugmentPlaylists(ctx))
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.HttpContext.RequestAborted);
                    cts.CancelAfter(TimeSpan.FromSeconds(6));
                    await TryAugmentPlaylistsAsync(ctx, cts.Token).ConfigureAwait(false);
                }
                else if (ShouldAugmentEmptyLibrary(ctx))
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.HttpContext.RequestAborted);
                    cts.CancelAfter(TimeSpan.FromSeconds(6));
                    await TryAugmentEmptyLibraryAsync(ctx, cts.Token).ConfigureAwait(false);
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
                var lazy = _inFlightSearches.GetOrAdd(cacheKey, key => new Lazy<Task<GatewaySearchResponse?>>(() => FetchGatewaySearchAsync(gatewayUrl, term, limit, provider, fallback, cfg.EnableSearchCache, cfg.DeezerArl)));
                try
                {
                    searchData = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // User aborted this specific HTTP request (e.g. typed next letter), but background fetch continues
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

        private async Task<GatewaySearchResponse?> FetchGatewaySearchAsync(string gatewayUrl, string term, int limit, string provider, string fallback, bool enableCache, string? deezerArl)
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
                using var fetchCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var data = await client.GetFromJsonAsync<GatewaySearchResponse>(url, fetchCts.Token).ConfigureAwait(false);
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
            var requestedTypes = ExtractIncludeItemTypes(httpCtx);
            bool hasTypeFilter = requestedTypes.Count > 0;
            bool wantArtists = !hasTypeFilter || requestedTypes.Contains("MusicArtist") || requestedTypes.Contains("Artist");
            bool wantAlbums = !hasTypeFilter || requestedTypes.Contains("MusicAlbum") || requestedTypes.Contains("Album");
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

            Guid searchUserId = Guid.Empty;
            try
            {
                var auth = _authContext.GetAuthorizationInfo(httpCtx.Request).GetAwaiter().GetResult();
                if (auth?.UserId != null && auth.UserId != Guid.Empty) searchUserId = auth.UserId;
            }
            catch { }

            var gatewayItems = new List<BaseItemDto>();
            var gatewayIds = new HashSet<Guid>();

            Action addArtists = () =>
            {
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

                        if (gatewayIds.Add(id))
                        {
                            gatewayItems.Add(BuildArtistDto(id, entry, _favoritesTracker.IsFavorite(searchUserId, id)));
                        }
                    }
                }
            };

            Action addAlbums = () =>
            {
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

                        int? albYear = al.ProductionYear;
                        DateTime? albDate = null;
                        if (!string.IsNullOrEmpty(al.ReleaseDate) && DateTime.TryParse(al.ReleaseDate, out var parsedDate))
                        {
                            albDate = parsedDate;
                            albYear ??= parsedDate.Year;
                        }

                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "album",
                            Name = al.Name,
                            ArtistName = al.ArtistName,
                            SpotifyId = al.Id,
                            ImageUrl = al.ImageUrl,
                            ArtistId = artistId,
                            ProductionYear = albYear,
                            PremiereDate = albDate,
                            Genres = al.Genres
                        };
                        _cache.Set(id, entry);
                        if (!string.IsNullOrEmpty(al.Name))
                        {
                            _registrar.RegisterAlbum(id, al.Name, al.ArtistName, entry);
                        }

                        if (gatewayIds.Add(id))
                        {
                            gatewayItems.Add(BuildAlbumDto(id, entry, _favoritesTracker.IsFavorite(searchUserId, id)));
                        }
                    }
                }
            };

            Action addTracks = () =>
            {
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
                            _registrar.RegisterAlbum(albumId.Value, t.AlbumName, t.ArtistName);
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
                            _registrar.RegisterArtist(artistId.Value, t.ArtistName);
                        }

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

                        if (gatewayIds.Add(id))
                        {
                            gatewayItems.Add(BuildTrackDto(id, entry, _favoritesTracker.IsFavorite(searchUserId, id)));
                        }
                    }
                }
            };

            // Order categories based on Deezer bestResult intent:
            if (string.Equals(data.BestResultType, "album", StringComparison.OrdinalIgnoreCase))
            {
                addAlbums();
                addArtists();
                addTracks();
            }
            else if (string.Equals(data.BestResultType, "track", StringComparison.OrdinalIgnoreCase))
            {
                addTracks();
                addAlbums();
                addArtists();
            }
            else
            {
                addArtists();
                addAlbums();
                addTracks();
            }

            if (gatewayItems.Count > 0)
            {
                var localItems = qr.Items.Where(i => !gatewayIds.Contains(i.Id)).ToList();
                var combined = gatewayItems.Concat(localItems).ToArray();
                qr.Items = combined;
                qr.TotalRecordCount = combined.Length;
            }
        }

        private SearchHintResult AugmentHints(SearchHintResult sr, GatewaySearchResponse data, string gatewayUrl)
        {
            var gatewayHints = new List<SearchHint>();
            var gatewayIds = new HashSet<Guid>();

            Action addArtistHints = () =>
            {
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
                        if (!string.IsNullOrEmpty(a.Name))
                        {
                            _registrar.RegisterArtist(id, a.Name);
                        }

                        if (gatewayIds.Add(id))
                        {
                            gatewayHints.Add(new SearchHint
                            {
                                Id = id,
                                ItemId = id,
                                Name = a.Name,
                                Type = BaseItemKind.MusicArtist,
                                MediaType = MediaType.Unknown,
                                Artists = !string.IsNullOrEmpty(a.Name) ? new[] { a.Name } : Array.Empty<string>(),
                                AlbumArtist = a.Name,
                                MatchedTerm = a.Name,
                                IsFolder = true,
                                PrimaryImageAspectRatio = 1.0,
                                PrimaryImageTag = id.ToString("N")
                            });
                        }
                    }
                }
            };

            Action addAlbumHints = () =>
            {
                if (data.Albums != null)
                {
                    foreach (var al in data.Albums)
                    {
                        var id = ResonoItemCache.StubGuid("dz-album", al.Id ?? al.Name ?? Guid.NewGuid().ToString());
                        var artistId = !string.IsNullOrEmpty(al.ArtistId)
                            ? ResonoItemCache.StubGuid("dz-artist", al.ArtistId)
                            : (!string.IsNullOrEmpty(al.ArtistName) ? ResonoItemCache.StubGuid("dz-artist", al.ArtistName) : (Guid?)null);

                        int? albYear = al.ProductionYear;
                        DateTime? albDate = null;
                        if (!string.IsNullOrEmpty(al.ReleaseDate) && DateTime.TryParse(al.ReleaseDate, out var parsedDate))
                        {
                            albDate = parsedDate;
                            albYear ??= parsedDate.Year;
                        }

                        var entry = new ResonoItemCache.Entry
                        {
                            Id = id,
                            Kind = "album",
                            Name = al.Name,
                            ArtistName = al.ArtistName,
                            SpotifyId = al.Id,
                            ImageUrl = al.ImageUrl,
                            ArtistId = artistId,
                            ProductionYear = albYear,
                            PremiereDate = albDate,
                            Genres = (al.Genres != null && al.Genres.Count > 0) ? al.Genres : null
                        };
                        _cache.Set(id, entry);
                        if (!string.IsNullOrEmpty(al.Name))
                        {
                            _registrar.RegisterAlbum(id, al.Name, al.ArtistName, entry);
                        }

                        var albArtist = !string.IsNullOrWhiteSpace(al.ArtistName) ? al.ArtistName : "Various Artists";
                        var effectiveYear = albYear ?? (albDate.HasValue ? albDate.Value.Year : 2024);

                        if (gatewayIds.Add(id))
                        {
                            gatewayHints.Add(new SearchHint
                            {
                                Id = id,
                                ItemId = id,
                                Name = al.Name,
                                Type = BaseItemKind.MusicAlbum,
                                MediaType = MediaType.Unknown,
                                Album = al.Name,
                                AlbumArtist = albArtist,
                                Artists = new[] { albArtist },
                                ProductionYear = effectiveYear,
                                MatchedTerm = al.Name,
                                IsFolder = true,
                                PrimaryImageAspectRatio = 1.0,
                                PrimaryImageTag = id.ToString("N")
                            });
                        }
                    }
                }
            };

            Action addTrackHints = () =>
            {
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
                                    Id = albumId.Value,
                                    Kind = "album",
                                    Name = t.AlbumName,
                                    ArtistName = t.ArtistName,
                                    SpotifyId = t.AlbumId,
                                    ImageUrl = t.ImageUrl,
                                    ArtistId = artistId
                                });
                            }
                        }

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

                        var entry = new ResonoItemCache.Entry
                        {
                            Id = id,
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

                        if (gatewayIds.Add(id))
                        {
                            gatewayHints.Add(new SearchHint
                            {
                                Id = id,
                                ItemId = id,
                                Name = t.Name,
                                Type = BaseItemKind.Audio,
                                MediaType = MediaType.Audio,
                                Artists = !string.IsNullOrEmpty(t.ArtistName) ? new[] { t.ArtistName } : Array.Empty<string>(),
                                Album = t.AlbumName,
                                AlbumId = albumId,
                                AlbumArtist = t.ArtistName,
                                PrimaryImageTag = id.ToString("N"),
                                PrimaryImageAspectRatio = 1.0,
                                RunTimeTicks = t.DurationMs.HasValue ? (long)t.DurationMs.Value * 10000 : null,
                                IndexNumber = t.TrackNumber,
                            });
                        }
                    }
                }
            };

            if (string.Equals(data.BestResultType, "album", StringComparison.OrdinalIgnoreCase))
            {
                addAlbumHints();
                addArtistHints();
                addTrackHints();
            }
            else if (string.Equals(data.BestResultType, "track", StringComparison.OrdinalIgnoreCase))
            {
                addTrackHints();
                addAlbumHints();
                addArtistHints();
            }
            else
            {
                addArtistHints();
                addAlbumHints();
                addTrackHints();
            }

            if (gatewayHints.Count == 0) return sr;

            var localHints = (sr.SearchHints ?? Array.Empty<SearchHint>()).Where(h => !gatewayIds.Contains(h.Id));
            var combined = gatewayHints.Concat(localHints).ToArray();
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
            bool wantAlbum = types.Count == 0 || types.Contains("MusicAlbum") || types.Contains("Album");
            bool wantAudio = types.Count == 0 || types.Contains("Audio") || types.Contains("Song");

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
                qr.TotalRecordCount = qr.Items.Count;
            }
        }

        private bool ShouldAugmentFavorites(ResultExecutingContext ctx)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg is null) return false;
            if (ctx.Result is not ObjectResult { Value: QueryResult<BaseItemDto> }) return false;

            var q = ctx.HttpContext.Request.Query;
            var filters = (q.TryGetValue("filters", out var f1) ? f1.ToString()
                : q.TryGetValue("Filters", out var f2) ? f2.ToString() : string.Empty);
            var isFav = (q.TryGetValue("isFavorite", out var if1) ? if1.ToString()
                : q.TryGetValue("IsFavorite", out var if2) ? if2.ToString() : string.Empty);

            return filters.IndexOf("IsFavorite", StringComparison.OrdinalIgnoreCase) >= 0
                || filters.IndexOf("Favorite", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(isFav, "true", StringComparison.OrdinalIgnoreCase);
        }

        private async Task TryAugmentFavoritesAsync(ResultExecutingContext ctx, CancellationToken ct)
        {
            if (ctx.Result is not ObjectResult or || or.Value is not QueryResult<BaseItemDto> qr) return;

            Guid userId = await ResonoItemDetailActionFilter.ResolveUserIdAsync(
                ctx.HttpContext.Request,
                ctx.RouteData.Values,
                _authContext,
                _sessionManager).ConfigureAwait(false);

            if (userId == Guid.Empty) return;

            var types = ExtractIncludeItemTypes(ctx.HttpContext);
            bool wantAudio = types.Count == 0 || types.Contains("Audio") || types.Contains("Song");
            bool wantAlbum = types.Count == 0 || types.Contains("MusicAlbum") || types.Contains("Album");
            bool wantArtist = types.Count == 0 || types.Contains("MusicArtist") || types.Contains("Artist");

            var favList = _favoritesTracker.GetFavorites(userId);
            if (favList.Count == 0) return;

            var existingIds = qr.Items.Select(i => i.Id).ToHashSet();
            var additions = new List<BaseItemDto>();

            foreach (var fav in favList)
            {
                if (!existingIds.Add(fav.ItemId)) continue;

                if (!_cache.TryGet(fav.ItemId, out var entry) || entry == null)
                {
                    var cfg = Plugin.Instance?.Configuration;
                    var gatewayUrl = GetEffectiveGatewayUrl(cfg?.GatewayUrl);
                    try
                    {
                        var client = _httpClientFactory.CreateClient();
                        var trackInfo = await client.GetFromJsonAsync<GatewayTrack>($"{gatewayUrl}/jellyfin/track/{fav.ItemId:N}", ct).ConfigureAwait(false);
                        if (trackInfo != null)
                        {
                            var albId = !string.IsNullOrEmpty(trackInfo.AlbumId) ? ResonoItemCache.StubGuid("dz-album", trackInfo.AlbumId) : (Guid?)null;
                            var artId = !string.IsNullOrEmpty(trackInfo.ArtistId) ? ResonoItemCache.StubGuid("dz-artist", trackInfo.ArtistId) : (Guid?)null;
                            entry = new ResonoItemCache.Entry
                            {
                                Kind = "track",
                                Name = trackInfo.Name,
                                ArtistName = trackInfo.ArtistName,
                                AlbumName = trackInfo.AlbumName,
                                SpotifyId = trackInfo.Id,
                                CanonicalId = trackInfo.CanonicalId,
                                ImageUrl = trackInfo.ImageUrl,
                                DurationMs = trackInfo.DurationMs,
                                TrackNumber = trackInfo.TrackNumber,
                                DiscNumber = trackInfo.DiscNumber,
                                StreamUrl = !string.IsNullOrEmpty(trackInfo.StreamUrl) ? $"{gatewayUrl}{trackInfo.StreamUrl}" : $"{gatewayUrl}/playback/{trackInfo.Id}",
                                AlbumId = albId,
                                ArtistId = artId
                            };
                            _cache.Set(fav.ItemId, entry);
                        }
                    }
                    catch { }
                }

                if (entry != null)
                {
                    if (entry.Kind == "track" && wantAudio)
                    {
                        _registrar.RegisterTrack(fav.ItemId, entry);
                        additions.Add(BuildTrackDto(fav.ItemId, entry, isFavorite: true));
                    }
                    else if (entry.Kind == "album" && wantAlbum)
                    {
                        additions.Add(BuildAlbumDto(fav.ItemId, entry, isFavorite: true));
                    }
                    else if (entry.Kind == "artist" && wantArtist)
                    {
                        additions.Add(BuildArtistDto(fav.ItemId, entry, isFavorite: true));
                    }
                }
            }

            // Ensure all items returned in favorites query have IsFavorite = true
            foreach (var item in qr.Items)
            {
                if (item.UserData != null) item.UserData.IsFavorite = true;
            }

            if (additions.Count > 0)
            {
                qr.Items = additions.Concat(qr.Items).ToArray();
                qr.TotalRecordCount = qr.Items.Count;
            }
        }

        private static bool TryExtractGuidListFromQuery(HttpRequest req, string key, out List<Guid> ids)
        {
            ids = new List<Guid>();
            foreach (var k in new[] { key, char.ToUpperInvariant(key[0]) + key[1..] })
            {
                if (req.Query.TryGetValue(k, out var qs))
                {
                    foreach (var part in qs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (Guid.TryParse(part, out var g)) ids.Add(g);
                }
            }
            return ids.Count > 0;
        }

        private bool ShouldAugmentPlaylists(ResultExecutingContext ctx)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg is null || !cfg.EnableVirtualPlaylists) return false;
            if (ctx.Result is not ObjectResult { Value: QueryResult<BaseItemDto> qr }) return false;

            var q = ctx.HttpContext.Request.Query;
            var sortBy = (q.TryGetValue("sortBy", out var sb) ? sb.ToString()
                : q.TryGetValue("SortBy", out var sb2) ? sb2.ToString() : string.Empty);
            var filters = (q.TryGetValue("filters", out var f1) ? f1.ToString()
                : q.TryGetValue("Filters", out var f2) ? f2.ToString() : string.Empty);
            var isFav = (q.TryGetValue("isFavorite", out var if1) ? if1.ToString()
                : q.TryGetValue("IsFavorite", out var if2) ? if2.ToString() : string.Empty);

            // Never inject charts into Recent Playlists or Favorite Playlists!
            if (sortBy.IndexOf("DatePlayed", StringComparison.OrdinalIgnoreCase) >= 0
                || filters.IndexOf("IsFavorite", StringComparison.OrdinalIgnoreCase) >= 0
                || filters.IndexOf("RecentlyPlayed", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(isFav, "true", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
            if (path.IndexOf("/UserViews", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("/Views", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            // Do not treat /Playlists/{id}/Items as a playlist container query
            if (path.IndexOf("/Playlists/", StringComparison.OrdinalIgnoreCase) >= 0 && path.TrimEnd('/').EndsWith("/Items", StringComparison.OrdinalIgnoreCase))
                return false;

            var types = ExtractIncludeItemTypes(ctx.HttpContext);
            if (types.Contains("Playlist") || path.IndexOf("/Playlists", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Discrete / Manet queries /Users/{userId}/Items?parentId={playlistsFolderGuid} without types
            // If the response contains playlist items, or has 0 items and parentId looks like playlists folder
            if (qr.Items.Any(i => i.Type == BaseItemKind.Playlist))
                return true;

            if (TryExtractGuidListFromQuery(ctx.HttpContext.Request, "parentId", out var pids))
            {
                foreach (var pid in pids)
                {
                    try
                    {
                        var folder = _libraryManager.GetItemById(pid);
                        if (folder != null)
                        {
                            var typeName = folder.GetClientTypeName();
                            if (string.Equals(typeName, "PlaylistFolder", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(folder.Name, "Playlists", StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                    }
                    catch { }
                }
            }

            return false;
        }

        private async Task TryAugmentPlaylistsAsync(ResultExecutingContext ctx, CancellationToken ct)
        {
            if (ctx.Result is not ObjectResult or || or.Value is not QueryResult<BaseItemDto> qr) return;

            var existingIds = qr.Items.Select(i => i.Id).ToHashSet();

            // 1. Ensure any existing playlist returned has LocationType = FileSystem and MediaType = Audio
            foreach (var item in qr.Items.Where(i => i.Type == BaseItemKind.Playlist))
            {
                item.LocationType = LocationType.FileSystem;
                item.MediaType = MediaType.Audio;
            }

            // 2. If parentId was specified (e.g. Discrete scoped to Music library),
            // user-created playlists were excluded by Jellyfin because playlists live in ManualPlaylistsFolder.
            // Fetch the user's actual playlists from Jellyfin and include them!
            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Playlist },
                    Recursive = true
                };

                var result = _libraryManager.GetItemsResult(query);
                if (result?.Items != null && result.Items.Count > 0)
                {
                    var userPlaylistDtos = new List<BaseItemDto>();
                    foreach (var pl in result.Items)
                    {
                        if (existingIds.Add(pl.Id))
                        {
                            var dto = new BaseItemDto
                            {
                                Id = pl.Id,
                                Name = pl.Name,
                                ServerId = Plugin.ServerSystemId,
                                Type = BaseItemKind.Playlist,
                                MediaType = MediaType.Audio,
                                LocationType = LocationType.FileSystem,
                                IsFolder = true,
                                CanDelete = true,
                                CanDownload = false,
                                RunTimeTicks = pl.RunTimeTicks,
                                DateCreated = pl.DateCreated,
                                UserData = new UserItemDataDto
                                {
                                    PlaybackPositionTicks = 0,
                                    PlayCount = 0,
                                    IsFavorite = false,
                                    Played = false,
                                    Key = pl.Id.ToString("N")
                                }
                            };

                            userPlaylistDtos.Add(dto);
                        }
                    }

                    if (userPlaylistDtos.Count > 0)
                    {
                        qr.Items = qr.Items.Concat(userPlaylistDtos).ToArray();
                        qr.TotalRecordCount = qr.Items.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to augment user playlists: {Message}", ex.Message);
            }

            // 2b. Add virtual "Canciones favoritas" playlist if user has favorites
            try
            {
                Guid plUserId = await ResonoItemDetailActionFilter.ResolveUserIdAsync(
                    ctx.HttpContext.Request,
                    ctx.RouteData.Values,
                    _authContext,
                    _sessionManager).ConfigureAwait(false);

                if (plUserId != Guid.Empty && _favoritesTracker.GetFavorites(plUserId).Count > 0)
                {
                    var favPlGuid = ResonoItemCache.StubGuid("dz-user-favorites", plUserId.ToString());
                    if (existingIds.Add(favPlGuid))
                    {
                        var favPlDto = new BaseItemDto
                        {
                            Id = favPlGuid,
                            Name = "Canciones favoritas",
                            ServerId = Plugin.ServerSystemId,
                            Type = BaseItemKind.Playlist,
                            MediaType = MediaType.Audio,
                            LocationType = LocationType.FileSystem,
                            IsFolder = true,
                            CanDelete = false,
                            CanDownload = false,
                            RunTimeTicks = 0,
                            UserData = new UserItemDataDto
                            {
                                PlaybackPositionTicks = 0,
                                PlayCount = 0,
                                IsFavorite = true,
                                Played = false,
                                Key = favPlGuid.ToString("N")
                            }
                        };
                        qr.Items = new[] { favPlDto }.Concat(qr.Items).ToArray();
                        qr.TotalRecordCount = qr.Items.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to augment favorites playlist: {Message}", ex.Message);
            }

            // 3. Augment with virtual discovery chart playlists if enabled
            var charts = await FetchChartsAsync(ct).ConfigureAwait(false);
            if (charts.Count > 0)
            {
                var toAdd = charts.Where(c => existingIds.Add(c.Id)).ToArray();
                if (toAdd.Length > 0)
                {
                    qr.Items = qr.Items.Concat(toAdd).ToArray();
                    qr.TotalRecordCount = qr.Items.Count;
                }
            }
        }

        private bool ShouldAugmentEmptyLibrary(ResultExecutingContext ctx)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg is null || !cfg.EnableVirtualPlaylists) return false;
            if (ctx.Result is not ObjectResult { Value: QueryResult<BaseItemDto> qr }) return false;
            if (qr.TotalRecordCount > 0) return false;

            var q = ctx.HttpContext.Request.Query;
            var sortBy = (q.TryGetValue("sortBy", out var sb) ? sb.ToString()
                : q.TryGetValue("SortBy", out var sb2) ? sb2.ToString() : string.Empty);
            var filters = (q.TryGetValue("filters", out var f1) ? f1.ToString()
                : q.TryGetValue("Filters", out var f2) ? f2.ToString() : string.Empty);
            var isFav = (q.TryGetValue("isFavorite", out var if1) ? if1.ToString()
                : q.TryGetValue("IsFavorite", out var if2) ? if2.ToString() : string.Empty);

            // Strictly DO NOT inject into Favorite queries or Recently Played queries!
            if (sortBy.IndexOf("DatePlayed", StringComparison.OrdinalIgnoreCase) >= 0
                || filters.IndexOf("IsFavorite", StringComparison.OrdinalIgnoreCase) >= 0
                || filters.IndexOf("RecentlyPlayed", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(isFav, "true", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
            if (path.IndexOf("/UserViews", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("/Views", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            // Do not fire on searches (those are handled by ShouldInject)
            var term = ExtractSearchTerm(ctx.HttpContext);
            if (!string.IsNullOrWhiteSpace(term)) return false;

            var types = ExtractIncludeItemTypes(ctx.HttpContext);
            if (types.Contains("MusicArtist") || types.Contains("Artist") || types.Contains("MusicAlbum") || types.Contains("Album") || path.StartsWith("/Artists", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private async Task TryAugmentEmptyLibraryAsync(ResultExecutingContext ctx, CancellationToken ct)
        {
            if (ctx.Result is not ObjectResult or || or.Value is not QueryResult<BaseItemDto> qr) return;

            var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
            var types = ExtractIncludeItemTypes(ctx.HttpContext);
            bool wantArtists = types.Contains("MusicArtist") || types.Contains("Artist") || path.StartsWith("/Artists", StringComparison.OrdinalIgnoreCase);
            bool wantAlbums = types.Contains("MusicAlbum") || types.Contains("Album");

            Guid userId = Guid.Empty;
            try
            {
                var auth = _authContext.GetAuthorizationInfo(ctx.HttpContext.Request).GetAwaiter().GetResult();
                if (auth?.UserId != null && auth.UserId != Guid.Empty) userId = auth.UserId;
            }
            catch { }

            if (wantAlbums)
            {
                var recAlbums = await FetchRecommendedAlbumsAsync(userId, ct).ConfigureAwait(false);
                if (recAlbums.Count > 0)
                {
                    qr.Items = recAlbums.ToArray();
                    qr.TotalRecordCount = recAlbums.Count;
                    return;
                }
            }

            var chartTracks = await FetchChartTrackListAsync("global", ct).ConfigureAwait(false);
            if (chartTracks.Count == 0) return;

            if (wantArtists)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var artistDtos = new List<BaseItemDto>();
                foreach (var t in chartTracks)
                {
                    if (!string.IsNullOrEmpty(t.ArtistName) && seen.Add(t.ArtistName))
                    {
                        var artistGuid = ResonoItemCache.StubGuid("dz-artist", t.ArtistName);
                        if (!_cache.TryGet(artistGuid, out var artEntry) || artEntry == null)
                        {
                            artEntry = new ResonoItemCache.Entry
                            {
                                Kind = "artist",
                                Name = t.ArtistName,
                                SpotifyId = t.ArtistId,
                                ImageUrl = t.ImageUrl,
                                Id = artistGuid
                            };
                            _cache.Set(artistGuid, artEntry);
                        }
                        artistDtos.Add(BuildArtistDto(artistGuid, artEntry));
                    }
                }
                if (artistDtos.Count > 0)
                {
                    qr.Items = artistDtos.ToArray();
                    qr.TotalRecordCount = qr.Items.Count;
                }
            }
        }

        public async Task<List<BaseItemDto>> FetchRecommendedAlbumsAsync(Guid userId, CancellationToken ct)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg is null) return new List<BaseItemDto>();

            var seedArtists = new List<string>();
            if (userId != Guid.Empty)
            {
                var userRecent = _recentlyPlayed.GetRecent(userId, 20);
                var seenArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in userRecent)
                {
                    if (_cache.TryGet(p.TrackId, out var tr) && !string.IsNullOrWhiteSpace(tr.ArtistName))
                    {
                        if (seenArtists.Add(tr.ArtistName))
                        {
                            seedArtists.Add(tr.ArtistName);
                            if (seedArtists.Count >= 3) break;
                        }
                    }
                }
            }

            var cacheKey = seedArtists.Count > 0 ? string.Join("|", seedArtists) : "charts:albums";
            if (_recommendedAlbumsCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expires)
            {
                return cached.Albums;
            }

            var list = new List<BaseItemDto>();
            try
            {
                var gatewayUrl = GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var client = _httpClientFactory.CreateClient();
                string url;
                if (seedArtists.Count > 0)
                {
                    var artistQuery = string.Join(",", seedArtists.Select(Uri.EscapeDataString));
                    url = $"{gatewayUrl}/jellyfin/recommendations?artists={artistQuery}&limit=30";
                }
                else
                {
                    url = $"{gatewayUrl}/jellyfin/charts/albums?limit=30";
                }

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var data = await client.GetFromJsonAsync<GatewayChartAlbumsResponse>(url, timeoutCts.Token).ConfigureAwait(false);
                if (data?.Albums != null)
                {
                    foreach (var a in data.Albums)
                    {
                        var albId = ResonoItemCache.StubGuid("dz-album", a.Id ?? a.Name ?? Guid.NewGuid().ToString());
                        var artId = !string.IsNullOrEmpty(a.ArtistName) ? ResonoItemCache.StubGuid("dz-artist", a.ArtistName) : (Guid?)null;
                        int? albYear = a.ProductionYear;
                        DateTime? albDate = null;
                        if (!string.IsNullOrEmpty(a.ReleaseDate) && DateTime.TryParse(a.ReleaseDate, out var parsedDate))
                        {
                            albDate = parsedDate;
                            albYear ??= parsedDate.Year;
                        }

                        var entry = new ResonoItemCache.Entry
                        {
                            Kind = "album",
                            Name = a.Name,
                            ArtistName = a.ArtistName,
                            ArtistId = artId,
                            ImageUrl = a.ImageUrl,
                            SpotifyId = a.Id,
                            ProductionYear = albYear,
                            PremiereDate = albDate,
                            Genres = a.Genres
                        };
                        _cache.Set(albId, entry);
                        _registrar.RegisterAlbum(albId, a.Name, a.ArtistName, entry);
                        list.Add(BuildAlbumDto(albId, entry));
                    }
                    if (list.Count > 0)
                    {
                        _recommendedAlbumsCache[cacheKey] = (DateTime.UtcNow.AddMinutes(15), list);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch recommended albums: {Message}", ex.Message);
            }
            return list;
        }

        public async Task<List<GatewayTrack>> FetchChartTrackListAsync(string chartId, CancellationToken ct)
        {
            if (_chartTracksListCache.TryGetValue(chartId, out var cached) && DateTime.UtcNow < cached.Expires)
            {
                return cached.Tracks;
            }

            var list = new List<GatewayTrack>();
            try
            {
                var cfg = Plugin.Instance!.Configuration;
                var gatewayUrl = GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var url = $"{gatewayUrl}/jellyfin/charts/{Uri.EscapeDataString(chartId)}/tracks";

                var client = _httpClientFactory.CreateClient();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var res = await client.GetFromJsonAsync<GatewayArtistTopResponse>(url, timeoutCts.Token).ConfigureAwait(false);
                if (res?.Tracks != null)
                {
                    list = res.Tracks;
                    _chartTracksListCache[chartId] = (DateTime.UtcNow.AddMinutes(15), list);
                    return list;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch chart tracks: {Message}", ex.Message);
            }
            return list;
        }

        public async Task<List<BaseItemDto>> FetchChartsAsync(CancellationToken ct)
        {
            var cfg = Plugin.Instance!.Configuration;
            var country = !string.IsNullOrWhiteSpace(cfg.ChartCountryCode) ? cfg.ChartCountryCode : "PE";
            if (_chartsCache.TryGetValue(country, out var cached) && DateTime.UtcNow < cached.Expires)
            {
                return cached.Charts;
            }

            var list = new List<BaseItemDto>();
            try
            {
                var gatewayUrl = GetEffectiveGatewayUrl(cfg.GatewayUrl);
                var url = $"{gatewayUrl}/jellyfin/charts?country={Uri.EscapeDataString(country)}";

                var client = _httpClientFactory.CreateClient();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var data = await client.GetFromJsonAsync<GatewayChartsResponse>(url, timeoutCts.Token).ConfigureAwait(false);
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
                    if (list.Count > 0)
                    {
                        _chartsCache[country] = (DateTime.UtcNow.AddMinutes(15), list);
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
            var imageTag = id.ToString("N");
            return new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.Name ?? "Chart Playlist",
                Type = BaseItemKind.Playlist,
                MediaType = MediaType.Audio,
                Tags = new[] { "ResonoVirtual", "Discovery" },
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, imageTag } },
                ImageBlurHashes = new Dictionary<ImageType, Dictionary<string, string>>
                {
                    { ImageType.Primary, new Dictionary<string, string> { { imageTag, "eODJO}t7%MWBt79FWBIUayRj00ayxut7t7_3ofofWBWB%MayIUWBay" } } }
                },
                PrimaryImageAspectRatio = 1.0,
                ChildCount = 50,
                IsFolder = true,
                CanDelete = false,
                CanDownload = false,
                LocationType = LocationType.FileSystem,
                UserData = new UserItemDataDto
                {
                    PlaybackPositionTicks = 0,
                    PlayCount = 0,
                    IsFavorite = false,
                    Played = false,
                    Key = id.ToString("N")
                }
            };
        }

        public static BaseItemDto BuildArtistDto(Guid id, ResonoItemCache.Entry e, bool isFavorite = false)
        {
            var imageTag = id.ToString("N");
            var name = !string.IsNullOrWhiteSpace(e.Name) ? e.Name : "Artist";
            var pair = new[] { new NameGuidPair { Name = name, Id = id } };
            var genres = (e.Genres != null && e.Genres.Count > 0) ? e.Genres.ToArray() : new[] { "Artist" };
            var genrePairs = genres.Select(g => new NameGuidPair { Name = g, Id = ResonoItemCache.StubGuid("dz-genre", g) }).ToArray();
            return new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = name,
                Type = BaseItemKind.MusicArtist,
                MediaType = MediaType.Unknown,
                Tags = new[] { "ResonoVirtual" },
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, imageTag } },
                ImageBlurHashes = new Dictionary<ImageType, Dictionary<string, string>>
                {
                    { ImageType.Primary, new Dictionary<string, string> { { imageTag, "eODJO}t7%MWBt79FWBIUayRj00ayxut7t7_3ofofWBWB%MayIUWBay" } } }
                },
                PrimaryImageAspectRatio = 1.0,
                IsFolder = true,
                Artists = new[] { name },
                ArtistItems = pair,
                AlbumArtists = pair,
                AlbumArtist = name,
                Genres = genres,
                GenreItems = genrePairs,
                Overview = "Artist",
                ChildCount = 50,
                SongCount = 50,
                AlbumCount = 20,
                LocationType = LocationType.FileSystem,
                UserData = new UserItemDataDto
                {
                    PlaybackPositionTicks = 0,
                    PlayCount = 0,
                    IsFavorite = isFavorite,
                    Played = false,
                    Key = id.ToString("N")
                }
            };
        }

        public static BaseItemDto BuildAlbumDto(Guid id, ResonoItemCache.Entry e, bool isFavorite = false)
        {
            var imageTag = id.ToString("N");

            // Auto-heal missing ArtistName from tracks in cache:
            var artistName = e.ArtistName;
            if (string.IsNullOrWhiteSpace(artistName))
            {
                if (ResonoItemCache.Instance != null && ResonoItemCache.Instance.TryGetTrackByAlbumId(id, out var tr) && !string.IsNullOrWhiteSpace(tr?.ArtistName))
                {
                    artistName = tr.ArtistName;
                    e.ArtistName = artistName;
                    e.ArtistId ??= tr.ArtistId;
                }
                else
                {
                    artistName = "Various Artists";
                }
            }

            var artistPair = new[] { new NameGuidPair { Name = artistName, Id = e.ArtistId ?? ResonoItemCache.StubGuid("dz-artist", artistName) } };

            var genresList = (e.Genres != null && e.Genres.Count > 0) ? e.Genres : null;
            if (genresList == null && ResonoItemDetailActionFilter.TryGetCachedAlbumGenres(id, out var cachedGenres))
            {
                genresList = cachedGenres;
                e.Genres = cachedGenres;
            }
            genresList ??= new List<string> { "Album" };

            var genrePairs = genresList.Select(g => new NameGuidPair { Name = g, Id = ResonoItemCache.StubGuid("dz-genre", g) }).ToArray();
            var prodYear = e.ProductionYear ?? (e.PremiereDate.HasValue ? e.PremiereDate.Value.Year : 2024);

            return new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.Name ?? "(unknown album)",
                Type = BaseItemKind.MusicAlbum,
                MediaType = MediaType.Unknown,
                Tags = new[] { "ResonoVirtual" },
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, imageTag } },
                ImageBlurHashes = new Dictionary<ImageType, Dictionary<string, string>>
                {
                    { ImageType.Primary, new Dictionary<string, string> { { imageTag, "eODJO}t7%MWBt79FWBIUayRj00ayxut7t7_3ofofWBWB%MayIUWBay" } } }
                },
                PrimaryImageAspectRatio = 1.0,
                Artists = new[] { artistName },
                AlbumArtist = artistName,
                AlbumArtists = artistPair,
                ArtistItems = artistPair,
                ProductionYear = prodYear,
                PremiereDate = e.PremiereDate ?? new DateTime(prodYear, 1, 1),
                Genres = genresList.ToArray(),
                GenreItems = genrePairs,
                IsFolder = true,
                LocationType = LocationType.FileSystem,
                UserData = new UserItemDataDto
                {
                    PlaybackPositionTicks = 0,
                    PlayCount = 0,
                    IsFavorite = isFavorite,
                    Played = false,
                    Key = id.ToString("N")
                }
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
                IsInfiniteStream = false,
                RequiresOpening = false,
                RequiresClosing = false,
                RequiresLooping = false,
                SupportsProbing = true,
                RunTimeTicks = e.DurationMs.HasValue ? (long)e.DurationMs.Value * 10000 : null,
                Bitrate = 320000,
                DefaultAudioStreamIndex = 0,
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = 0,
                        IsDefault = true,
                        Codec = "mp3",
                        BitRate = 320000,
                        Channels = 2,
                        SampleRate = 44100,
                        ChannelLayout = "stereo"
                    },
                    new MediaStream
                    {
                        Codec = "lrc",
                        Type = MediaStreamType.Lyric,
                        Index = 1,
                        IsDefault = true,
                        IsExternal = true,
                        DeliveryMethod = SubtitleDeliveryMethod.External,
                        DeliveryUrl = $"/Audio/{idStr}/Lyrics",
                        Path = $"/data/music/resono/{idStr}.lrc"
                    }
                },
                RequiredHttpHeaders = new Dictionary<string, string>()
            };
        }

        public static BaseItemDto BuildTrackDto(Guid id, ResonoItemCache.Entry e, bool isFavorite = false)
        {
            var trackImageTag = id.ToString("N");
            var albumImageTag = e.AlbumId.HasValue ? e.AlbumId.Value.ToString("N") : trackImageTag;

            NameGuidPair[]? artistPair = null;
            if (!string.IsNullOrEmpty(e.ArtistName))
            {
                artistPair = new[] { new NameGuidPair { Name = e.ArtistName, Id = e.ArtistId ?? ResonoItemCache.StubGuid("dz-artist", e.ArtistName) } };
            }

            var genres = (e.Genres != null && e.Genres.Count > 0) ? e.Genres.ToArray() : new[] { "Music" };
            var genrePairs = genres.Select(g => new NameGuidPair { Name = g, Id = ResonoItemCache.StubGuid("dz-genre", g) }).ToArray();

            var mediaSource = BuildMediaSource(id, e);

            return new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.Name ?? "(unknown track)",
                Type = BaseItemKind.Audio,
                MediaType = MediaType.Audio,
                HasLyrics = true,
                Tags = new[] { "ResonoVirtual" },
                AlbumPrimaryImageTag = albumImageTag,
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, trackImageTag } },
                ImageBlurHashes = new Dictionary<ImageType, Dictionary<string, string>>
                {
                    { ImageType.Primary, new Dictionary<string, string> { { trackImageTag, "eODJO}t7%MWBt79FWBIUayRj00ayxut7t7_3ofofWBWB%MayIUWBay" } } }
                },
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
                ProductionYear = e.ProductionYear,
                PremiereDate = e.PremiereDate,
                Genres = genres,
                GenreItems = genrePairs,
                IsFolder = false,
                CanDownload = false,
                LocationType = LocationType.FileSystem,
                MediaSources = new[] { mediaSource },
                MediaSourceCount = 1,
                Container = "mp3",
                UserData = new UserItemDataDto
                {
                    PlaybackPositionTicks = 0,
                    PlayCount = 0,
                    IsFavorite = isFavorite,
                    Played = false,
                    Key = id.ToString("N")
                }
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

        [JsonPropertyName("bestResultType")]
        public string? BestResultType { get; set; }
    }

    public class GatewayArtist
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

        [JsonPropertyName("releaseDate")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("productionYear")]
        public int? ProductionYear { get; set; }

        [JsonPropertyName("genres")]
        public List<string>? Genres { get; set; }

        [JsonPropertyName("providerIds")]
        public Dictionary<string, string>? ProviderIds { get; set; }
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

        [JsonPropertyName("providerIds")]
        public Dictionary<string, string>? ProviderIds { get; set; }
    }
}
