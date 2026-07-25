using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.YouTube
{
    public class YouTubeIndexerSettingsValidator : AbstractValidator<YouTubeIndexerSettings>
    {
        public YouTubeIndexerSettingsValidator()
        {
            // Validate CookiePath (if provided)
            RuleFor(x => x.CookiePath)
                .Must(path => string.IsNullOrEmpty(path) || File.Exists(path))
                .WithMessage("Cookie file does not exist. Please provide a valid path to the cookies file.")
                .Must(path => string.IsNullOrEmpty(path) || CookieManager.ParseCookieFile(path).Length != 0)
                .WithMessage("Cookie file is invalid or contains no valid cookies.");

            // Validate TrustedSessionGeneratorUrl (optional)
            RuleFor(x => x.TrustedSessionGeneratorUrl)
                .Must(url => string.IsNullOrEmpty(url) || Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .WithMessage("Trusted Session Generator URL must be a valid URL if provided.");

            // 0 = disabled on the YouTube indexer, 1-100 = percent. Spotify narrows this to 0-50.
            RuleFor(x => x.TrackCountTolerance)
                .GreaterThanOrEqualTo(0)
                .LessThanOrEqualTo(100)
                .WithMessage("Track count tolerance must be between 0 and 100");
        }
    }

    public class YouTubeIndexerSettings : IIndexerSettings
    {
        private static readonly YouTubeIndexerSettingsValidator Validator = new();

        [FieldDefinition(0, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; } = null;

        [FieldDefinition(1, Label = "Cookie Path", Type = FieldType.FilePath, Hidden = HiddenType.Visible, Placeholder = "/path/to/cookies.txt", HelpText = "Specify the path to the YouTube cookies file. This is optional but helps with accessing restricted content.", Advanced = true)]
        public string CookiePath { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "Generator URL", Type = FieldType.Textbox, Placeholder = "http://localhost:8080", HelpText = "URL to the YouTube Trusted Session Generator service. When provided, PoToken and Visitor Data will be fetched automatically.", Advanced = true)]
        public string TrustedSessionGeneratorUrl { get; set; } = string.Empty;

        /// <summary>
        /// Maximum accepted deviation, in percent, between the track count of the release Lidarr
        /// monitors and the track count the indexer reports for a candidate album. Candidates
        /// outside the tolerance are dropped during the search, before anything is grabbed.
        ///
        /// Added 25/07/2026 after a three-week grab loop: the YouTube indexer kept returning
        /// playlists and whole discographies for a specific album (Taipan "Inedits" 36 tracks for
        /// 12 expected, Supreme NTM "Best Of" 44 for 28, IAM "Integrale" 3 for 135). They
        /// downloaded fine, failed import as AlbumImportIncomplete, and Lidarr grabbed them again
        /// - 4 GB of residue, some albums re-downloaded 40 times. On the 33 albums observed, 20%
        /// rejects 28 of them; 50% only 18.
        ///
        /// NOTE: on the YouTube indexer 0 means DISABLED, so the filter can be switched off from
        /// the UI without rebuilding the plugin. This deliberately differs from the Spotify
        /// indexer, which inherits this setting and reads 0 as "require an exact track count"
        /// (SpotifyToYouTubeEnricher.IsTrackCountValid).
        /// </summary>
        [FieldDefinition(13, Label = "Track Count Tolerance", Type = FieldType.Number, Unit = "%", HelpText = "Maximum difference, in percent, between the track count of the monitored release and the track count offered by the indexer. Candidates outside this range are rejected during the search, before download. 0 disables the check on the YouTube indexer (on the Spotify indexer, 0 means: require an exact match).", Advanced = true)]
        public int TrackCountTolerance { get; set; } = 20;

        public string BaseUrl { get; set; } = string.Empty;

        public virtual NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}