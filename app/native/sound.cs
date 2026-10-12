// Screen Room's sound helper. Uses Windows' per-app audio capture (process loopback, Windows 10 build
// 20348+ and Windows 11) to capture one window's app, or everything except Screen Room itself.
// It also lists minimised windows, which the screen picker can't otherwise see, and restores them.
//
//   screen-room-sound probe              prints "ok" if per-app capture works on this PC
//   screen-room-sound windows            prints the minimised windows as JSON
//   screen-room-sound restore <hwnd>     un-minimises a window (leaves others alone)
//   screen-room-sound window <hwnd>      streams the sound of that window's app
//   screen-room-sound except <pid>       streams all sound except that process and its children
//
// Sound goes to stdout as raw 48 kHz stereo 32-bit float; "ready" goes to stderr once it's flowing.
// Closing stdin stops it.
using System;
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
            if (cmd == "restore" && args.Length > 1)
            {
                IntPtr hwnd = Hwnd(args[1]);
                if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9);   // SW_RESTORE
                return 0;
            }
            if (cmd == "window" && args.Length > 1) return Capture(ProcessForWindow(Hwnd(args[1])), false);
            if (cmd == "except" && args.Length > 1) return Capture(uint.Parse(args[1]), true);
            Console.Error.WriteLine("usage: screen-room-sound probe | windows | restore <hwnd> | window <hwnd> | except <pid>");
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

    static int Capture(uint pid, bool exclude)
    {
        IAudioClient client = Activate(pid, exclude);
        Initialize(client);
        var packetReady = new AutoResetEvent(false);
        Check(client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
        Guid iid = typeof(IAudioCaptureClient).GUID;
        object service;
        Check(client.GetService(ref iid, out service), "GetService");
        var capture = (IAudioCaptureClient)service;

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
        Check(client.Start(), "Start");
        Console.Error.WriteLine("ready");
        Console.Error.Flush();

        while (true)
        {
            packetReady.WaitOne(100);
            while (true)
            {
                uint frames;
                Check(capture.GetNextPacketSize(out frames), "GetNextPacketSize");
                if (frames == 0) break;
                IntPtr data;
                uint flags;
                ulong position, qpc;
                Check(capture.GetBuffer(out data, out frames, out flags, out position, out qpc), "GetBuffer");
                int bytes = (int)frames * FrameBytes;
                if (bytes > buf.Length) buf = new byte[bytes];
                if ((flags & 2) != 0) Array.Clear(buf, 0, bytes);      // AUDCLNT_BUFFERFLAGS_SILENT
                else Marshal.Copy(data, buf, 0, bytes);
                capture.ReleaseBuffer(frames);
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
