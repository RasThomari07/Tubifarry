using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using System.Net;
using Tubifarry.Core.Records;
using Tubifarry.Core.Replacements;
using Tubifarry.Core.Utilities;
using Tubifarry.Download.Clients.YouTube;
using YouTubeMusicAPI.Internal;
using YouTubeMusicAPI.Models.Search;

namespace Tubifarry.Indexers.YouTube
{
    internal class YouTubeRequestGenerator : IIndexerRequestGenerator<LazyIndexerPageableRequest>
    {
        private const int MaxPages = 3;

        private readonly Logger _logger;

        private readonly YouTubeIndexer _youTubeIndexer;
        private SessionTokens? _sessionToken;

        public YouTubeRequestGenerator(YouTubeIndexer indexer)
        {
            _youTubeIndexer = indexer;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetRecentRequests()
        {
            // YouTube doesn't support RSS/recent releases functionality in a traditional sense
            _youTubeIndexer.SearchAlbumQuery = null;
            _youTubeIndexer.SearchArtistQuery = null;
            _youTubeIndexer.SearchAlbumTrackCount = null;
            return new LazyIndexerPageableRequestChain();
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for album: '{searchCriteria.AlbumQuery}' by artist: '{searchCriteria.ArtistQuery}'");

            // Propagate search context to the parser so it can override YouTube Music titles
            // with MusicBrainz titles when the two are similar (avoids "Unable to parse" rejections).
            _youTubeIndexer.SearchAlbumQuery = searchCriteria.AlbumQuery;
            _youTubeIndexer.SearchArtistQuery = searchCriteria.ArtistQuery;

            // Expected track count for this exact search, so the parser can reject playlists and
            // discographies before they are grabbed. Assigned unconditionally (null included):
            // the indexer is a singleton, so a previous search would otherwise leak its value.
            _youTubeIndexer.SearchAlbumTrackCount = GetExpectedTrackCount(searchCriteria.Albums);
            _logger.Debug($"Track count filter: expecting {_youTubeIndexer.SearchAlbumTrackCount?.ToString() ?? "unknown (filter off)"} track(s) for '{searchCriteria.AlbumQuery}'");

            // AcceptableSizeSpecification rejects any release whose album has Duration=0 in the DB.
            // MusicBrainz lacks duration data for many older/niche releases; refresh never fixes this.
            // Patching the LazyLoaded cache in memory here is safe: the same Album object references
            // are later used by AcceptableSizeSpecification via searchCriteria.Albums.
            PatchZeroDurationAlbums(searchCriteria.Albums);

            // Fallback tiers (album only, then artist only) fire when a tier yields fewer releases
            // than this threshold. 5 was fine while almost every candidate was kept; with the track
            // count filter a good search often leaves 1-3 releases, so the artist-only tier - the one
            // that surfaces playlists and discographies - would fire on nearly every album, at the
            // cost of two extra YouTube calls per extra candidate. That traffic profile is what
            // soft-blocked the IP on 9-12 Jul. At 1 the fallbacks only run when the primary search
            // returns nothing. Put it back to 5 if searches start coming up empty too often.
            LazyIndexerPageableRequestChain chain = new(1);

            // Primary search: album + artist
            if (!string.IsNullOrEmpty(searchCriteria.AlbumQuery) && !string.IsNullOrEmpty(searchCriteria.ArtistQuery))
            {
                string primaryQuery = $"{searchCriteria.AlbumQuery} {searchCriteria.ArtistQuery}";
                chain.AddFactory(() => GetRequests(primaryQuery, SearchCategory.Albums));
            }

            // Fallback search: album only
            if (!string.IsNullOrEmpty(searchCriteria.AlbumQuery))
            {
                chain.AddTierFactory(() => GetRequests(searchCriteria.AlbumQuery, SearchCategory.Albums));
            }

            // Last resort: artist only (still search for albums)
            if (!string.IsNullOrEmpty(searchCriteria.ArtistQuery))
            {
                chain.AddTierFactory(() => GetRequests(searchCriteria.ArtistQuery, SearchCategory.Albums));
            }

            return chain;
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Debug($"Generating search requests for artist: '{searchCriteria.ArtistQuery}'");

            _youTubeIndexer.SearchAlbumQuery = null;
            _youTubeIndexer.SearchArtistQuery = searchCriteria.ArtistQuery;

            // An artist search targets every monitored album at once (ReleaseSearchService fills
            // searchCriteria.Albums with the whole list), so there is no single expected count.
            _youTubeIndexer.SearchAlbumTrackCount = null;

            LazyIndexerPageableRequestChain chain = new(5);
            if (!string.IsNullOrEmpty(searchCriteria.ArtistQuery))
                chain.AddFactory(() => GetRequests(searchCriteria.ArtistQuery, SearchCategory.Albums));

            return chain;
        }

        /// <summary>
        /// Track count Lidarr expects for this search, read from the release the import will be
        /// matched against. Returns null when it cannot be determined - no album, several albums,
        /// no target release, or TrackCount left at 0 - in which case the filter stays off.
        /// </summary>
        private int? GetExpectedTrackCount(IList<Album>? albums)
        {
            // AlbumSearchCriteria always carries exactly one album (ReleaseSearchService.AlbumSearch
            // builds it with a single-item list); anything else means we cannot tell which album the
            // results should match, so we do not filter.
            if (albums == null || albums.Count != 1)
                return null;

            try
            {
                AlbumRelease? release = GetTargetRelease(albums[0]);
                return release == null || release.TrackCount <= 0 ? null : release.TrackCount;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Track count filter: could not read the expected track count for '{albums[0].Title}'");
                return null;
            }
        }

        /// <summary>
        /// The release the album will actually be imported against: the monitored one, or the first
        /// available when the album accepts any release. Shared with AcceptableSize patching so both
        /// features agree on which release is the reference.
        /// </summary>
        private static AlbumRelease? GetTargetRelease(Album album)
        {
            List<AlbumRelease>? releases = album.AlbumReleases?.Value;
            if (releases == null)
                return null;
            return releases.FirstOrDefault(r => r.Monitored)
                ?? (album.AnyReleaseOk ? releases.FirstOrDefault() : null);
        }

        private void PatchZeroDurationAlbums(IList<Album>? albums)
        {
            if (albums == null) return;
            const int estimatePerTrackMs = 210_000; // 3:30 / track
            foreach (Album album in albums)
            {
                try
                {
                    AlbumRelease? release = GetTargetRelease(album);
                    if (release == null || release.TrackCount == 0 || release.Duration != 0) continue;
                    release.Duration = release.TrackCount * estimatePerTrackMs;
                    _logger.Debug($"AcceptableSize patch: '{album.Title}' — estimated {release.Duration / 60000}min ({release.TrackCount} tracks × 3:30)");
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, $"AcceptableSize patch failed for album '{album.Title}'");
                }
            }
        }

        private void UpdateTokens()
        {
            if (_sessionToken?.IsValid == true)
                return;
            _sessionToken = TrustedSessionHelper.GetTrustedSessionTokensAsync(_youTubeIndexer.Settings.TrustedSessionGeneratorUrl).GetAwaiter().GetResult();
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchQuery, SearchCategory category)
        {
            UpdateTokens();

            for (int page = 0; page < MaxPages; page++)
            {
                Dictionary<string, object> payload = Payload.WebRemix(
                    geographicalLocation: "US",
                    visitorData: _sessionToken!.VisitorData,
                    poToken: _sessionToken!.PoToken,
                    signatureTimestamp: null,
                    items:
                    [
                        ("query", searchQuery),
                        ("params", ToParams(category)),
                        ("continuation", null)
                    ]
                );

                string jsonPayload = JsonConvert.SerializeObject(payload, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

                HttpRequest request = new($"https://music.youtube.com/youtubei/v1/search?key={PluginKeys.YouTubeSecret}", HttpAccept.Json) { Method = HttpMethod.Post };
                if (!string.IsNullOrEmpty(_youTubeIndexer.Settings.CookiePath))
                {
                    try
                    {
                        foreach (Cookie cookie in CookieManager.ParseCookieFile(_youTubeIndexer.Settings.CookiePath))
                            request.Cookies[cookie.Name] = cookie.Value;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, $"Failed to load cookies from {_youTubeIndexer.Settings.CookiePath}");
                    }
                }
                request.SetContent(jsonPayload);
                _logger.Trace($"Created YouTube Music API request for query: '{searchQuery}', category: {category}");

                yield return new IndexerRequest(request);
            }
        }

        public static string? ToParams(SearchCategory? value) =>
           value switch
           {
               SearchCategory.Songs => "EgWKAQIIAWoQEAMQChAJEAQQBRAREBAQFQ%3D%3D",
               SearchCategory.Videos => "EgWKAQIQAWoQEAMQBBAJEAoQBRAREBAQFQ%3D%3D",
               SearchCategory.Albums => "EgWKAQIYAWoQEAMQChAJEAQQBRAREBAQFQ%3D%3D",
               SearchCategory.CommunityPlaylists => "EgeKAQQoAEABahAQAxAKEAkQBBAFEBEQEBAV",
               SearchCategory.Artists => "EgWKAQIgAWoQEAMQChAJEAQQBRAREBAQFQ%3D%3D",
               SearchCategory.Podcasts => "EgWKAQJQAWoQEAMQChAJEAQQBRAREBAQFQ%3D%3D",
               SearchCategory.Episodes => "EgWKAQJIAWoQEAMQChAJEAQQBRAREBAQFQ%3D%3D",
               SearchCategory.Profiles => "EgWKAQJYAWoQEAMQChAJEAQQBRAREBAQFQ%3D%3D",
               _ => null
           };
    }
}