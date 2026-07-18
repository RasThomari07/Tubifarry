using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Download.Clients.YouTube
{
    public class YoutubeProviderSettingsValidator : AbstractValidator<YoutubeProviderSettings>
    {
        public YoutubeProviderSettingsValidator()
        {
            // Validate DownloadPath
            RuleFor(x => x.DownloadPath)
                .IsValidPath()
                .WithMessage("Download path must be a valid directory.");

            // Validate CookiePath (if provided)
            RuleFor(x => x.CookiePath)
                .Must(path => string.IsNullOrEmpty(path) || System.IO.File.Exists(path))
                .WithMessage("Cookie file does not exist. Please provide a valid path to the cookies file.")
                .Must(path => string.IsNullOrEmpty(path) || CookieManager.ParseCookieFile(path).Length != 0)
                .WithMessage("Cookie file is invalid or contains no valid cookies.");

            // Validate Chunks
            RuleFor(x => x.Chunks)
                .Must(chunks => chunks > 0 && chunks < 5)
                .WithMessage("Chunks must be greater than 0 and smaller than 5.");

            // Validate FFmpegPath only when non-empty (auto-download handles the missing case)
            RuleFor(x => x.FFmpegPath)
                .IsValidPath()
                .When(x => !string.IsNullOrEmpty(x.FFmpegPath))
                .WithMessage("Invalid FFmpeg path. Please provide a valid directory containing ffmpeg.");

            // Validate Random Delay Range
            RuleFor(x => x.RandomDelayMin)
                .LessThanOrEqualTo(x => x.RandomDelayMax)
                .WithMessage("Minimum delay must be less than or equal to maximum delay.");

            RuleFor(x => x.RandomDelayMax)
                .GreaterThanOrEqualTo(x => x.RandomDelayMin)
                .WithMessage("Maximum delay must be greater than or equal to minimum delay.")
                .GreaterThanOrEqualTo(_ => 0)
                .WithMessage("Maximum delay must be greater than or equal to 0.");

            // Validate Max Download Speed
            RuleFor(x => x.MaxDownloadSpeed)
                .GreaterThanOrEqualTo(0)
                .WithMessage("Max download speed must be greater than or equal to 0.")
                .LessThanOrEqualTo(2_500)
                .WithMessage("Max download speed must be less than or equal to 20 Mbps (2,500 KB/s).");

            // Validate TrustedSessionGeneratorUrl
            RuleFor(x => x.TrustedSessionGeneratorUrl)
                .Must(url => string.IsNullOrEmpty(url) || Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .WithMessage("Trusted Session Generator URL must be a valid URL if provided.");

            // Validate SponsorBlock API endpoint
            RuleFor(x => x.SponsorBlockApiEndpoint)
                .NotEmpty()
                .When(x => x.UseSponsorBlock)
                .WithMessage("SponsorBlock API endpoint is required when SponsorBlock is enabled.")
                .Must(url => Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .When(x => x.UseSponsorBlock && !string.IsNullOrEmpty(x.SponsorBlockApiEndpoint))
                .WithMessage("SponsorBlock API endpoint must be a valid URL.");

            // yt-dlp path is optional: when empty, the backend auto-downloads and self-updates a
            // managed copy (and auto-provisions deno). Only validate a path if one is supplied.
            RuleFor(x => x.YtDlpPath)
                .Must(path => string.IsNullOrEmpty(path) || System.IO.File.Exists(path))
                .When(x => x.UseYtDlp)
                .WithMessage("yt-dlp executable not found at the specified path (leave empty to auto-download).");

            RuleFor(x => x.BgUtilUrl)
                .Must(url => string.IsNullOrEmpty(url) || Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .WithMessage("bgutil POT Provider URL must be a valid URL if provided.");
        }
    }

    public class YoutubeProviderSettings : IProviderConfig
    {
        private static readonly YoutubeProviderSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Download Path", Type = FieldType.Path, HelpText = "Specify the directory where downloaded files will be saved.")]
        public string DownloadPath { get; set; } = "";

        [FieldDefinition(1, Label = "Cookie Path", Type = FieldType.FilePath, Hidden = HiddenType.Visible, Placeholder = "/downloads/Cookies/cookies.txt", HelpText = "Specify the path to the YouTube cookies file. This is optional but required for accessing restricted content.", Advanced = true)]
        public string CookiePath { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Use ID3v2.3 Tags", HelpText = "Enable this option to use ID3v2.3 tags for better compatibility with older media players like Windows Media Player.", Type = FieldType.Checkbox, Advanced = true)]
        public bool UseID3v2_3 { get; set; }

        [FieldDefinition(3, Label = "ReEncode", Type = FieldType.Select, SelectOptions = typeof(ReEncodeOptions), HelpText = "Specify whether to re-encode audio files and how to handle FFmpeg.", Advanced = true)]
        public int ReEncode { get; set; } = (int)ReEncodeOptions.Disabled;

        [FieldDefinition(4, Label = "FFmpeg Path", Type = FieldType.Path, Placeholder = "/downloads/FFmpeg", HelpText = "Directory containing ffmpeg. Used for re-encoding and yt-dlp audio extraction. Leave empty to auto-download a shared copy to ProgramData/Lidarr/tubifarry-ffmpeg.", Advanced = true)]
        public string FFmpegPath { get; set; } = string.Empty;

        [FieldDefinition(5, Label = "File Chunk Count", Type = FieldType.Number, HelpText = "Number of chunks to split the download into. Each chunk is its own download. Note: Non-chunked downloads from YouTube are typically much slower.", Advanced = true)]
        public int Chunks { get; set; } = 2;

        [FieldDefinition(6, Label = "Delay Min", Type = FieldType.Number, HelpText = "Minimum random delay between requests to avoid bot notifications.", Unit = "ms", Advanced = true)]
        public int RandomDelayMin { get; set; } = 100;

        [FieldDefinition(7, Label = "Delay Max", Type = FieldType.Number, HelpText = "Maximum random delay between requests to avoid bot notifications.", Unit = "ms", Advanced = true)]
        public int RandomDelayMax { get; set; } = 2000;

        [FieldDefinition(8, Label = "Max Download Speed", Type = FieldType.Number, HelpText = "Set to 0 for unlimited speed. Limits the download speed per download.", Unit = "KB/s", Advanced = true)]
        public int MaxDownloadSpeed { get; set; }

        [FieldDefinition(9, Label = "Trusted Session Generator URL", Type = FieldType.Textbox, Placeholder = "http://localhost:8080", HelpText = "URL to the YouTube Trusted Session Generator service. When provided, PoToken and Visitor Data will be fetched automatically.", Advanced = true)]
        public string TrustedSessionGeneratorUrl { get; set; } = string.Empty;

        [FieldDefinition(10, Label = "Use SponsorBlock", Type = FieldType.Checkbox, HelpText = "Enable SponsorBlock integration to automatically remove non-music segments (intros, outros, talking) from downloaded tracks.")]
        public bool UseSponsorBlock { get; set; }

        [FieldDefinition(11, Label = "SponsorBlock API Endpoint", Type = FieldType.Textbox, Placeholder = "https://sponsor.ajay.app", HelpText = "SponsorBlock API endpoint URL. Change only if using a custom SponsorBlock instance.", Advanced = true)]
        public string SponsorBlockApiEndpoint { get; set; } = "https://sponsor.ajay.app";

        [FieldDefinition(12, Label = "Use yt-dlp Backend", Type = FieldType.Checkbox, HelpText = "Download via yt-dlp + bgutil instead of the native YouTubeMusicAPI extraction (which is broken upstream: 'Failed to get streaming data'). Requires the yt-dlp path below and a running bgutil POT provider.")]
        public bool UseYtDlp { get; set; } = true;

        [FieldDefinition(13, Label = "yt-dlp Path", Type = FieldType.FilePath, Placeholder = @"leave empty to auto-download", HelpText = "Full path to the yt-dlp executable. Leave empty to auto-download and self-update a managed copy in ProgramData/Lidarr/tubifarry-ytdlp; deno is auto-provisioned too for signature solving.", Advanced = true)]
        public string YtDlpPath { get; set; } = string.Empty;

        [FieldDefinition(14, Label = "bgutil POT Provider URL", Type = FieldType.Textbox, Placeholder = "leave empty to auto-manage", HelpText = "Base URL of an external bgutil POT provider (poToken GVS). Leave empty to auto-download and self-host the provider via the managed deno runtime in ProgramData/Lidarr/tubifarry-bgutil (no Docker/Node needed).", Advanced = true)]
        public string BgUtilUrl { get; set; } = string.Empty;

        [FieldDefinition(15, Label = "yt-dlp Player Client", Type = FieldType.Textbox, Placeholder = "web_music", HelpText = "YouTube player client passed to yt-dlp. web_music is music.youtube.com's own client and serves audio; web/web_safari/mweb hit the captcha wall and tv returns DRM-only formats. Fallback: tv_simply.", Advanced = true)]
        public string PlayerClient { get; set; } = "web_music";

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public enum ReEncodeOptions
    {
        [FieldOption(Label = "Disabled", Hint = "No re-encoding, keep original.")]
        Disabled,

        [FieldOption(Label = "Only Extract", Hint = "Extract audio, no re-encoding.")]
        OnlyExtract,

        [FieldOption(Label = "AAC", Hint = "Re-encode to AAC (VBR).")]
        AAC,

        [FieldOption(Label = "MP3", Hint = "Re-encode to MP3 (VBR).")]
        MP3,

        [FieldOption(Label = "Opus", Hint = "Re-encode to Opus (VBR).")]
        Opus,

        [FieldOption(Label = "Vorbis", Hint = "Re-encode to Vorbis (fixed 224 kbps).")]
        Vorbis
    }
}