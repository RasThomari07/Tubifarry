using System.IO.Compression;
using System.Runtime.InteropServices;
using DownloadAssistant.Base;
using NzbDrone.Common.Instrumentation;

namespace Tubifarry.Core.Utilities
{
    /// <summary>
    /// Resolves a Deno runtime, downloading a private copy from the official GitHub
    /// releases when none is found. Deno is a single self-contained binary and serves
    /// two purposes for the yt-dlp backend: it runs the EJS signature/n solver
    /// (<c>--remote-components ejs:github</c>) and the bgutil POT provider (Deno >= 2.0).
    /// Resolution order: embedded copy → system PATH → auto-download+extract.
    /// </summary>
    public static class DenoLocator
    {
        private static string? _resolvedDir;
        private static readonly SemaphoreSlim _sem = new(1, 1);

        /// <summary>Directory holding the auto-managed Deno binary (survives plugin upgrades).</summary>
        public static string EmbeddedDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Lidarr", "tubifarry-deno");

        public static string ExeName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "deno.exe" : "deno";

        /// <summary>Directory that should be added to the yt-dlp process PATH (null until resolved).</summary>
        public static string? ResolvedDir => _resolvedDir;

        /// <summary>Full path to the resolved deno executable, or null.</summary>
        public static string? ResolvedExe => _resolvedDir == null ? null : Path.Combine(_resolvedDir, ExeName);

        /// <summary>GitHub release asset (a .zip) for the current OS + architecture.</summary>
        private static string AssetName()
        {
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "deno-x86_64-pc-windows-msvc.zip"; // Deno ships x64 only for Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return $"deno-{arch}-apple-darwin.zip";
            return $"deno-{arch}-unknown-linux-gnu.zip";
        }

        public static async Task<string?> ResolveAsync()
        {
            if (_resolvedDir != null && File.Exists(Path.Combine(_resolvedDir, ExeName)))
                return _resolvedDir;

            await _sem.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_resolvedDir != null && File.Exists(Path.Combine(_resolvedDir, ExeName)))
                    return _resolvedDir;

                // 1. Embedded copy already present
                if (File.Exists(Path.Combine(EmbeddedDir, ExeName)))
                    return Commit(EmbeddedDir);

                // 2. System PATH
                foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    try
                    {
                        if (File.Exists(Path.Combine(entry.Trim(), ExeName)))
                            return Commit(entry.Trim());
                    }
                    catch { /* malformed PATH entry */ }
                }

                // 3. Auto-download + extract
                return await DownloadLatestAsync().ConfigureAwait(false);
            }
            finally
            {
                _sem.Release();
            }
        }

        private static async Task<string?> DownloadLatestAsync()
        {
            NLog.Logger log = NzbDroneLogger.GetLogger(typeof(DenoLocator));
            string url = $"https://github.com/denoland/deno/releases/latest/download/{AssetName()}";
            try
            {
                Directory.CreateDirectory(EmbeddedDir);
                string zip = Path.Combine(EmbeddedDir, "deno.zip");
                log.Info($"Deno not found; downloading latest release from {url}");
                using (HttpResponseMessage resp = await HttpGet.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    await using FileStream fs = File.Create(zip);
                    await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
                }
                // Extract deno(.exe) from the archive
                using (ZipArchive archive = ZipFile.OpenRead(zip))
                {
                    ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, ExeName, StringComparison.OrdinalIgnoreCase));
                    entry?.ExtractToFile(Path.Combine(EmbeddedDir, ExeName), overwrite: true);
                }
                try { File.Delete(zip); } catch { /* best effort */ }

                string exe = Path.Combine(EmbeddedDir, ExeName);
                if (!File.Exists(exe))
                {
                    log.Error("Deno archive did not contain the expected executable");
                    return null;
                }
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    TryMakeExecutable(exe);
                log.Info($"Deno downloaded to {exe}");
                return Commit(EmbeddedDir);
            }
            catch (Exception ex)
            {
                log.Error(ex, "Deno auto-download failed");
                return null;
            }
        }

        public static void Invalidate() => _resolvedDir = null;

        private static string Commit(string dir)
        {
            _resolvedDir = dir;
            return dir;
        }

        private static void TryMakeExecutable(string path)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new() { FileName = "chmod", UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("+x");
                psi.ArgumentList.Add(path);
                System.Diagnostics.Process.Start(psi)?.WaitForExit();
            }
            catch { /* best effort */ }
        }
    }
}
