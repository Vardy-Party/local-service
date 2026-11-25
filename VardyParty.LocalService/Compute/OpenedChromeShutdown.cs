using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VardyParty.LocalService.Compute;

/// <summary>
/// System Chrome started for <c>/mp</c> is a child of this process. Disposing
/// <c>SystemChromeProcess</c> kills that tree, but an in-flight harvest skips
/// dispose when the host exits. Close every Chrome still parented here.
/// Never kill a browser that is not a descendant of this process.
/// </summary>
internal static class OpenedChromeShutdown
{
    private static int _ran;

    internal readonly record struct ProcessRow(int Pid, int ParentPid, string Name);

    public static void CloseAll()
    {
        if (Interlocked.Exchange(ref _ran, 1) != 0)
        {
            return;
        }

        IReadOnlyList<ProcessRow> snapshot;
        try
        {
            snapshot = SnapshotProcesses();
        }
        catch
        {
            return;
        }

        foreach (var pid in ChromeRootsLaunchedBy(Environment.ProcessId, snapshot))
        {
            TryKillTree(pid);
        }
    }

    /// <summary>
    /// Chrome processes in <paramref name="rootPid"/>'s descendant tree whose
    /// parent is not itself Chrome (one kill per launched browser).
    /// </summary>
    internal static List<int> ChromeRootsLaunchedBy(int rootPid, IReadOnlyList<ProcessRow> rows)
    {
        var byParent = rows.ToLookup(r => r.ParentPid);
        var nameByPid = new Dictionary<int, string>();
        foreach (var row in rows)
        {
            nameByPid.TryAdd(row.Pid, row.Name);
        }
        var roots = new List<int>();
        var stack = new Stack<int>();
        var seen = new HashSet<int> { rootPid };
        stack.Push(rootPid);
        while (stack.Count > 0)
        {
            var pid = stack.Pop();
            foreach (var child in byParent[pid])
            {
                if (!seen.Add(child.Pid))
                {
                    continue;
                }

                stack.Push(child.Pid);
                if (IsChromeName(child.Name) && !IsChromeName(ParentName(child.ParentPid, nameByPid)))
                {
                    roots.Add(child.Pid);
                }
            }
        }

        return roots;
    }

    internal static bool IsChromeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var file = Path.GetFileNameWithoutExtension(name.Trim());
        return file.Equals("chrome", StringComparison.OrdinalIgnoreCase)
            || file.Equals("chromium", StringComparison.OrdinalIgnoreCase)
            || file.Equals("chromium-browse", StringComparison.OrdinalIgnoreCase)
            || file.Equals("chromium-browser", StringComparison.OrdinalIgnoreCase)
            || file.Equals("google-chrome", StringComparison.OrdinalIgnoreCase)
            || file.Equals("Google Chrome", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("chromium-browse", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("google-chrome", StringComparison.OrdinalIgnoreCase);
    }

    private static string ParentName(int parentPid, IReadOnlyDictionary<int, string> names) =>
        names.TryGetValue(parentPid, out var name) ? name : "";

    private static void TryKillTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
            // already gone
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        catch (NotSupportedException)
        {
            // platform cannot kill a tree
        }
    }

    internal static IReadOnlyList<ProcessRow> SnapshotProcesses()
    {
        if (OperatingSystem.IsWindows())
        {
            return SnapshotWindows();
        }

        if (Directory.Exists("/proc"))
        {
            return SnapshotProc();
        }

        return [];
    }

    [SupportedOSPlatform("windows")]
    private static List<ProcessRow> SnapshotWindows()
    {
        var rows = new List<ProcessRow>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1))
        {
            return rows;
        }

        try
        {
            var entry = new ProcessEntry32 { DwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return rows;
            }

            do
            {
                rows.Add(new ProcessRow((int)entry.Th32ProcessID, (int)entry.Th32ParentProcessID, entry.SzExeFile));
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return rows;
    }

    private static List<ProcessRow> SnapshotProc()
    {
        var rows = new List<ProcessRow>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid))
            {
                continue;
            }

            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                if (TryParseProcStat(pid, stat, out var row))
                {
                    rows.Add(row);
                }
            }
            catch (IOException)
            {
                // process vanished
            }
            catch (UnauthorizedAccessException)
            {
                // other users
            }
        }

        return rows;
    }

    /// <summary>
    /// <c>/proc/pid/stat</c> comm is <c>(...)</c> and may contain <c>)</c>; the
    /// last closing paren ends the name. Linux comm is 15 chars, so
    /// <c>chromium-browser</c> is stored as <c>chromium-browse</c>.
    /// </summary>
    internal static bool TryParseProcStat(int pid, string stat, out ProcessRow row)
    {
        row = default;
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open < 0 || close <= open + 1 || close + 2 >= stat.Length)
        {
            return false;
        }

        var rest = stat.Substring(close + 2).Split((char[])[' '], StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 2 || !int.TryParse(rest[1], out var ppid))
        {
            return false;
        }

        var comm = stat.Substring(open + 1, close - open - 1);
        row = new ProcessRow(pid, ppid, comm);
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint DwSize;
        public uint CntUsage;
        public uint Th32ProcessID;
        public IntPtr Th32DefaultHeapID;
        public uint Th32ModuleID;
        public uint CntThreads;
        public uint Th32ParentProcessID;
        public int PcPriClassBase;
        public uint DwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzExeFile;
    }
}
