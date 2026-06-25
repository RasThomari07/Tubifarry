using Tubifarry.Download.Base;
using YouTubeMusicAPI.Client;

namespace Tubifarry.Download.Clients.YouTube
{
    /// <summary>
    /// YouTube-specific download options extending the base options
    /// </summary>
    public record YouTubeDownloadOptions : BaseDownloadOptions
    {
        /// <summary>
        /// YouTube Music API client instance
        /// </summary>
        public YouTubeMusicClient? YouTubeMusicClient { get; set; }

        /// <summary>
        /// Re-encoding options for audio format conversion
        /// </summary>
        public ReEncodeOptions ReEncodeOptions { get; set; }

        /// <summary>
        /// Whether to use ID3v2.3 tags instead of ID3v2.4
        /// </summary>
        public bool UseID3v2_3 { get; set; }

        /// <summary>
        /// Minimum random delay in milliseconds
        /// </summary>
        public int RandomDelayMin { get; set; } = 100;

        /// <summary>
        /// Maximum random delay in milliseconds
        /// </summary>
        public int RandomDelayMax { get; set; } = 2000;

        /// <summary>
        /// Whether to use SponsorBlock for trimming
        /// </summary>
        public bool UseSponsorBlock { get; set; }

        /// <summary>
        /// SponsorBlock API endpoint URL
        /// </summary>
        public string SponsorBlockApiEndpoint { get; set; } = "https://sponsor.ajay.app";

        /// <summary>
        /// URL to the Trusted Session Generator service
        /// </summary>
        public string? TrustedSessionGeneratorUrl { get; set; }

        /// <summary>
        /// Use the yt-dlp + bgutil backend instead of the native YouTubeMusicAPI extraction.
        /// </summary>
        public bool UseYtDlp { get; set; }

        /// <summary>Full path to the yt-dlp executable.</summary>
        public string? YtDlpPath { get; set; }

        /// <summary>Base URL of the bgutil POT provider (poToken).</summary>
        public string? BgUtilUrl { get; set; }

        /// <summary>YouTube player client passed to yt-dlp (e.g. web_safari).</summary>
        public string? PlayerClient { get; set; }

        /// <summary>Path to the YouTube cookies.txt (passed to yt-dlp as --cookies).</summary>
        public string? CookiePath { get; set; }

        /// <summary>Directory containing ffmpeg, passed to yt-dlp as --ffmpeg-location.</summary>
        public string? FFmpegPath { get; set; }

        public YouTubeDownloadOptions() { }

        protected YouTubeDownloadOptions(YouTubeDownloadOptions options) : base(options)
        {
            YouTubeMusicClient = options.YouTubeMusicClient;
            ReEncodeOptions = options.ReEncodeOptions;
            UseID3v2_3 = options.UseID3v2_3;
            RandomDelayMin = options.RandomDelayMin;
            RandomDelayMax = options.RandomDelayMax;
            UseSponsorBlock = options.UseSponsorBlock;
            SponsorBlockApiEndpoint = options.SponsorBlockApiEndpoint;
            TrustedSessionGeneratorUrl = options.TrustedSessionGeneratorUrl;
            UseYtDlp = options.UseYtDlp;
            YtDlpPath = options.YtDlpPath;
            BgUtilUrl = options.BgUtilUrl;
            PlayerClient = options.PlayerClient;
            CookiePath = options.CookiePath;
            FFmpegPath = options.FFmpegPath;
        }
    }
}