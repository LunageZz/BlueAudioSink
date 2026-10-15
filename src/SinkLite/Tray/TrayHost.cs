using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SinkLite.Core;
using SinkLite.Platform;

namespace SinkLite.Tray
{
    /// <summary>
    /// 托盘宿主。刻意不使用 WinForms（实测会把体积从 10 MB 拉到 51 MB），
    /// 改为 P/Invoke Shell_NotifyIcon + 隐藏消息窗口。
    /// </summary>
    internal sealed class TrayHost : IDisposable
    {
        private const int WM_APP = 0x8000;
        private const int WM_TRAY = WM_APP + 1;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_COMMAND = 0x0111;
        private const int WM_DESTROY = 0x0002;
        private const int MF_STRING = 0x0000;
        private const int TPM_RETURNCMD = 0x0100;
        private const int TPM_RIGHTBUTTON = 0x0002;
        private const int NIM_ADD = 0x0;
        private const int NIM_MODIFY = 0x1;
        private const int NIM_DELETE = 0x2;
        private const int NIF_MESSAGE = 0x1;
        private const int NIF_ICON = 0x2;
        private const int NIF_TIP = 0x4;

        private const int CmdConnect = 1001;
        private const int CmdDisconnect = 1002;
        private const int CmdFixRoute = 1003;
        private const int CmdOpenLogs = 1004;
        private const int CmdAutoStart = 1005;
        private const int CmdExit = 1006;
        private const int CmdDiagnose = 1007;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public int ptX;
            public int ptY;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX wndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG msg, IntPtr hWnd, int min, int max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr menu, int flags, IntPtr id, string text);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenu(
            IntPtr menu, int flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void DestroyIcon(IntPtr icon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, int type);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconFromResourceEx(
            byte[] resourceBits, uint resourceSize, bool isIcon, uint version,
            int desiredWidth, int desiredHeight, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

        private readonly SinkController controller;
        private readonly Platform.RouteControl route;
        private readonly Platform.EndpointWatcher watcher;
        private readonly Platform.Log log;

        private IntPtr hwnd;
        private NOTIFYICONDATA iconData;
        private WndProcDelegate wndProc;
        private string lastDetail = string.Empty;

        public TrayHost(SinkController controller, Platform.RouteControl route, Platform.EndpointWatcher watcher, Platform.Log log)
        {
            this.controller = controller;
            this.route = route;
            this.watcher = watcher;
            this.log = log;
        }

        public void Run()
        {
            wndProc = WindowProc;
            IntPtr instance = GetModuleHandle(null);

            WNDCLASSEX wc = new WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc);
            wc.hInstance = instance;
            wc.lpszClassName = "BlueAudioSinkHiddenWindow";
            RegisterClassEx(ref wc);

            hwnd = CreateWindowEx(
                0, "BlueAudioSinkHiddenWindow", "BlueAudioSink", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            iconData = new NOTIFYICONDATA();
            iconData.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            iconData.hWnd = hwnd;
            iconData.uID = 1;
            iconData.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            iconData.uCallbackMessage = WM_TRAY;
            iconData.hIcon = LoadStateIcon(SinkState.Idle);
            iconData.szTip = "BlueAudioSink：未连接";
            Shell_NotifyIcon(NIM_ADD, ref iconData);

            controller.SnapshotChanged += OnSnapshotChanged;
            controller.RequestRefreshDevices();

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            Shell_NotifyIcon(NIM_DELETE, ref iconData);
        }

        private void OnSnapshotChanged(object sender, SinkSnapshot snapshot)
        {
            string tip = BuildTip(snapshot);
            if (tip == lastDetail)
            {
                return;
            }
            lastDetail = tip;

            IntPtr old = iconData.hIcon;
            iconData.hIcon = LoadStateIcon(snapshot.State);
            iconData.szTip = tip.Length > 127 ? tip.Substring(0, 127) : tip;
            iconData.uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE;
            Shell_NotifyIcon(NIM_MODIFY, ref iconData);
            if (old != IntPtr.Zero)
            {
                DestroyIcon(old);
            }

            log.Info("Tray.Updated", ("state", snapshot.State.ToString()), ("detail", snapshot.Detail));
        }

        private static string BuildTip(SinkSnapshot snapshot)
        {
            string device = string.IsNullOrEmpty(snapshot.DeviceName) ? "未选择设备" : snapshot.DeviceName;
            return "BlueAudioSink：" + snapshot.Detail + "（" + device + "）";
        }

        private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TRAY)
            {
                int evt = lParam.ToInt32() & 0xFFFF;
                if (evt == WM_RBUTTONUP)
                {
                    ShowMenu();
                    return IntPtr.Zero;
                }

                if (evt == WM_LBUTTONDBLCLK)
                {
                    SinkSnapshot snapshot = controller.Current;
                    string text = snapshot.Detail + Environment.NewLine + Environment.NewLine +
                        (string.IsNullOrEmpty(snapshot.Hint) ? "设备：" + snapshot.DeviceName : snapshot.Hint);
                    MessageBox(hWnd, text, "BlueAudioSink", 0x40);
                    return IntPtr.Zero;
                }
            }

            if (msg == WM_COMMAND)
            {
                int id = wParam.ToInt32() & 0xFFFF;
                HandleMenuCommand(id);
                return IntPtr.Zero;
            }

            if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void ShowMenu()
        {
            SinkSnapshot snapshot = controller.Current;
            bool autoStart = Platform.AutoStart.IsEnabled();

            IntPtr menu = CreatePopupMenu();
            AppendMenu(menu, MF_STRING, new IntPtr(CmdConnect), "连接 / 重连");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdDisconnect), "断开");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdFixRoute), "切换输出到物理设备");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdDiagnose), "诊断（输出归因到日志）");
            AppendMenu(menu, MF_STRING, IntPtr.Zero, "—");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdOpenLogs), "打开日志目录");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdAutoStart), autoStart ? "开机自启：已开启" : "开机自启：已关闭");
            AppendMenu(menu, MF_STRING, IntPtr.Zero, "—");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdExit), "退出");

            POINT point;
            GetCursorPos(out point);
            SetForegroundWindow(hwnd);
            int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, 0, hwnd, IntPtr.Zero);
            DestroyMenu(menu);

            if (cmd != 0)
            {
                HandleMenuCommand(cmd);
            }
        }

        private void HandleMenuCommand(int id)
        {
            switch (id)
            {
                case CmdConnect:
                    controller.RequestRefreshDevices();
                    System.Threading.Thread.Sleep(200);
                    controller.RequestConnect(null);
                    break;

                case CmdDisconnect:
                    controller.RequestDisconnect();
                    break;

                case CmdFixRoute:
                    FixRoute();
                    break;

                case CmdDiagnose:
                    controller.RequestRefreshDevices();
                    break;

                case CmdOpenLogs:
                    OpenLogDirectory();
                    break;

                case CmdAutoStart:
                    Platform.AutoStart.SetEnabled(!Platform.AutoStart.IsEnabled());
                    break;

                case CmdExit:
                    controller.RequestShutdown();
                    Shell_NotifyIcon(NIM_DELETE, ref iconData);
                    PostQuitMessage(0);
                    break;
            }
        }

        private void FixRoute()
        {
            EndpointInfo target = route.PickPhysicalRender();
            if (target == null)
            {
                MessageBox(hwnd, "没有找到可用的物理输出设备。", "BlueAudioSink", 0x40);
                return;
            }

            if (route.SetDefaultRender(target.Id, out string error))
            {
                log.Info("Route.Switched", ("endpoint", target.Name));
                MessageBox(hwnd, "已把默认输出切换到：" + target.Name, "BlueAudioSink", 0x40);
            }
            else
            {
                log.Warn("Route.SwitchFailed", ("endpoint", target.Name), ("error", error));
                MessageBox(hwnd,
                    "切换默认输出失败：" + error + Environment.NewLine + Environment.NewLine +
                    "请手动在「设置 → 系统 → 声音」中选择正确输出。",
                    "BlueAudioSink", 0x30);
            }
        }

        private void OpenLogDirectory()
        {
            try
            {
                Directory.CreateDirectory(Platform.Log.DefaultDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = Platform.Log.DefaultDirectory,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        /// <summary>按状态加载内嵌图标，避免任何 GDI+/System.Drawing 依赖。</summary>
        private static IntPtr LoadStateIcon(SinkState state)
        {
            string name;
            switch (state)
            {
                case SinkState.Flowing: name = "green"; break;
                case SinkState.LinkReady: name = "blue"; break;
                case SinkState.Armed: name = "yellow"; break;
                case SinkState.Busy:
                case SinkState.Backoff: name = "yellow"; break;
                case SinkState.Faulted: name = "red"; break;
                default: name = "gray"; break;
            }

            try
            {
                using (Stream stream = typeof(TrayHost).Assembly
                    .GetManifestResourceStream("dot." + name + ".ico"))
                {
                    if (stream == null)
                    {
                        return LoadIcon(IntPtr.Zero, new IntPtr(32516));
                    }

                    byte[] data = new byte[stream.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = stream.Read(data, read, data.Length - read);
                        if (n <= 0)
                        {
                            break;
                        }
                        read += n;
                    }

                    IntPtr icon = CreateIconFromResourceEx(
                        data, (uint)read, true, 0x00030000u, 32, 32, 0u);
                    return icon != IntPtr.Zero ? icon : LoadIcon(IntPtr.Zero, new IntPtr(32516));
                }
            }
            catch
            {
                return LoadIcon(IntPtr.Zero, new IntPtr(32516));
            }
        }

        public void Dispose()
        {
            try
            {
                controller.SnapshotChanged -= OnSnapshotChanged;
                if (iconData.hIcon != IntPtr.Zero)
                {
                    DestroyIcon(iconData.hIcon);
                    iconData.hIcon = IntPtr.Zero;
                }
            }
            catch
            {
            }
        }
    }
}
