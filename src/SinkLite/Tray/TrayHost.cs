using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SinkLite.Core;
using SinkLite.Platform;

namespace SinkLite.Tray
{
    /// <summary>
    /// 托盘宿主：Shell_NotifyIcon + 隐藏消息窗口。
    /// 所有 Win32 声明来自 Platform.Win32；消息循环由 UiHost 统一提供。
    /// </summary>
    internal sealed class TrayHost : IDisposable
    {
        public const int WM_TRAY = Win32.WM_APP + 1;

        private const int MF_STRING = 0x0000;
        private const int TPM_RETURNCMD = 0x0100;
        private const int TPM_RIGHTBUTTON = 0x0002;
        private const int NIM_ADD = 0x0;
        private const int NIM_MODIFY = 0x1;
        private const int NIM_DELETE = 0x2;
        private const int NIF_MESSAGE = 0x1;
        private const int NIF_ICON = 0x2;
        private const int NIF_TIP = 0x4;

        public const int CmdOpenWindow = 1000;
        public const int CmdConnect = 1001;
        public const int CmdDisconnect = 1002;
        public const int CmdAutoStart = 1003;
        public const int CmdOpenLogs = 1004;
        public const int CmdDiagnose = 1005;
        public const int CmdExit = 1006;
        public const int CmdOutputBase = 1100;

        private readonly SinkController controller;
        private readonly Platform.RouteControl route;
        private readonly Platform.Log log;

        private IntPtr hwnd;
        private Win32.NOTIFYICONDATA iconData;
        private Win32.WndProcDelegate wndProc;
        private string lastTip = string.Empty;

        public TrayHost(SinkController controller, Platform.RouteControl route, Platform.Log log)
        {
            this.controller = controller;
            this.route = route;
            this.log = log;
        }

        /// <summary>托盘隐藏窗口句柄。</summary>
        public IntPtr Handle
        {
            get { return hwnd; }
        }

        public SinkHost Ui { get; set; }

        public void Create()
        {
            wndProc = WindowProc;
            IntPtr instance = Win32.GetModuleHandle(null);

            Win32.WNDCLASSEX wc = new Win32.WNDCLASSEX();
            wc.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.WNDCLASSEX));
            wc.lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(wndProc);
            wc.hInstance = instance;
            wc.lpszClassName = "BlueAudioSinkTrayWindow";
            Win32.RegisterClassEx(ref wc);

            hwnd = Win32.CreateWindowEx(
                0, "BlueAudioSinkTrayWindow", "BlueAudioSink", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            iconData = new Win32.NOTIFYICONDATA();
            iconData.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.NOTIFYICONDATA));
            iconData.hWnd = hwnd;
            iconData.uID = 1;
            iconData.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            iconData.uCallbackMessage = WM_TRAY;
            iconData.hIcon = LoadStateIcon(SinkState.Idle);
            iconData.szTip = "BlueAudioSink：未连接";
            Win32.Shell_NotifyIcon(NIM_ADD, ref iconData);

            controller.SnapshotChanged += OnSnapshotChanged;
            log.Info("Tray.Created");
        }

        private void OnSnapshotChanged(object sender, SinkSnapshot snapshot)
        {
            string tip = BuildTip(snapshot);
            if (tip == lastTip)
            {
                return;
            }
            lastTip = tip;

            IntPtr old = iconData.hIcon;
            iconData.hIcon = LoadStateIcon(snapshot.State);
            iconData.szTip = tip.Length > 127 ? tip.Substring(0, 127) : tip;
            iconData.uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE;
            Win32.Shell_NotifyIcon(NIM_MODIFY, ref iconData);
            if (old != IntPtr.Zero)
            {
                Win32.DestroyIcon(old);
            }
        }

        private static string BuildTip(SinkSnapshot snapshot)
        {
            string device = string.IsNullOrEmpty(snapshot.DeviceName) ? "未选择设备" : snapshot.DeviceName;
            return "BlueAudioSink：" + snapshot.Detail + "（" + device + "）";
        }

        /// <summary>按状态加载内嵌图标，避免 GDI+/System.Drawing 依赖。</summary>
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
                using (Stream stream = typeof(TrayHost).Assembly.GetManifestResourceStream("dot." + name + ".ico"))
                {
                    if (stream == null)
                    {
                        return Win32.LoadIcon(IntPtr.Zero, new IntPtr(Win32.IDI_APPLICATION));
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

                    IntPtr icon = Win32.CreateIconFromResourceEx(data, (uint)read, true, 0x00030000u, 32, 32, 0u);
                    return icon != IntPtr.Zero ? icon : Win32.LoadIcon(IntPtr.Zero, new IntPtr(Win32.IDI_APPLICATION));
                }
            }
            catch
            {
                return Win32.LoadIcon(IntPtr.Zero, new IntPtr(Win32.IDI_APPLICATION));
            }
        }

        /// <summary>处理一条属于托盘隐藏窗口的消息。</summary>
        public void Dispatch(ref Win32.MSG m)
        {
            if (hwnd == IntPtr.Zero || m.hwnd != hwnd)
            {
                return;
            }

            WindowProc(hwnd, m.message, m.wParam, m.lParam);
        }

        private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TRAY)
            {
                int evt = lParam.ToInt32() & 0xFFFF;
                if (evt == Win32.WM_RBUTTONUP)
                {
                    ShowMenu();
                    return IntPtr.Zero;
                }

                if (evt == Win32.WM_LBUTTONDBLCLK)
                {
                    Ui?.ShowWindow();
                    return IntPtr.Zero;
                }
            }

            if (msg == Win32.WM_COMMAND)
            {
                HandleCommand(wParam.ToInt32() & 0xFFFF);
                return IntPtr.Zero;
            }

            if (msg == Win32.WM_DESTROY)
            {
                Win32.Shell_NotifyIcon(NIM_DELETE, ref iconData);
            }

            return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void ShowMenu()
        {
            SinkSnapshot snapshot = controller.Current;
            bool autoStart = Platform.AutoStart.IsEnabled();

            IntPtr menu = Win32.CreatePopupMenu();
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(CmdOpenWindow), "打开主界面");
            Win32.AppendMenu(menu, MF_STRING, IntPtr.Zero, "—");
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(CmdConnect), "连接 / 重连");
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(CmdDisconnect), "断开");
            Win32.AppendMenu(menu, MF_STRING, IntPtr.Zero, "—");

            Win32.AppendMenu(menu, MF_STRING, IntPtr.Zero, "锁定输出设备（声音固定从这里出）");
            IReadOnlyList<EndpointInfo> outputs = controller.ListPhysicalOutputs();
            if (outputs.Count == 0)
            {
                Win32.AppendMenu(menu, MF_STRING, IntPtr.Zero, "   （未发现可用物理输出）");
            }
            else
            {
                for (int i = 0; i < outputs.Count; i++)
                {
                    bool preferred = outputs[i].Id == controller.PreferredRenderId;
                    string label = (preferred ? "   ● " : "      ") + outputs[i].Name;
                    Win32.AppendMenu(menu, MF_STRING, new IntPtr(CmdOutputBase + i), label);
                }
            }

            Win32.AppendMenu(menu, MF_STRING, IntPtr.Zero, "—");
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(CmdOpenLogs), "打开日志目录");
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(CmdAutoStart), autoStart ? "开机自启：已开启" : "开机自启：已关闭");
            Win32.AppendMenu(menu, MF_STRING, IntPtr.Zero, "—");
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(CmdExit), "退出");

            Win32.POINT point;
            Win32.GetCursorPos(out point);
            Win32.SetForegroundWindow(hwnd);
            int cmd = Win32.TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, 0, hwnd, IntPtr.Zero);
            Win32.DestroyMenu(menu);

            if (cmd != 0)
            {
                HandleCommand(cmd);
            }
        }

        public void HandleCommand(int id)
        {
            if (id >= CmdOutputBase)
            {
                IReadOnlyList<EndpointInfo> outputs = controller.ListPhysicalOutputs();
                int index = id - CmdOutputBase;
                if (index >= 0 && index < outputs.Count)
                {
                    EndpointInfo pick = outputs[index];
                    bool ok = controller.SetPreferredRender(pick.Id, pick.Name);
                    Win32.MessageBox(hwnd,
                        ok ? "已把声音输出锁定到：" + pick.Name
                           : "切换失败，请手动在「设置 → 系统 → 声音」中选择。",
                        "BlueAudioSink", 0x40);
                }
                return;
            }

            switch (id)
            {
                case CmdOpenWindow:
                    Ui?.ShowWindow();
                    break;

                case CmdConnect:
                    controller.RequestRefreshDevices();
                    controller.RequestConnect(null);
                    break;

                case CmdDisconnect:
                    controller.RequestDisconnect();
                    break;

                case CmdAutoStart:
                    Platform.AutoStart.SetEnabled(!Platform.AutoStart.IsEnabled());
                    break;

                case CmdOpenLogs:
                    OpenLogDirectory();
                    break;

                case CmdExit:
                    RequestExit();
                    break;
            }
        }

        /// <summary>用户请求退出：隐藏窗口后结束消息循环。</summary>
        public void RequestExit()
        {
            Ui?.PrepareExit();
            controller.RequestShutdown();
            Win32.Shell_NotifyIcon(NIM_DELETE, ref iconData);
            Win32.PostQuitMessage(0);
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

        public void Dispose()
        {
            try
            {
                controller.SnapshotChanged -= OnSnapshotChanged;
                if (iconData.hIcon != IntPtr.Zero)
                {
                    Win32.DestroyIcon(iconData.hIcon);
                    iconData.hIcon = IntPtr.Zero;
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>主窗口需要向托盘暴露的最小接口，避免循环依赖细节。</summary>
    internal interface SinkHost
    {
        void ShowWindow();

        void PrepareExit();
    }
}
