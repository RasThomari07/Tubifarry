using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Reflection;
using System.Text.RegularExpressions;
using Tubifarry.Core.Model;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;
using Tubifarry.Download.Clients.YouTube;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models.Info;
using YouTubeMusicAPI.Models.Search;
using YouTubeMusicAPI.Pagination;

namespace Tubifarry.Indexers.YouTube
{
    /// <summary>
    /// Parses YouTube Music API responses and converts them to releases.
    /// </summary>
    internal class YouTubeParser : IParseIndexerResponse
    {
        private const int DEFAULT_BITRATE = 128;
        private const double TitleOverlapThreshold = 0.4;
        private readonly Logger _logger;
        private readonly YouTubeIndexer _youTubeIndexer;
        private YouTubeMusicClient? _youTubeClient;
        private SessionTokens? _sessionToken;

        private static readonly Lazy<Func<JObject, Page<SearchResult>?>?> _getPageDelegate = new(() =>
        {
            try
            {
                Assembly ytMusicAssembly = typeof(YouTubeMusicClient).Assembly;
                Type? searchParserType = ytMusicAssembly.GetType("YouTubeMusicAPI.Internal.Parsers.SearchParser");
                MethodInfo? getPageMethod = searchParserType?.GetMethod("GetPage", BindingFlags.Public | BindingFlags.Static);
                if (getPageMethod == null) return null;
                return (Func<JObject, Page<SearchResult>?>)Delegate.CreateDelegate(
                    typeof(Func<JObject, Page<SearchResult>?>), getPageMethod);
            }
            catch { return null; }
        });

        public YouTubeParser(YouTubeIndexer indexer)
        {
            _youTubeIndexer = indexer;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<ReleaseInfo> releases = [];

            try
            {
                if (string.IsNullOrEmpty(indexerResponse.Content))
                {
                    _logger.Warn("Received empty response content");
                    return releases;
                }
                JObject jsonResponse = JObject.Parse(indexerResponse.Content);
                Page<SearchResult> searchPage = TryParseWithDelegate(jsonResponse) ?? new Page<SearchResult>([], null);

                _logger.Trace($"Parsed {searchPage.Items.Count} search results from YouTube Music API response");
                ProcessSearchResults(searchPage.Items, releases);
                _logger.Debug($"Successfully converted {releases.Count} results to releases");
                return [.. releases.DistinctBy(x => x.DownloadUrl).OrderByDescending(o => o.PublishDate)];
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"An error occurred while parsing YouTube Music API response. Response length: {indexerResponse.Content?.Length ?? 0}");
                return releases;
            }
        }

        /// <summary>
        /// Try to parse using cached delegate to access internal SearchParser - 50x faster than reflection
        /// </summary>
        private Page<SearchResult>? TryParseWithDelegate(JObject jsonResponse)
        {
            try
            {
                Func<JObject, Page<SearchResult>?>? delegateMethod = _getPageDelegate.Value;
                if (delegateMethod == null)
                {
                    _logger.Error("SearchParser.GetPage delegate not available");
                    return null;
                }
                Page<SearchResult>? result = delegateMethod(jsonResponse);
                if (result != null)
                {
                    _logger.Trace("Successfully parsed response using cached delegate");
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to parse response using delegate, falling back to manual parsing");
            }
            return null;
        }

        private void ProcessSearchResults(IReadOnlyList<SearchResult> searchResults, List<ReleaseInfo> releases)
        {
            foreach (SearchResult searchResult in searchResults)
            {
                if (searchResult is not AlbumSearchResult album)
                    continue;

                // A title-similar album whose volume/disc/part number differs from the search is a
                // different record ("Astrology" for a search of "Astrology 01"). Without this guard the
                // title override rewrites it onto the requested EP, it gets grabbed, then fails to import.
                if (IsVolumeMismatch(album.Name, _youTubeIndexer.SearchAlbumQuery))
                {
                    _logger.Debug($"Skipped album '{album.Name}': volume/number mismatch with search '{_youTubeIndexer.SearchAlbumQuery}'");
                    continue;
                }

                try
                {
                    AlbumData albumData = ExtractAlbumInfo(album);
                    albumData.ParseReleaseDate();
                    EnrichAlbumWithYouTubeDataAsync(albumData).GetAwaiter().GetResult();
                    if (albumData.Bitrate > 0)
                    {
                        releases.Add(albumData.ToReleaseInfo());
                        _logger.Trace($"Added album: '{albumData.AlbumName}' by '{albumData.ArtistName}' (Bitrate: {albumData.Bitrate}kbps)");
                    }
                    else
                    {
                        _logger.Trace($"Skipped album (no bitrate): '{albumData.AlbumName}' by '{albumData.ArtistName}'");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to process album: '{album?.Name}' by '{album?.Artists?.FirstOrDefault()?.Name}'");
                }
            }
        }

        private async Task EnrichAlbumWithYouTubeDataAsync(AlbumData albumData)
        {
            try
            {
                UpdateClient();

                string browseId = await _youTubeClient!.GetAlbumBrowseIdAsync(albumData.AlbumId);
                AlbumInfo albumInfo = await _youTubeClient.GetAlbumInfoAsync(browseId);

                if (albumInfo?.Songs == null || albumInfo.Songs.Length == 0)
                {
                    _logger.Trace($"No songs found for album: '{albumData.AlbumName}'");
                    albumData.Bitrate = DEFAULT_BITRATE;
                    return;
                }

                albumData.Duration = (long)albumInfo.Duration.TotalSeconds;
                albumData.TotalTracks = albumInfo.SongCount;
                albumData.ExplicitContent = albumInfo.Songs.Any(x => x.IsExplicit);

                // The per-track streaming probe (GetStreamingDataAsync) can no longer succeed:
                // YouTubeMusicAPI's signature/poToken path is broken upstream, so it threw for every
                // track (bot check / Jint "Cannot read property 'call'") and the bitrate fell back to
                // DEFAULT anyway — at the cost of one doomed YouTube call per search result, which
                // only helps get the IP soft-blocked during big library scans. Downloads run through
                // yt-dlp (which reads the real bitrate), so the indexer just defaults here.
                albumData.Bitrate = DEFAULT_BITRATE;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, $"Failed to enrich album data for: '{albumData.AlbumName}'");
                albumData.Bitrate = DEFAULT_BITRATE;
            }
        }

        private void UpdateClient()
        {
            if (_sessionToken?.IsValid == true)
                return;
            _sessionToken = TrustedSessionHelper.GetTrustedSessionTokensAsync(_youTubeIndexer.Settings.TrustedSessionGeneratorUrl).GetAwaiter().GetResult();
            _youTubeClient = TrustedSessionHelper.CreateAuthenticatedClientAsync(_youTubeIndexer.Settings.TrustedSessionGeneratorUrl, _youTubeIndexer.Settings.CookiePath).GetAwaiter().GetResult();
        }

        private AlbumData ExtractAlbumInfo(AlbumSearchResult album)
        {
            string albumName = album.Name;
            string artistName = album.Artists.FirstOrDefault()?.Name ?? "Unknown Artist";
            string? searchAlbum = _youTubeIndexer.SearchAlbumQuery;
            string? searchArtist = _youTubeIndexer.SearchArtistQuery;

            if (!string.IsNullOrEmpty(searchAlbum))
            {
                double overlap = TokenOverlap(albumName, searchAlbum);
                if (overlap >= TitleOverlapThreshold)
                {
                    _logger.Debug($"Title override: '{albumName}' → '{searchAlbum}' (overlap {overlap:P0})");
                    albumName = searchAlbum;
                    if (!string.IsNullOrEmpty(searchArtist) && !string.Equals(artistName, searchArtist, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Debug($"Artist override: '{artistName}' → '{searchArtist}'");
                        artistName = searchArtist;
                    }
                }
            }

            return new AlbumData("Youtube", nameof(YoutubeDownloadProtocol))
            {
                AlbumId = album.Id,
                InfoUrl = $"https://music.youtube.com/playlist?list={album.Id}",
                AlbumName = albumName,
                ArtistName = artistName,
                ReleaseDate = album.ReleaseYear > 0 ? album.ReleaseYear.ToString() : "0000-01-01",
                ReleaseDatePrecision = "year",
                CustomString = album.Thumbnails.FirstOrDefault()?.Url ?? string.Empty,
                CoverResolution = album.Thumbnails.FirstOrDefault() is { } thumbnail
                        ? $"{thumbnail.Width}x{thumbnail.Height}"
                        : "Unknown Resolution"
            };
        }

        /// <summary>
        /// True when <paramref name="ytAlbumName"/> is title-similar enough to be rewritten onto the
        /// searched album, yet carries a different volume/disc/part number — i.e. a different record.
        /// Only near-identical titles (the ones the override targets) are guarded, so ordinary title
        /// overrides ("Wacko" → "MC Wack", no numbers involved) are unaffected.
        /// </summary>
        private bool IsVolumeMismatch(string ytAlbumName, string? searchAlbum)
        {
            if (string.IsNullOrEmpty(searchAlbum) || string.IsNullOrEmpty(ytAlbumName))
                return false;
            if (TokenOverlap(ytAlbumName, searchAlbum) < TitleOverlapThreshold)
                return false;
            return !NumberTokens(ytAlbumName).SetEquals(NumberTokens(searchAlbum));
        }

        // Volume/disc/part markers = standalone 1-3 digit numbers (4-digit years like "2011" are
        // ignored) plus uppercase Roman numerals, so "33" and "XXXIII" compare as the same number.
        private static readonly Regex _arabicNumber = new(@"\b\d{1,3}\b", RegexOptions.Compiled);
        private static readonly Regex _romanNumber = new(
            @"\b(?=[MDCLXVI]{2,}\b)M{0,3}(?:CM|CD|D?C{0,3})(?:XC|XL|L?X{0,3})(?:IX|IV|V?I{0,3})\b",
            RegexOptions.Compiled);

        private static HashSet<int> NumberTokens(string s)
        {
            HashSet<int> nums = [.. _arabicNumber.Matches(s).Select(m => int.Parse(m.Value))];
            foreach (Match m in _romanNumber.Matches(s))
                nums.Add(RomanToInt(m.Value));
            return nums;
        }

        private static int RomanToInt(string roman)
        {
            int total = 0, prev = 0;
            foreach (char ch in roman.Reverse())
            {
                int val = ch switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, 'M' => 1000, _ => 0 };
                total += val < prev ? -val : val;
                prev = val;
            }
            return total;
        }

        private static double TokenOverlap(string a, string b)
        {
            HashSet<string> ta = Tokenize(a);
            HashSet<string> tb = Tokenize(b);
            int common = ta.Count(t => tb.Contains(t));
            int union = ta.Count + tb.Count - common;
            return union == 0 ? 0 : (double)common / union;
        }

        private static HashSet<string> Tokenize(string s) =>
            [.. s.ToLowerInvariant()
                .Split([' ', '-', ':', ',', '.', '/', '\\', '(', ')', '[', ']', '\'', '"'], StringSplitOptions.RemoveEmptyEntries)];
    }
}