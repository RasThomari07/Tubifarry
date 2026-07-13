using System.Diagnostics;
using System.Runtime.InteropServices;
using DownloadAssistant.Base;
using NzbDrone.Common.Instrumentation;

namespace Tubifarry.Core.Utilities
{
    /// <summary>
    /// Resolves a working yt-dlp executable, downloading a private copy from the
    /// official GitHub releases when none is configured/found. Mirrors <see cref="FfmpegLocator"/>.
    /// Resolution order: configured path → embedded copy → system PATH → auto-download.
    /// The embedded copy lives under ProgramData/Lidarr so it survives plugin upgrades.
    /// </summary>
    public static class YtDlpLocator
    {
        private static string? _resolvedPath;
        private static readonly SemaphoreSlim _sem = new(1, 1);

        /// <summary>Directory holding the auto-managed yt-dlp binary.</summary>
        public static string EmbeddedDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Lidarr", "tubifarry-ytdlp");

        /// <summary>Platform executable name (yt-dlp.exe on Windows, yt-dlp elsewhere).</summary>
        public static string ExeName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";

        /// <summary>GitHub release asset name for the current platform.</summary>
        private static string AssetName =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "yt-dlp_macos" : "yt-dlp_linux";

        /// <summary>Last successfully resolved yt-dlp path (null until ResolveAsync succeeds).</summary>
        public static string? ResolvedPath => _resolvedPath;

        /// <summary>True if the resolved binary is the one we auto-manage (so we may self-update it).</summary>
        public static bool IsSelfManaged => _resolvedPath != null &&
            _resolvedPath.StartsWith(EmbeddedDir, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Resolves a usable yt-dlp path, downloading to the embedded dir if necessary.
        /// Returns the resolved path, or null if every attempt fails.
        /// </summary>
        public static async Task<string?> ResolveAsync(string? configuredPath)
        {
            if (_resolvedPath != null && File.Exists(_resolvedPath))
                return _resolvedPath;

            await _sem.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_resolvedPath != null && File.Exists(_resolvedPath))
                    return _resolvedPath;

                // 1. Configured path (a file, or a directory containing the exe)
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    string candidate = File.Exists(configuredPath)
                        ? configuredPath
                        : Path.Combine(configuredPath, ExeName);
                    if (File.Exists(candidate))
                        return Commit(candidate);
                }

                // 2. Embedded copy already present
                string embedded = Path.Combine(EmbeddedDir, ExeName);
                if (File.Exists(embedded))
                    return Commit(embedded);

                // 3. System PATH
                foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    try
                    {
                        string p = Path.Combine(entry.Trim(), ExeName);
                        if (File.Exists(p))
                            return Commit(p);
                    }
                    catch { /* malformed PATH entry */ }
                }

                // 4. Auto-download the latest release to the embedded dir
                return await DownloadLatestAsync(embedded).ConfigureAwait(false);
            }
            finally
            {
                _sem.Release();
            }
        }

        /// <summary>Downloads the latest yt-dlp release binary to <paramref name="target"/>.</summary>
        private static async Task<string?> DownloadLatestAsync(string target)
        {
            NLog.Logger log = NzbDroneLogger.GetLogger(typeof(YtDlpLocator));
            string url = $"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{AssetName}";
            try
            {
                Directory.CreateDirectory(EmbeddedDir);
                log.Info($"yt-dlp not found; downloading latest release from {url}");
                using HttpResponseMessage resp = await HttpGet.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                string tmp = target + ".tmp";
                await using (FileStream fs = File.Create(tmp))
                    await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
                if (File.Exists(target))
                    File.Delete(target);
                File.Move(tmp, target);
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    TryMakeExecutable(target);
                log.Info($"yt-dlp downloaded to {target}");
                return File.Exists(target) ? Commit(target) : null;
            }
            catch (Exception ex)
            {
                log.Error(ex, "yt-dlp auto-download failed");
                return null;
            }
        }

        /// <summary>
        /// Runs "yt-dlp -U" to self-update the managed binary (no-op for a user-supplied path).
        /// YouTube breaks yt-dlp often, so callers should invoke this periodically.
        /// </summary>
        public static async Task<bool> TryUpdateAsync(string ytDlpPath)
        {
            NLog.Logger log = NzbDroneLogger.GetLogger(typeof(YtDlpLocator));
            try
            {
                if (string.IsNullOrWhiteSpace(ytDlpPath) || !File.Exists(ytDlpPath))
                    return false;
                ProcessStartInfo psi = new()
                {
                    FileName = ytDlpPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-U");
                using Process proc = new() { StartInfo = psi };
                proc.Start();
                string outp = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                log.Debug($"yt-dlp -U: {outp.Trim()}");
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                log.Warn(ex, "yt-dlp self-update failed");
                return false;
            }
        }

        /// <summary>Clears the cached path so the next ResolveAsync performs a fresh lookup.</summary>
        public static void Invalidate() => _resolvedPath = null;

        private static string Commit(string path)
        {
            _resolvedPath = path;
            return path;
        }

        private static void TryMakeExecutable(string path)
        {
            try
            {
                ProcessStartInfo psi = new() { FileName = "chmod", UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("+x");
                psi.ArgumentList.Add(path);
                Process.Start(psi)?.WaitForExit();
            }
            catch { /* best effort */ }
        }
    }
}
