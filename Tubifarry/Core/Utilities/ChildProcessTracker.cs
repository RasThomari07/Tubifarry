using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NzbDrone.Common.Instrumentation;

namespace Tubifarry.Core.Utilities
{
    /// <summary>
    /// Ties child processes we spawn (the self-hosted deno bgutil POT server and the yt-dlp
    /// backend, which itself spawns a deno signature solver) to the lifetime of this Lidarr
    /// process via a Windows Job Object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
    ///
    /// Windows does not kill child processes when the parent dies, so without this the deno
    /// bgutil server survives every Lidarr restart and the orphans pile up (one per run).
    /// Because the job handle lives for the whole process, the OS terminates every tracked
    /// child when Lidarr exits — including a crash or a force-kill, which a ProcessExit hook
    /// would miss. No-op on non-Windows or if the job cannot be created.
    /// </summary>
    public static class ChildProcessTracker
    {
        private static readonly IntPtr _job;
        private static readonly bool _available;

        static ChildProcessTracker()
        {
            if (!OperatingSystem.IsWindows())
            {
                _available = false;
                return;
            }
            try
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                if (_job == IntPtr.Zero)
                {
                    _available = false;
                    return;
                }

                JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr infoPtr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    _available = SetInformationJobObject(_job, JobObjectExtendedLimitInformation, infoPtr, (uint)length);
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                }
            }
            catch
            {
                _available = false;
            }
        }

        /// <summary>
        /// Assigns an already-started process to the kill-on-close job so it dies with Lidarr.
        /// Safe to call for any process; silently does nothing if tracking is unavailable or the
        /// process has already exited.
        /// </summary>
        [SupportedOSPlatformGuard("windows")]
        public static void Track(Process? process)
        {
            if (!_available || process == null)
                return;
            try
            {
                if (process.HasExited)
                    return;
                if (!AssignProcessToJobObject(_job, process.Handle))
                {
                    int err = Marshal.GetLastWin32Error();
                    NzbDroneLogger.GetLogger(typeof(ChildProcessTracker))
                        .Trace($"ChildProcessTracker: could not track PID {process.Id} (win32 {err})");
                }
            }
            catch
            {
                // Best effort: a failure here only means the child may outlive Lidarr, which is
                // exactly the pre-existing behaviour — never let it break a download.
            }
        }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;

        [SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
