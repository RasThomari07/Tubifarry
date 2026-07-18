using System.Text.Json;
using NLog;

namespace Tubifarry.Download.Clients.YouTube
{
    /// <summary>
    /// Process-wide circuit breaker for YouTube soft-blocks (bot check / captcha / HTTP 429).
    ///
    /// When YouTube starts refusing playback for the whole IP/session, retrying only makes it
    /// worse: every extra yt-dlp call re-arms the penalty, so a ~1h block snowballs into a
    /// multi-day outage (observed 9-12 Jul). This breaker detects the block from yt-dlp's stderr,
    /// then "opens" for a cool-down during which album downloads fail fast WITHOUT invoking yt-dlp,
    /// so the queue drains without hammering YouTube. It closes again on the first clean download
    /// (or when the cool-down elapses). In-process equivalent of the youtube-blackhole script's
    /// grab_blocked.json breaker.
    ///
    /// The open/closed state is persisted to disk: a Lidarr restart during a block used to reset
    /// the breaker and send the whole queue straight back at YouTube, which is how the 9-12 Jul
    /// outage kept renewing itself.
    /// </summary>
    public static class YouTubeCircuitBreaker
    {
        /// <summary>
        /// How long to stop attempting YouTube downloads after a soft-block. YouTube announces
        /// "rate-limited for up to an hour", but every request made meanwhile restarts that hour,
        /// so the blackhole script settled on 6h to guarantee the penalty actually lapses.
        /// </summary>
        public static TimeSpan Cooldown { get; set; } = TimeSpan.FromHours(6);

        private static readonly object _lock = new();
        private static DateTime _blockedUntilUtc = DateTime.MinValue;
        private static bool _loaded;

        /// <summary>Survives plugin upgrades and Lidarr restarts, next to the other tubifarry state.</summary>
        private static readonly string _stateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Lidarr", "tubifarry-state", "youtube-block.json");

        private sealed record BlockState(DateTime BlockedUntilUtc);

        // Lower-case stderr fragments that mean "the IP/session is blocked" - deliberately narrow
        // so a single unavailable / age-gated video does not trip the breaker.
        private static readonly string[] _markers =
        {
            "not a bot",
            "captcha",
            "rate-limited",
            "http error 429",
            "too many requests",
        };

        /// <summary>True when a yt-dlp stderr line signals an IP/session-wide YouTube block.</summary>
        public static bool IsSoftBlockMarker(string? stderrLine)
        {
            if (string.IsNullOrEmpty(stderrLine))
                return false;
            string s = stderrLine.ToLowerInvariant();
            foreach (string m in _markers)
                if (s.Contains(m, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>True while the cool-down is active (skip YouTube downloads).</summary>
        public static bool IsOpen
        {
            get { lock (_lock) { EnsureLoaded(); return DateTime.UtcNow < _blockedUntilUtc; } }
        }

        /// <summary>Time left on the current cool-down (zero when closed).</summary>
        public static TimeSpan Remaining
        {
            get
            {
                lock (_lock)
                {
                    EnsureLoaded();
                    TimeSpan r = _blockedUntilUtc - DateTime.UtcNow;
                    return r > TimeSpan.Zero ? r : TimeSpan.Zero;
                }
            }
        }

        /// <summary>Open the breaker for <see cref="Cooldown"/> (extends an active cool-down, never shortens it).</summary>
        public static void Trip(Logger? logger = null)
        {
            DateTime until;
            lock (_lock)
            {
                EnsureLoaded();
                DateTime candidate = DateTime.UtcNow + Cooldown;
                if (candidate > _blockedUntilUtc)
                {
                    _blockedUntilUtc = candidate;
                    Save();
                }
                until = _blockedUntilUtc;
            }
            logger?.Warn($"YouTube soft-block detected - pausing YouTube downloads for ~{Cooldown.TotalHours:0.#}h " +
                         $"(until {until:yyyy-MM-dd HH:mm} UTC). yt-dlp will not be called for YouTube until then.");
        }

        /// <summary>Close the breaker after a clean download.</summary>
        public static void Reset()
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (_blockedUntilUtc == DateTime.MinValue)
                    return;
                _blockedUntilUtc = DateTime.MinValue;
                Save();
            }
        }

        /// <summary>Reads the persisted cool-down once per process. Caller must hold <see cref="_lock"/>.</summary>
        private static void EnsureLoaded()
        {
            if (_loaded)
                return;
            _loaded = true;
            try
            {
                if (!File.Exists(_stateFile))
                    return;
                BlockState? state = JsonSerializer.Deserialize<BlockState>(File.ReadAllText(_stateFile));
                if (state != null)
                    _blockedUntilUtc = DateTime.SpecifyKind(state.BlockedUntilUtc, DateTimeKind.Utc);
            }
            catch
            {
                // An unreadable or corrupt state file must never stop downloads: fail closed-open
                // (breaker closed) and let the next soft-block rewrite it.
            }
        }

        /// <summary>Persists the cool-down. Caller must hold <see cref="_lock"/>.</summary>
        private static void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_stateFile);
                if (dir != null)
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_stateFile, JsonSerializer.Serialize(new BlockState(_blockedUntilUtc)));
            }
            catch
            {
                // Persistence is best-effort; the in-memory breaker still protects this process.
            }
        }
    }
}
