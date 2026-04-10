using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NKS.WebDevConsole.Core.Services;

/// <summary>
/// Windows Job Object wrapper that guarantees every child process spawned by the
/// daemon (httpd, mysqld, php-cgi, redis-server, mailpit, caddy, …) is killed
/// when the daemon process itself exits — even on crash or force-kill.
///
/// Plan item "Phase 2 — Windows Job Objects for child process cleanup" — without
/// this, an abnormal daemon exit leaves orphaned service processes holding ports
/// and the next daemon start fails with "port already in use" errors.
///
/// No-op on non-Windows platforms. Plugins call <see cref="AssignProcess"/> right
/// after they start a child process.
/// </summary>
public static class DaemonJobObject
{
    private static readonly object _lock = new();
    private static IntPtr _jobHandle = IntPtr.Zero;
    private static bool _initialized;

    /// <summary>
    /// Lazily creates the singleton Job Object with KILL_ON_JOB_CLOSE and returns
    /// its handle. Called automatically by <see cref="AssignProcess"/>. Safe to
    /// call from anywhere; initializes only once.
    /// </summary>
    public static IntPtr EnsureInitialized()
    {
        if (_initialized) return _jobHandle;
        lock (_lock)
        {
            if (_initialized) return _jobHandle;

            if (!OperatingSystem.IsWindows())
            {
                _initialized = true;
                return IntPtr.Zero;
            }

            try
            {
                var handle = CreateJobObject(IntPtr.Zero, "NKS.WebDevConsole.Daemon.JobObject");
                if (handle == IntPtr.Zero)
                {
                    _initialized = true;
                    return IntPtr.Zero;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    }
                };

                int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, extendedInfoPtr, false);
                    if (!SetInformationJobObject(handle, JobObjectInfoType.ExtendedLimitInformation,
                        extendedInfoPtr, (uint)length))
                    {
                        CloseHandle(handle);
                        handle = IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(extendedInfoPtr);
                }

                _jobHandle = handle;
            }
            catch
            {
                _jobHandle = IntPtr.Zero;
            }
            _initialized = true;
            return _jobHandle;
        }
    }

    /// <summary>
    /// Assigns the given process to the daemon Job Object. Returns true on
    /// success, false on non-Windows or when the process is already dead.
    /// Swallows exceptions — failure to assign is logged by the caller but
    /// must not prevent the child from starting.
    /// </summary>
    public static bool AssignProcess(Process process)
    {
        if (process is null) return false;
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            if (process.HasExited) return false;
            var job = EnsureInitialized();
            if (job == IntPtr.Zero) return false;
            return AssignProcessToJobObject(job, process.Handle);
        }
        catch
        {
            return false;
        }
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
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
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JobObjectInfoType infoType,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
