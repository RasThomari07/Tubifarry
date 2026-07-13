using System.Diagnostics;
using System.IO.Compression;
using DownloadAssistant.Base;
using NzbDrone.Common.Instrumentation;

namespace Tubifarry.Core.Utilities
{
    /// <summary>
    /// Self-hosts the bgutil POT provider (proof-of-origin token / poToken) so the yt-dlp
    /// backend no longer needs an external Docker container. It downloads the provider's
    /// server source + the yt-dlp plugin from GitHub, runs the HTTP server via the managed
    /// <see cref="DenoLocator">Deno</see> runtime, and exposes the base URL + plugin directory.
    /// Verified: Deno 2.x runs the full server (BotGuard VM via canvas/jsdom) and mints tokens.
    /// </summary>
    public static class BgUtilProvider
    {
        // Pinned version — the server source and the yt-dlp plugin must match.
        public const string Version = "1.3.1";
        private const int Port = 4416;

        private static Process? _server;
        private static string? _baseUrl;
        private static readonly SemaphoreSlim _sem = new(1, 1);

        public static string EmbeddedDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Lidarr", "tubifarry-bgutil");

        /// <summary>Directory holding the extracted server source (src/main.ts, run by deno).</summary>
        public static string ServerDir => Path.Combine(EmbeddedDir, "server");

        /// <summary>Directory holding the bgutil yt-dlp plugin (passed to yt-dlp via --plugin-dirs).</summary>
        public static string PluginDir => Path.Combine(EmbeddedDir, "plugins");

        /// <summary>Base URL of the running managed server, or null if not started.</summary>
        public static string? BaseUrl => _baseUrl;

        /// <summary>
        /// Ensures the provider is provisioned and its HTTP server is running (via deno).
        /// Returns the base URL to hand to yt-dlp, or null if it could not be started.
        /// The first start downloads the source + installs npm deps and can take a few minutes.
        /// </summary>
        public static async Task<string?> EnsureRunningAsync(string denoExe)
        {
            if (_baseUrl != null && _server is { HasExited: false } && await PingAsync(_baseUrl).ConfigureAwait(false))
                return _baseUrl;

            await _sem.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_baseUrl != null && _server is { HasExited: false } && await PingAsync(_baseUrl).ConfigureAwait(false))
                    return _baseUrl;

                NLog.Logger log = NzbDroneLogger.GetLogger(typeof(BgUtilProvider));

                if (string.IsNullOrWhiteSpace(denoExe) || !File.Exists(denoExe))
                {
                    log.Warn("bgutil: deno runtime not available; cannot self-host the POT provider");
                    return null;
                }

                if (!await EnsureProvisionedAsync(log).ConfigureAwait(false))
                    return null;

                // Someone else (another Lidarr run, or the user's Docker) may already hold the port.
                string existing = $"http://127.0.0.1:{Port}";
                if (await PingAsync(existing).ConfigureAwait(false))
                {
                    log.Info($"bgutil: a POT provider is already listening on {existing}; reusing it");
                    return _baseUrl = existing;
                }

                return await StartServerAsync(denoExe, log).ConfigureAwait(false);
            }
            finally
            {
                _sem.Release();
            }
        }

        private static async Task<bool> EnsureProvisionedAsync(NLog.Logger log)
        {
            try
            {
                bool haveServer = File.Exists(Path.Combine(ServerDir, "src", "main.ts"));
                bool havePlugin = Directory.Exists(PluginDir) &&
                    Directory.EnumerateFiles(PluginDir, "*", SearchOption.AllDirectories).Any();
                if (haveServer && havePlugin)
                    return true;

                Directory.CreateDirectory(EmbeddedDir);

                if (!haveServer)
                {
                    // GitHub source archive: bgutil-ytdlp-pot-provider-<ver>/server/...
                    string srcUrl = $"https://github.com/Brainicism/bgutil-ytdlp-pot-provider/archive/refs/tags/{Version}.zip";
                    string srcZip = Path.Combine(EmbeddedDir, "src.zip");
                    log.Info($"bgutil: downloading server source from {srcUrl}");
                    await DownloadAsync(srcUrl, srcZip).ConfigureAwait(false);
                    string extractRoot = Path.Combine(EmbeddedDir, "_src");
                    if (Directory.Exists(extractRoot))
                        Directory.Delete(extractRoot, true);
                    ZipFile.ExtractToDirectory(srcZip, extractRoot);
                    // Move <extractRoot>/bgutil-ytdlp-pot-provider-<ver>/server -> ServerDir
                    string inner = Path.Combine(extractRoot, $"bgutil-ytdlp-pot-provider-{Version}", "server");
                    if (!Directory.Exists(inner))
                    {
                        log.Error("bgutil: server/ folder not found in the downloaded source archive");
                        return false;
                    }
                    if (Directory.Exists(ServerDir))
                        Directory.Delete(ServerDir, true);
                    Directory.Move(inner, ServerDir);
                    try { Directory.Delete(extractRoot, true); File.Delete(srcZip); } catch { }
                }

                if (!havePlugin)
                {
                    // Prebuilt yt-dlp plugin ZIP from the release assets.
                    string plugUrl = $"https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/{Version}/bgutil-ytdlp-pot-provider.zip";
                    Directory.CreateDirectory(PluginDir);
                    string plugZip = Path.Combine(PluginDir, "bgutil-ytdlp-pot-provider.zip");
                    log.Info($"bgutil: downloading yt-dlp plugin from {plugUrl}");
                    await DownloadAsync(plugUrl, plugZip).ConfigureAwait(false);
                    // yt-dlp loads plugin ZIPs directly from a --plugin-dirs directory; keep the zip.
                }
                return true;
            }
            catch (Exception ex)
            {
                log.Error(ex, "bgutil: provisioning failed");
                return false;
            }
        }

        private static async Task<string?> StartServerAsync(string denoExe, NLog.Logger log)
        {
            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = denoExe,
                    WorkingDirectory = ServerDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string a in new[] { "run", "-A", "--node-modules-dir=auto", "--allow-scripts",
                    Path.Combine("src", "main.ts"), "-p", Port.ToString() })
                    psi.ArgumentList.Add(a);

                log.Info($"bgutil: starting POT server via deno on port {Port} (first run installs deps, ~2-3 min)");
                Process proc = new() { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) log.Trace($"[bgutil] {e.Data}"); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log.Trace($"[bgutil:err] {e.Data}"); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                _server = proc;

                // Poll /ping until the server answers (long timeout to cover first-run dep install).
                string url = $"http://127.0.0.1:{Port}";
                for (int i = 0; i < 120; i++)
                {
                    if (proc.HasExited)
                    {
                        log.Error($"bgutil: server process exited early (code {proc.ExitCode})");
                        return null;
                    }
                    if (await PingAsync(url).ConfigureAwait(false))
                    {
                        log.Info($"bgutil: POT server ready at {url}");
                        return _baseUrl = url;
                    }
                    await Task.Delay(2000).ConfigureAwait(false);
                }
                log.Error("bgutil: server did not become ready in time");
                return null;
            }
            catch (Exception ex)
            {
                log.Error(ex, "bgutil: failed to start server");
                return null;
            }
        }

        private static async Task<bool> PingAsync(string baseUrl)
        {
            try
            {
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
                using HttpResponseMessage resp = await HttpGet.HttpClient.GetAsync($"{baseUrl}/ping", cts.Token).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private static async Task DownloadAsync(string url, string target)
        {
            using HttpResponseMessage resp = await HttpGet.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using FileStream fs = File.Create(target);
            await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
        }

        /// <summary>Stops the managed server (best effort), e.g. on plugin unload.</summary>
        public static void Stop()
        {
            try { if (_server is { HasExited: false }) _server.Kill(true); } catch { }
            _server = null;
            _baseUrl = null;
        }
    }
}
