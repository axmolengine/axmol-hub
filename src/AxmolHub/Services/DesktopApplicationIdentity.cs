using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AxmolHub.Core;
using Microsoft.Win32.SafeHandles;

namespace AxmolHub;

/// <summary>Reads the owning executable from the OS, not from window captions or process display names.</summary>
internal static class DesktopApplicationIdentity
{
    internal static ComputerProcessIdentity? Resolve(int processId)
    {
        if (processId <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(processId);
            var started = process.StartTime.ToUniversalTime().Ticks;
            var path = ExecutablePath(processId);
            if (ComputerApplication.FromExecutable(path) is not { } application) return null;
            // A second Process instance avoids StartTime's cache. If the PID was reused while the path was read,
            // no identity is emitted; a missing identity can never fall back to trusting the PID or the caption.
            using var after = Process.GetProcessById(processId);
            return !after.HasExited && after.StartTime.ToUniversalTime().Ticks == started
                ? new ComputerProcessIdentity(application, started) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception
                                      or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ExecutablePath(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle.IsInvalid) return null;
            var path = new StringBuilder(32768);
            var length = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
        }
        if (OperatingSystem.IsMacOS())
        {
            var buffer = new byte[4096]; // PROC_PIDPATHINFO_MAXSIZE
            var length = proc_pidpath(processId, buffer, (uint)buffer.Length);
            if (length <= 0) return null;
            var end = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, end < 0 ? length : end);
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit,
        int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        StringBuilder path, ref int length);

    [DllImport("/usr/lib/libproc.dylib")]
    private static extern int proc_pidpath(int processId, [Out] byte[] buffer, uint size);
}
