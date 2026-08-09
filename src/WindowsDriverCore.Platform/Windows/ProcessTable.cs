using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// Reads the live process table.
/// </summary>
/// <remarks>
/// Toolhelp32 rather than WMI or <c>Process.GetProcesses</c>: neither exposes
/// the parent process id, which is the whole reason this exists. A launcher stub
/// starts the real application as a child and exits, so the parent link is the
/// only way back to the application.
///
/// <c>DllImport</c> rather than <c>LibraryImport</c>, unlike the rest of the
/// interop here: the snapshot entry carries a fixed-size inline string, which
/// the source generator will not marshal (SYSLIB1051).
/// </remarks>
internal static class ProcessTable
{
    private const uint SnapProcess = 0x00000002;
    private const int MaxPath = 260;

    /// <summary>Maps each process id to its parent's.</summary>
    /// <returns>Process id to parent process id.</returns>
    internal static Dictionary<int, int> ParentsByProcessId()
    {
        Dictionary<int, int> parents = [];

        nint snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == 0 || snapshot == -1)
        {
            return parents;
        }

        try
        {
            ProcessEntry32 entry = new()
            {
                // Toolhelp32 rejects the call outright unless this is set to the
                // struct's own size first.
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
            };

            if (!Process32FirstW(snapshot, ref entry))
            {
                return parents;
            }

            do
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return parents;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>
    /// One row of the process snapshot, laid out as <c>PROCESSENTRY32W</c>.
    /// </summary>
    /// <remarks>
    /// Every field is present even though only two are read. The layout is fixed
    /// by the Win32 header, and dropping an unused field would change the struct
    /// size — which Toolhelp32 validates against <see cref="Size"/> and rejects.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string ExeFile;
    }
}
