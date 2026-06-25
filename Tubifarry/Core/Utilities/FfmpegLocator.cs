using NzbDrone.Common.Instrumentation;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace Tubifarry.Core.Utilities
{
    /// <summary>
    /// Resolves a working FFmpeg directory, downloading a private copy when none is found.
    /// Resolution order: configured path → embedded path → system PATH → auto-download.
    /// </summary>
    public static class FfmpegLocator
    {
        private static string? _resolvedPath;
        private static readonly SemaphoreSlim _sem = new(1, 1);

        /// <summary>
        /// Default download target when no path is configured.
        /// Lives in ProgramData/Lidarr so it survives plugin folder upgrades.
        /// </summary>
        public static string EmbeddedPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Lidarr", "tubifarry-ffmpeg");

        /// <summary>Last successfully resolved FFmpeg directory (null until ResolveAsync completes).</summary>
        public static string? ResolvedPath => _resolvedPath;

        /// <summary>Returns true if <paramref name="dir"/> contains a valid ffmpeg executable.</summary>
        public static bool HasFFmpegAt(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return false;
            string[] names = ["ffmpeg", "ffmpeg.exe"];
            return Directory.EnumerateFiles(dir)
                .Any(f => names.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                          && IsExecutable(f));
        }

        /// <summary>
        /// Resolves a working FFmpeg directory, downloading if necessary.
        /// Returns the resolved directory path, or null if all attempts fail.
        /// </summary>
        public static async Task<string?> ResolveAsync(string? configuredPath)
        {
            // Fast path: cached and still present on disk
            if (_resolvedPath != null && HasFFmpegAt(_resolvedPath))
                return _resolvedPath;

            await _sem.WaitAsync().ConfigureAwait(false);
            try
            {
                // Double-checked inside lock
                if (_resolvedPath != null && HasFFmpegAt(_resolvedPath))
                    return _resolvedPath;

                // 1. Configured path
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    string dir = File.Exists(configuredPath)
                        ? Path.GetDirectoryName(configuredPath)!
                        : configuredPath;
                    if (HasFFmpegAt(dir))
                        return Commit(dir);
                }

                // 2. Embedded path already present
                if (HasFFmpegAt(EmbeddedPath))
                    return Commit(EmbeddedPath);

                // 3. System PATH
                foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    if (HasFFmpegAt(entry))
                        return Commit(entry);
                }

                // 4. Auto-download to embedded (fallback to configured if provided)
                string target = string.IsNullOrWhiteSpace(configuredPath) ? EmbeddedPath : configuredPath;
                NzbDroneLogger.GetLogger(typeof(FfmpegLocator)).Info($"FFmpeg not found; downloading to '{target}'");
                try
                {
                    Directory.CreateDirectory(target);
                    await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, target).ConfigureAwait(false);
                    if (HasFFmpegAt(target))
                        return Commit(target);
                }
                catch (Exception ex)
                {
                    NzbDroneLogger.GetLogger(typeof(FfmpegLocator)).Error(ex, "FFmpeg auto-download failed");
                }

                return null;
            }
            finally
            {
                _sem.Release();
            }
        }

        /// <summary>Clears the cached path so the next call to ResolveAsync performs a fresh lookup.</summary>
        public static void Invalidate() => _resolvedPath = null;

        private static string Commit(string dir)
        {
            _resolvedPath = dir;
            FFmpeg.SetExecutablesPath(dir);
            return dir;
        }

        private static bool IsExecutable(string filePath)
        {
            try
            {
                using FileStream stream = File.OpenRead(filePath);
                byte[] magic = new byte[4];
                stream.Read(magic, 0, 4);
                return (magic[0] == 0x4D && magic[1] == 0x5A)                                                          // Windows PE
                    || (magic[0] == 0x7F && magic[1] == 0x45 && magic[2] == 0x4C && magic[3] == 0x46)                 // Linux ELF
                    || (magic[0] == 0xFE && magic[1] == 0xED && magic[2] == 0xFA)                                      // macOS Mach-O
                    || (magic[0] == 0xCA && magic[1] == 0xFE && magic[2] == 0xBA && magic[3] == 0xBE);                 // macOS fat binary
            }
            catch { return false; }
        }
    }
}
