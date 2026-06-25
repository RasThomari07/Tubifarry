using System.Diagnostics;
using System.Globalization;
using NLog;

namespace Tubifarry.Download.Clients.YouTube
{
    /// <summary>
    /// Downloads a YouTube Music album/playlist via the yt-dlp executable (with the
    /// bgutil-ytdlp-pot-provider plugin for the GVS poToken). Replaces the native
    /// YouTubeMusicAPI streaming extraction, which is broken upstream
    /// ("Failed to get streaming data"). yt-dlp keeps an up-to-date player/signature
    /// implementation and consumes the bgutil poToken, so downloads actually succeed.
    /// </summary>
    public static class YtDlpDownloader
    {
        public static readonly string[] AudioExtensions = { ".m4a", ".opus", ".mp3", ".ogg", ".webm", ".aac", ".flac" };

        public record YtDlpRunOptions
        {
            /// <summary>Full path to the yt-dlp executable.</summary>
            public required string YtDlpPath { get; init; }

            /// <summary>Album/playlist URL (e.g. music.youtube.com/playlist?list=OLAK5uy_...).</summary>
            public required string Url { get; init; }

            /// <summary>Folder where audio files are written (created if missing).</summary>
            public required string DestinationPath { get; init; }

            public string BgUtilUrl { get; init; } = "http://127.0.0.1:4416";
            public string PlayerClient { get; init; } = "web_safari";
            public string? CookiePath { get; init; }

            /// <summary>Directory containing ffmpeg, passed as --ffmpeg-location.</summary>
            public string? FFmpegDir { get; init; }

            /// <summary>yt-dlp output template, relative to DestinationPath.</summary>
            public string OutputTemplate { get; init; } = "%(playlist_index)02d - %(title)s.%(ext)s";

            public int SleepRequests { get; init; } = 2;
        }

        /// <summary>
        /// Runs yt-dlp and waits for completion. Returns the process exit code
        /// (0 = full success; non-zero usually means some tracks failed while others
        /// may still have downloaded). <paramref name="onTrackCompleted"/> fires once
        /// per finished file so the caller can report queue progress.
        /// </summary>
        public static async Task<int> RunAsync(YtDlpRunOptions opt, Logger logger, Action<string>? onTrackCompleted, CancellationToken token)
        {
            if (!File.Exists(opt.YtDlpPath))
                throw new FileNotFoundException($"yt-dlp executable not found: {opt.YtDlpPath}");

            Directory.CreateDirectory(opt.DestinationPath);

            List<string> args = new()
            {
                "--ignore-errors",
                "--no-overwrites",
                "--ignore-config",
                "--no-warnings",
                // Download the JS challenge solver (signature + n). Needs deno on PATH.
                "--remote-components", "ejs:github",
                // poToken provider (bgutil) + a player client that actually consumes it.
                "--extractor-args", $"youtubepot-bgutilhttp:base_url={opt.BgUtilUrl}",
                "--extractor-args", $"youtube:player_client={opt.PlayerClient}",
                // Anti-bot pacing.
                "--sleep-requests", opt.SleepRequests.ToString(CultureInfo.InvariantCulture),
                "--sleep-interval", "1",
                "--max-sleep-interval", "5",
                "--fragment-retries", "3",
                "-f", "bestaudio/best",
                "-x",
                "-o", Path.Combine(opt.DestinationPath, opt.OutputTemplate),
            };
            if (!string.IsNullOrWhiteSpace(opt.CookiePath) && File.Exists(opt.CookiePath))
            {
                args.Add("--cookies");
                args.Add(opt.CookiePath);
            }
            if (!string.IsNullOrWhiteSpace(opt.FFmpegDir) && Directory.Exists(opt.FFmpegDir))
            {
                args.Add("--ffmpeg-location");
                args.Add(opt.FFmpegDir);
            }
            args.Add(opt.Url);

            ProcessStartInfo psi = new()
            {
                FileName = opt.YtDlpPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in args)
                psi.ArgumentList.Add(a);

            logger.Debug($"yt-dlp backend: \"{opt.YtDlpPath}\" {string.Join(' ', args)}");

            using Process proc = new() { StartInfo = psi, EnableRaisingEvents = true };

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    return;
                logger.Trace($"[yt-dlp] {e.Data}");
                if (onTrackCompleted != null &&
                    (e.Data.Contains("[ExtractAudio] Destination:", StringComparison.Ordinal) ||
                     e.Data.Contains("has already been downloaded", StringComparison.Ordinal)))
                {
                    onTrackCompleted(e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    logger.Trace($"[yt-dlp:err] {e.Data}");
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { /* best effort */ }
                throw;
            }

            return proc.ExitCode;
        }

        /// <summary>Enumerates audio files produced in the destination folder, sorted by name.</summary>
        public static IReadOnlyList<string> EnumerateAudioFiles(string destinationPath)
        {
            if (!Directory.Exists(destinationPath))
                return Array.Empty<string>();
            return Directory.EnumerateFiles(destinationPath)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
