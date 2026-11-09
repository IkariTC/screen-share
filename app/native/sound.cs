// Screen Room's sound helper. Uses Windows' per-app audio capture (process loopback, Windows 10 build
// 20348+ and Windows 11) to capture one window's app, or everything except Screen Room itself.
// It also lists minimised windows, which the screen picker can't otherwise see, and restores them.
//
//   screen-room-sound probe              prints "ok" if per-app capture works on this PC
//   screen-room-sound windows            prints the minimised windows as JSON
//   screen-room-sound restore <hwnd>     un-minimises a window (leaves others alone)
//   screen-room-sound window <hwnd>      streams the sound of that window's app
//   screen-room-sound except <pid>       streams all sound except that process and its children
//   screen-room-sound except-discord <pid>   streams all sound except Discord; while Discord is closed, except that process
//   screen-room-sound discord            prints the process id of Discord's main process, or 0
//
// Sound goes to stdout as raw 48 kHz stereo 32-bit float; "ready" goes to stderr once it's flowing.
// The except commands also say "leaving out room" or "leaving out discord" on stderr, and again if that changes.
// Closing stdin stops it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Program
{
    const int Rate = 48000, Channels = 2, FrameBytes = Channels * 4;

    [MTAThread]
    static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "";
        try
        {
            if (cmd == "probe")
            {
                Initialize(Activate((uint)Process.GetCurrentProcess().Id, true));
                Write("ok");
                return 0;
            }
            if (cmd == "windows") { Write(MinimisedWindowsJson()); return 0; }
            if (cmd == "discord") { Write(FindDiscord(0).ToString()); return 0; }
            if (cmd == "restore" && args.Length > 1)
            {
                IntPtr hwnd = Hwnd(args[1]);
                if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9);   // SW_RESTORE
                return 0;
            }
            if (cmd == "window" && args.Length > 1) return Capture(ProcessForWindow(Hwnd(args[1])), false, null);
            if (cmd == "except" && args.Length > 1)
            {
                room = uint.Parse(args[1]);
                return Capture(room, true, null);
            }
            if (cmd == "except-discord" && args.Length > 1)
            {
                room = uint.Parse(args[1]);
                uint discord = 0;
                Func<uint> target = () => { discord = FindDiscord(discord); return discord != 0 ? discord : room; };
                return Capture(target(), true, target);
            }
            Console.Error.WriteLine("usage: screen-room-sound probe | windows | discord | restore <hwnd> | window <hwnd> | except <pid> | except-discord <pid>");
            return 64;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    static IntPtr Hwnd(string s) { return new IntPtr(long.Parse(s)); }

    static void Write(string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        Stream o = Console.OpenStandardOutput();
        o.Write(bytes, 0, bytes.Length);
        o.Flush();
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new Exception(what + " failed (0x" + hr.ToString("X8") + ")");
    }

    /* ---------- Capturing ---------- */

    const int LookEveryMs = 2000;   // how often except-discord checks whether Discord has opened or closed

    static uint room;               // Screen Room's own process, on the except commands

    class Source
    {
        public IAudioClient Client;
        public IAudioCaptureClient Reader;
        public AutoResetEvent PacketReady;

        public void Close()
        {
            Client.Stop();
            Marshal.ReleaseComObject(Reader);
            Marshal.ReleaseComObject(Client);
            PacketReady.Dispose();
        }
    }

    static void Say(string line)
    {
        Console.Error.WriteLine(line);
        Console.Error.Flush();
    }

    static Source Open(uint pid, bool exclude)
    {
        IAudioClient client = Activate(pid, exclude);
        Initialize(client);
        var packetReady = new AutoResetEvent(false);
        Check(client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
        Guid iid = typeof(IAudioCaptureClient).GUID;
        object service;
        Check(client.GetService(ref iid, out service), "GetService");
        Check(client.Start(), "Start");
        return new Source { Client = client, Reader = (IAudioCaptureClient)service, PacketReady = packetReady };
    }

    // Starts capturing and tells Screen Room what's left out, so it can mute friends' streams in time:
    // before the room's own sound can be picked up, and only once it's safely out again.
    static Source Aim(uint pid, bool exclude)
    {
        if (room != 0 && pid != room) Say("leaving out discord");
        Source source = Open(pid, exclude);
        if (room != 0 && pid == room) Say("leaving out room");
        return source;
    }

    // target, if given, is asked now and then what should be left out by now
    static int Capture(uint pid, bool exclude, Func<uint> target)
    {
        Source source = Aim(pid, exclude);

        // Screen Room closing our stdin means it has stopped sharing (or quit)
        var watch = new Thread(() =>
        {
            try { Console.OpenStandardInput().CopyTo(Stream.Null); } catch { }
            Environment.Exit(0);
        });
        watch.IsBackground = true;
        watch.Start();

        Stream stdout = Console.OpenStandardOutput();
        byte[] buf = new byte[Rate * FrameBytes];
        Say("ready");

        int lastLook = Environment.TickCount;
        while (true)
        {
            source.PacketReady.WaitOne(100);
            if (target != null && Environment.TickCount - lastLook >= LookEveryMs)
            {
                lastLook = Environment.TickCount;
                uint next = target();
                if (next != pid)
                {
                    try
                    {
                        Source fresh = Aim(next, exclude);
                        source.Close();
                        source = fresh;
                        pid = next;
                    }
                    catch (Exception e)
                    {
                        // Carry on as we were and try again at the next look
                        Say("error: " + e.Message);
                        if (pid == room) Say("leaving out room");
                    }
                }
            }
            while (true)
            {
                uint frames;
                Check(source.Reader.GetNextPacketSize(out frames), "GetNextPacketSize");
                if (frames == 0) break;
                IntPtr data;
                uint flags;
                ulong position, qpc;
                Check(source.Reader.GetBuffer(out data, out frames, out flags, out position, out qpc), "GetBuffer");
                int bytes = (int)frames * FrameBytes;
                if (bytes > buf.Length) buf = new byte[bytes];
                if ((flags & 2) != 0) Array.Clear(buf, 0, bytes);      // AUDCLNT_BUFFERFLAGS_SILENT
                else Marshal.Copy(data, buf, 0, bytes);
                source.Reader.ReleaseBuffer(frames);
                try { stdout.Write(buf, 0, bytes); stdout.Flush(); }
                catch (IOException) { return 0; }                       // Screen Room has gone
            }
        }
    }

    static IAudioClient Activate(uint pid, bool exclude)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, { pid, include/exclude tree } }
        IntPtr parms = Marshal.AllocHGlobal(12);
        Marshal.WriteInt32(parms, 0, 1);
        Marshal.WriteInt32(parms, 4, (int)pid);
        Marshal.WriteInt32(parms, 8, exclude ? 1 : 0);
        // ...wrapped in a PROPVARIANT of type VT_BLOB
        IntPtr variant = Marshal.AllocHGlobal(24);
        for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(variant, i, 0);
        Marshal.WriteInt16(variant, 0, 65);
        Marshal.WriteInt32(variant, 8, 12);
        Marshal.WriteIntPtr(variant, 16, parms);

        var done = new Activated();
        Guid iid = typeof(IAudioClient).GUID;
        IActivateAudioInterfaceAsyncOperation op;
        Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variant, done, out op), "Starting per-app capture");
        if (!done.Finished.WaitOne(5000)) throw new Exception("per-app capture didn't start in time");
        Marshal.FinalReleaseComObject(op);      // it keeps the capture alive, and the same app then can't be captured again
        Check(done.Result, "Per-app capture");
        return (IAudioClient)done.Client;
    }

    static void Initialize(IAudioClient client)
    {
        // WAVEFORMATEX: IEEE float, stereo, 48 kHz
        IntPtr format = Marshal.AllocHGlobal(18);
        Marshal.WriteInt16(format, 0, 3);
        Marshal.WriteInt16(format, 2, Channels);
        Marshal.WriteInt32(format, 4, Rate);
        Marshal.WriteInt32(format, 8, Rate * FrameBytes);
        Marshal.WriteInt16(format, 12, FrameBytes);
        Marshal.WriteInt16(format, 14, 32);
        Marshal.WriteInt16(format, 16, 0);
        const uint Loopback = 0x20000, EventCallback = 0x40000, AutoConvert = 0x80000000, GoodResampling = 0x08000000;
        Check(client.Initialize(0, Loopback | EventCallback | AutoConvert | GoodResampling, 200000, 0, format, IntPtr.Zero), "Initialize");
    }

    class Activated : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Finished = new ManualResetEvent(false);
        public int Result;
        public object Client;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            int hr;
            object client;
            int got = op.GetActivateResult(out hr, out client);
            Result = got < 0 ? got : hr;
            Client = client;
            Finished.Set();
            return 0;
        }
    }

    /* ---------- Finding Discord ---------- */

    static bool IsDiscord(string exe)
    {
        return exe.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("DiscordPTB.exe", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("DiscordCanary.exe", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("DiscordDevelopment.exe", StringComparison.OrdinalIgnoreCase);
    }

    // Discord's main process, whose tree holds all its sound: a Discord that wasn't started by another Discord.
    // Only one can be left out, so keep the one we have while it's running; otherwise take the one with the
    // most children, which is the real client rather than a launcher or updater. 0 if Discord isn't open.
    static uint FindDiscord(uint keep)
    {
        var exes = new Dictionary<uint, string>();
        var parents = new Dictionary<uint, uint>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);   // TH32CS_SNAPPROCESS
        if (snapshot == new IntPtr(-1)) return keep;
        try
        {
            var entry = new ProcessEntry { Size = Marshal.SizeOf(typeof(ProcessEntry)) };
            for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            {
                exes[entry.Id] = entry.ExeFile;
                parents[entry.Id] = entry.ParentId;
            }
        }
        finally { CloseHandle(snapshot); }

        var mains = new Dictionary<uint, int>();            // Discord's main processes, and how many children each has
        foreach (var process in exes)
        {
            string parentExe;
            if (IsDiscord(process.Value) && !(exes.TryGetValue(parents[process.Key], out parentExe) && IsDiscord(parentExe)))
                mains[process.Key] = 0;
        }
        foreach (var process in exes)
        {
            if (IsDiscord(process.Value) && mains.ContainsKey(parents[process.Key])) mains[parents[process.Key]]++;
        }

        if (mains.ContainsKey(keep)) return keep;
        uint best = 0;
        int most = -1;
        foreach (var main in mains)
        {
            if (main.Value > most) { best = main.Key; most = main.Value; }
        }
        return best;
    }

    /* ---------- Windows ---------- */

    // The app whose sound belongs to a window. Store apps draw inside ApplicationFrameHost,
    // so for those it's the process behind the window's inner content.
    static uint ProcessForWindow(IntPtr hwnd)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0) throw new Exception("that window has closed");
        if (ExePath(pid).EndsWith("\\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint inner = 0;
            EnumChildWindows(hwnd, (child, l) =>
            {
                uint cp;
                GetWindowThreadProcessId(child, out cp);
                if (cp != pid) { inner = cp; return false; }
                return true;
            }, IntPtr.Zero);
            if (inner != 0) pid = inner;
        }
        return pid;
    }

    static string MinimisedWindowsJson()
    {
        var json = new StringBuilder("[");
        EnumWindows((hwnd, l) =>
        {
            if (!IsWindowVisible(hwnd) || !IsIconic(hwnd) || GetWindow(hwnd, 4) != IntPtr.Zero) return true;   // GW_OWNER
            if (((long)GetWindowLongPtr(hwnd, -20) & 0x80) != 0) return true;                                   // WS_EX_TOOLWINDOW
            int cloaked;
            if (DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) return true;             // hidden by the shell
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var title = new StringBuilder(len + 1);
            GetWindowText(hwnd, title, title.Capacity);
            if (json.Length > 1) json.Append(',');
            json.Append("{\"hwnd\":\"").Append(hwnd.ToInt64()).Append("\",\"title\":").Append(Quote(title.ToString()))
                .Append(",\"exe\":").Append(Quote(ExePath(ProcessForWindow(hwnd)))).Append('}');
            return true;
        }, IntPtr.Zero);
        return json.Append(']').ToString();
    }

    static string ExePath(uint pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return "";
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageName(h, 0, path, ref size) ? path.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    // JSON string, ASCII only so the console code page can't mangle titles
    static string Quote(string s)
    {
        var b = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') b.Append('\\').Append(c);
            else if (c < 0x20 || c > 0x7e) b.Append("\\u").Append(((int)c).ToString("x4"));
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }

    /* ---------- Windows APIs ---------- */

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    // PROCESSENTRY32W
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public int Size;
        public uint Usage, Id;
        public IntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
}

// Marks the completion handler as callable from any thread, which activation requires
[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAgileObject { }
