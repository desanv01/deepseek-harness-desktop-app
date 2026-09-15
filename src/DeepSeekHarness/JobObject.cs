using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DShNative;

/**
 * A Windows job object that terminates every member process when its last
 * handle closes. The app keeps the handle for its whole lifetime, so the dsh
 * child and its descendants cannot outlive the app - including on a crash or a
 * Task Manager kill, where no managed cleanup code runs.
 */
public sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;
    private bool _disposed;

    private JobObject(IntPtr handle)
    {
        _handle = handle;
    }

    public bool IsValid => _handle != IntPtr.Zero;

    /**
     * Creates a job for the managed server. killOnClose is the shutdown
     * guarantee: the OS kills the server when this process dies. Keep-alive mode
     * turns it off on purpose, so the harness survives the window - then the
     * lease, the next launch's adoption, and `--stop` are what clean up.
     */
    public static JobObject? Create(bool killOnClose = true)
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            Log.Warn("CreateJobObject failed: " + Marshal.GetLastWin32Error());
            return null;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = killOnClose ? JobObjectLimitKillOnJobClose : 0,
            },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                Log.Warn("SetInformationJobObject failed: " + Marshal.GetLastWin32Error());
                CloseHandle(handle);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return new JobObject(handle);
    }

    /** Adds a running child to this job; false when the OS refused. */
    public bool Assign(Process process)
    {
        if (_handle == IntPtr.Zero || process == null) return false;
        if (AssignProcessToJobObject(_handle, process.Handle)) return true;
        Log.Warn("AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error());
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero) CloseHandle(handle);
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
