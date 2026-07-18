using System.Diagnostics;
using System.Globalization;
using NLog;
using Tubifarry.Core.Utilities;

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

        /// <summary>Result of a yt-dlp run: process exit code + whether YouTube signalled an IP/session soft-block.</summary>
        public readonly record struct YtDlpResult(int ExitCode, bool SoftBlocked);

        public record YtDlpRunOptions
        {
            /// <summary>Full path to the yt-dlp executable.</summary>
            public required string YtDlpPath { get; init; }

            /// <summary>Album/playlist URL (e.g. music.youtube.com/playlist?list=OLAK5uy_...).</summary>
            public required string Url { get; init; }

            /// <summary>Folder where audio files are written (created if missing).</summary>
            public required string DestinationPath { get; init; }

            public string BgUtilUrl { get; init; } = "http://127.0.0.1:4416";
            // web_music is music.youtube.com's own client. Measured 12 Jul on one captcha-blocked
            // track, same IP + cookies: web / web_safari / mweb -> captcha; tv -> DRM-only formats
            // (no audio); web_music and tv_simply -> audio served. Fallback: tv_simply.
            public string PlayerClient { get; init; } = "web_music";
            public string? CookiePath { get; init; }

            /// <summary>Directory containing ffmpeg, passed as --ffmpeg-location.</summary>
            public string? FFmpegDir { get; init; }

            /// <summary>Directory containing deno, prepended to the process PATH (signature solver + bgutil).</summary>
            public string? DenoDir { get; init; }

            /// <summary>Directory holding the bgutil yt-dlp plugin, passed via --plugin-dirs (self-managed provider).</summary>
            public string? BgUtilPluginDir { get; init; }

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
        public static async Task<YtDlpResult> RunAsync(YtDlpRunOptions opt, Logger logger, Action<string>? onTrackCompleted, CancellationToken token)
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
                // fetch_pot=always forces yt-dlp to attach a PO Token to the *player* request,
                // not just the GVS stream. YouTube now gates the player request itself: without
                // this, web_music returns "playability: LOGIN_REQUIRED / Sign in to confirm
                // you're not a bot" and the download never starts, even though bgutil is up.
                "--extractor-args", $"youtubepot-bgutilhttp:base_url={opt.BgUtilUrl}",
                "--extractor-args", $"youtube:player_client={opt.PlayerClient};fetch_pot=always",
                // Anti-bot pacing. These are the values yt-dlp itself recommends in its
                // rate-limit error message (the "-t sleep" preset); 1/5 was too aggressive and
                // kept the session rate-limited.
                "--sleep-requests", opt.SleepRequests.ToString(CultureInfo.InvariantCulture),
                "--sleep-interval", "10",
                "--max-sleep-interval", "20",
                "--fragment-retries", "3",
                "-f", "bestaudio/best",
                "-x",
                "-o", Path.Combine(opt.DestinationPath, opt.OutputTemplate),
            };
            if (!string.IsNullOrWhiteSpace(opt.BgUtilPluginDir) && Directory.Exists(opt.BgUtilPluginDir))
            {
                // Make the self-managed bgutil yt-dlp plugin discoverable (the auto-downloaded
                // yt-dlp has no plugins of its own).
                args.Add("--plugin-dirs");
                args.Add(opt.BgUtilPluginDir);
            }
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

            // Make the auto-managed deno visible to yt-dlp (EJS signature solver + bgutil) without
            // requiring it on the system PATH.
            if (!string.IsNullOrWhiteSpace(opt.DenoDir) && Directory.Exists(opt.DenoDir))
                psi.Environment["PATH"] = opt.DenoDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

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
            bool softBlocked = false;
            object softBlockLock = new();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    return;
                logger.Trace($"[yt-dlp:err] {e.Data}");
                if (YouTubeCircuitBreaker.IsSoftBlockMarker(e.Data))
                    lock (softBlockLock) softBlocked = true;
            };

            proc.Start();
            // Tie yt-dlp (and the deno signature solver it spawns) to Lidarr's lifetime so a
            // Lidarr crash/restart mid-download can't leave orphaned deno processes behind.
            ChildProcessTracker.Track(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(token).ConfigureAwait(false);
                proc.WaitForExit(); // flush async stdout/stderr handlers before reading softBlocked
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { /* best effort */ }
                throw;
            }

            bool wasSoftBlocked;
            lock (softBlockLock) wasSoftBlocked = softBlocked;
            return new YtDlpResult(proc.ExitCode, wasSoftBlocked);
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
