using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SinkLite.Core;
using SinkLite.Platform;

namespace SinkLite.UI
{
    /// <summary>
    /// 主窗口：Win32 + GDI 手绘，无托盘、无外部依赖。
    ///
    /// 设计：
    ///   - 主页面：状态、音频源选择、输出选择、操作按钮；
    ///   - 设置页面：开机自启、启动时自动连接、打开日志目录；
    ///   - 输出下拉为就地展开的自绘列表；
    ///   - 按钮具备 常态 / 悬停 / 按下 三态；
    ///   - 关闭窗口即退出程序（不再常驻托盘）。
    /// </summary>
    internal sealed class MainWindow : IDisposable
    {
        private const int IdTimer = 1;
        private const int MaxDeviceRows = 5;
        private const int MaxDropdownRows = 6;

        private const int PadX = 18;
        private const int HeaderHeight = 68;
        private const int DeviceRowHeight = 32;
        private const int DropdownRowHeight = 32;
        private const int ButtonHeight = 36;
        private const int SettingRowHeight = 58;

        private const int AreaNone = -1;
        private const int AreaConnect = 1;
        private const int AreaDisconnect = 2;
        private const int AreaRefresh = 3;
        private const int AreaSettings = 4;
        private const int AreaOutput = 5;
        private const int AreaDeviceBase = 100;
        private const int AreaDropdownBase = 200;
        private const int AreaBack = 300;
        private const int AreaAutoStart = 301;
        private const int AreaAutoConnect = 302;
        private const int AreaOpenLogs = 303;
        private const int AreaModeBluetooth = 310;
        private const int AreaModeWifi = 311;

        private readonly SinkController controller;
        private readonly Log log;
        private readonly WifiController wifi;

        private IntPtr hwnd;
        private Win32.WndProcDelegate wndProc;
        private IntPtr fontTitle;
        private IntPtr fontBody;
        private IntPtr fontSmall;
        private IntPtr fontCaption;
        private IntPtr brushBack;
        private IntPtr brushCard;
        private IntPtr brushBorder;
        private IntPtr brushAccent;
        private IntPtr brushAccentHover;
        private IntPtr brushAccentPress;
        private IntPtr brushBtn;
        private IntPtr brushBtnHover;
        private IntPtr brushBtnPress;
        private IntPtr brushRowHover;
        private IntPtr brushRowSelected;
        private IntPtr brushSwitchOff;
        private IntPtr brushSwitchOffHover;

        private double scale = 1.0;
        private bool mouseInside;
        private bool settingsMode;
        private bool wifiMode;

        private int selectedDevice;
        private int hoverArea = AreaNone;
        private int pressedArea = AreaNone;
        private bool dropdownOpen;

        private IReadOnlyList<EndpointInfo> cachedOutputs = new List<EndpointInfo>();
        private string cachedPreferredName = "选择输出设备";
        private DateTime lastOutputRefresh = DateTime.MinValue;
        private string lastPaintKey = string.Empty;

        private Win32.RECT dropdownRect;
        private int dropdownRows;

        public MainWindow(SinkController controller, Log log, bool startWifi)
        {
            this.controller = controller;
            this.log = log;

            Settings settings = Settings.Load();
            wifi = new WifiController(log, settings.WifiPort, settings.BufferBaseMs, settings.BufferMaxMs);
            wifiMode = startWifi || string.Equals(settings.Mode, "wifi", StringComparison.OrdinalIgnoreCase);
        }

        private int S(int value)
        {
            return (int)Math.Round(value * scale);
        }

        public void Create()
        {
            try
            {
                Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch
            {
            }

            uint dpi = Win32.GetDpiForSystem();
            if (dpi < 72 || dpi > 480)
            {
                dpi = 96;
            }
            scale = dpi / 96.0;

            CreateGdiObjects();

            wndProc = WindowProc;
            IntPtr instance = Win32.GetModuleHandle(null);

            Win32.WNDCLASSEX wc = new Win32.WNDCLASSEX();
            wc.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.WNDCLASSEX));
            wc.lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(wndProc);
            wc.hInstance = instance;
            wc.hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW);
            wc.hIcon = LoadAppIcon();
            wc.hIconSm = LoadAppIcon();
            wc.lpszClassName = "BlueAudioSinkMainWindow";
            ushort registered = Win32.RegisterClassEx(ref wc);
            int registrationError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (registered == 0 && registrationError != 1410)
                throw new System.ComponentModel.Win32Exception(registrationError, "注册主窗口失败");

            hwnd = Win32.CreateWindowEx(
                0,
                "BlueAudioSinkMainWindow",
                "BlueAudioSink",
                Win32.WS_CAPTION | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | 0x00040000,
                S(160), S(160), S(580), S(440),
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                log.Error("Ui.CreateFailed", ("win32Error", error));
                throw new System.ComponentModel.Win32Exception(error, "创建主窗口失败");
            }
            log.Info("Ui.WindowHandle", ("hwnd", hwnd.ToInt64()));
            Win32.SetTimer(hwnd, new IntPtr(IdTimer), 1000, IntPtr.Zero);
            controller.SnapshotChanged += OnSnapshotChanged;
            log.Info("Ui.Created", ("dpi", (int)dpi), ("scale", scale), ("mode", wifiMode ? "wifi" : "bluetooth"));

            // 启动即进入 Wi-Fi 模式：接收器与发现广播在窗口显示前就绪。
            // 绑定失败则回退到蓝牙模式，并按既有语义自动连接。
            if (wifiMode && !wifi.Start())
            {
                wifiMode = false;
                Settings settings = Settings.Load();
                if (settings.AutoConnect)
                {
                    string target = string.IsNullOrEmpty(settings.DeviceId) ? null : settings.DeviceId;
                    controller.RequestConnect(target);
                }
                RequestRepaint(true);
            }
        }

        /// <summary>从嵌入资源加载窗口图标，避免额外文件依赖。</summary>
        private static IntPtr LoadAppIcon()
        {
            try
            {
                using (Stream stream = typeof(MainWindow).Assembly.GetManifestResourceStream("app.ico"))
                {
                    if (stream == null)
                    {
                        return IntPtr.Zero;
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

                    return Win32.CreateIconFromResourceEx(data, (uint)read, true, 0x00030000u, 32, 32, 0u);
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private void CreateGdiObjects()
        {
            brushBack = Win32.CreateSolidBrush(Win32.Rgb(246, 246, 246));
            brushCard = Win32.CreateSolidBrush(Win32.Rgb(255, 255, 255));
            brushBorder = Win32.CreateSolidBrush(Win32.Rgb(226, 226, 226));
            brushAccent = Win32.CreateSolidBrush(Win32.Rgb(0, 120, 215));
            brushAccentHover = Win32.CreateSolidBrush(Win32.Rgb(40, 150, 240));
            brushAccentPress = Win32.CreateSolidBrush(Win32.Rgb(0, 85, 160));
            brushBtn = Win32.CreateSolidBrush(Win32.Rgb(255, 255, 255));
            brushBtnHover = Win32.CreateSolidBrush(Win32.Rgb(232, 240, 250));
            brushBtnPress = Win32.CreateSolidBrush(Win32.Rgb(206, 222, 240));
            brushRowHover = Win32.CreateSolidBrush(Win32.Rgb(238, 238, 238));
            brushRowSelected = Win32.CreateSolidBrush(Win32.Rgb(229, 241, 251));
            brushSwitchOff = Win32.CreateSolidBrush(Win32.Rgb(196, 196, 196));
            brushSwitchOffHover = Win32.CreateSolidBrush(Win32.Rgb(168, 168, 168));

            fontTitle = Win32.CreateFontW(-S(20), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontBody = Win32.CreateFontW(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontSmall = Win32.CreateFontW(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontCaption = Win32.CreateFontW(-S(12), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
        }

        private void RecreateFonts()
        {
            Win32.DeleteObject(fontTitle);
            Win32.DeleteObject(fontBody);
            Win32.DeleteObject(fontSmall);
            Win32.DeleteObject(fontCaption);
            fontTitle = Win32.CreateFontW(-S(20), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontBody = Win32.CreateFontW(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontSmall = Win32.CreateFontW(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontCaption = Win32.CreateFontW(-S(12), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
        }

        public void ShowWindow()
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            if (Win32.IsIconic(hwnd))
            {
                Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            }
            else
            {
                Win32.ShowWindow(hwnd, Win32.SW_SHOW);
            }
            // The first ShowWindow call can obey a launcher's SW_HIDE startup flag.
            // A window-only application must explicitly show its UI after that call.
            Win32.ShowWindow(hwnd, Win32.SW_SHOW);
            Win32.SetForegroundWindow(hwnd);
            RefreshOutputCache(true);
            RequestRepaint(true);
        }

        private void OnSnapshotChanged(object sender, SinkSnapshot snapshot)
        {
            RequestRepaint(false);
        }

        private void RequestRepaint(bool force)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            string key = BuildPaintKey();
            if (!force && key == lastPaintKey)
            {
                return;
            }
            lastPaintKey = key;
            Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        private string BuildPaintKey()
        {
            SinkSnapshot s = controller.Current;
            WifiSnapshot w = wifi.Current;
            return string.Concat(
                (int)s.State, "|", s.Detail ?? "", "|", s.DeviceName ?? "", "|",
                s.Peak.ToString("0.000"), "|", s.Hint ?? "", "|",
                selectedDevice, "|", hoverArea, "|", pressedArea, "|",
                dropdownOpen ? 1 : 0, "|", settingsMode ? 1 : 0, "|",
                controller.Devices.Count, "|", cachedOutputs.Count, "|", cachedPreferredName, "|",
                wifiMode ? 1 : 0, "|", (int)w.State, "|", w.Enabled ? 1 : 0, "|",
                w.Detail ?? "", "|", w.Remote ?? "", "|", w.DeviceName ?? "", "|",
                double.IsNaN(w.RttMs) ? "-1" : Math.Round(w.RttMs).ToString(), "|",
                w.BufferMs, "|", w.TargetMs, "|",
                double.IsNaN(w.LatencyMs) ? "-1" : Math.Round(w.LatencyMs).ToString(), "|",
                double.IsNaN(w.LossPercent) ? "-1" : Math.Round(w.LossPercent, 1).ToString(), "|",
                w.Received, "|", w.Missing, "|", w.Discontinuities, "|", w.Underruns);
        }

        private void OnTimer()
        {
            RefreshOutputCache(false);
            RequestRepaint(false);
        }

        private void RefreshOutputCache(bool force)
        {
            if (!force && DateTime.Now - lastOutputRefresh < TimeSpan.FromSeconds(10))
            {
                return;
            }

            lastOutputRefresh = DateTime.Now;
            try
            {
                cachedOutputs = controller.ListPhysicalOutputs();
                string preferred = null;
                foreach (EndpointInfo o in cachedOutputs)
                {
                    if (o.Id == controller.PreferredRenderId)
                    {
                        preferred = o.Name;
                        break;
                    }
                }
                cachedPreferredName = preferred ??
                    (cachedOutputs.Count > 0 ? cachedOutputs[0].Name : "选择输出设备");
            }
            catch
            {
            }
        }

        // ===== 布局 =====

        private struct Metrics
        {
            public int Width;
            public int Height;
            public int Left;
            public int ContentWidth;
            public int SourceLabelTop;
            public int DeviceListTop;
            public int VisibleRows;
            public int OutputLabelTop;
            public int OutputBoxTop;
            public int OutputBoxBottom;
            public int ButtonTop;
            public int ButtonWidth;
            public int ButtonGap;
            public Win32.RECT ConnectRect;
            public Win32.RECT DisconnectRect;
            public Win32.RECT RefreshRect;
            public Win32.RECT SettingsRect;
            public Win32.RECT OutputRect;
            public Win32.RECT BackRect;
            public Win32.RECT LogsButtonRect;
            public Win32.RECT ModeBluetoothRect;
            public Win32.RECT ModeWifiRect;
            public int SettingRowsTop;
        }

        private Metrics ComputeMetrics()
        {
            Win32.RECT client;
            Win32.GetClientRect(hwnd, out client);
            int width = Math.Max(client.Right - client.Left, S(420));
            int height = Math.Max(client.Bottom - client.Top, S(300));

            Metrics m = new Metrics();
            m.Width = width;
            m.Height = height;
            m.Left = S(PadX);
            m.ContentWidth = width - S(PadX) * 2;

            // 顶部模式切换：蓝牙 / Wi-Fi，放在标题右侧，避免与状态标题重叠。
            int modeWidth = S(72);
            int modeTop = S(20);
            int modeHeight = S(28);
            int modeGap = S(4);
            m.ModeWifiRect = Rect(width - S(PadX) - modeWidth, modeTop,
                width - S(PadX), modeTop + modeHeight);
            m.ModeBluetoothRect = Rect(m.ModeWifiRect.Left - modeGap - modeWidth, modeTop,
                m.ModeWifiRect.Left - modeGap, modeTop + modeHeight);

            m.SourceLabelTop = S(HeaderHeight) + S(8);
            m.DeviceListTop = m.SourceLabelTop + S(22);

            int bottomZone = S(ButtonHeight) + S(28);
            int available = height - m.DeviceListTop - bottomZone - S(70);
            int fitRows = Math.Max(1, available / S(DeviceRowHeight));
            int wantRows = Math.Max(1, controller.Devices.Count);
            m.VisibleRows = Math.Max(1, Math.Min(Math.Min(MaxDeviceRows, wantRows), fitRows));

            int listBottom = m.DeviceListTop + m.VisibleRows * S(DeviceRowHeight);
            m.OutputLabelTop = listBottom + S(12);
            m.OutputBoxTop = m.OutputLabelTop + S(22);
            m.OutputBoxBottom = m.OutputBoxTop + S(34);
            m.OutputRect = Rect(m.Left, m.OutputBoxTop, m.Left + m.ContentWidth, m.OutputBoxBottom);

            m.ButtonTop = m.OutputBoxBottom + S(18);
            int maxButtonTop = height - S(ButtonHeight) - S(14);
            if (m.ButtonTop > maxButtonTop)
            {
                m.ButtonTop = maxButtonTop;
            }

            m.ButtonGap = S(10);
            m.ButtonWidth = Math.Max(S(70), (m.ContentWidth - m.ButtonGap * 3) / 4);

            int x = m.Left;
            m.ConnectRect = Rect(x, m.ButtonTop, x + m.ButtonWidth, m.ButtonTop + S(ButtonHeight));
            x = m.ConnectRect.Right + m.ButtonGap;
            m.DisconnectRect = Rect(x, m.ButtonTop, x + m.ButtonWidth, m.ButtonTop + S(ButtonHeight));
            x = m.DisconnectRect.Right + m.ButtonGap;
            m.RefreshRect = Rect(x, m.ButtonTop, x + m.ButtonWidth, m.ButtonTop + S(ButtonHeight));
            x = m.RefreshRect.Right + m.ButtonGap;
            m.SettingsRect = Rect(x, m.ButtonTop, x + m.ButtonWidth, m.ButtonTop + S(ButtonHeight));

            int backWidth = S(76);
            m.BackRect = Rect(m.Left, S(18), m.Left + backWidth, S(18) + S(32));
            m.SettingRowsTop = S(92);

            int logsTop = m.SettingRowsTop + S(SettingRowHeight) * 2 + S(14);
            m.LogsButtonRect = Rect(m.Left, logsTop, m.Left + S(160), logsTop + S(36));

            return m;
        }

        private void ComputeDropdown(Metrics m)
        {
            dropdownRows = Math.Max(1, Math.Min(MaxDropdownRows, cachedOutputs.Count));
            if (dropdownRows == 0)
            {
                dropdownRect = Rect(m.OutputRect.Left, m.OutputRect.Bottom, m.OutputRect.Right, m.OutputRect.Bottom);
                return;
            }

            int height = dropdownRows * S(DropdownRowHeight) + S(8);
            int top = m.OutputRect.Bottom + S(4);
            if (top + height > m.Height - S(8))
            {
                top = Math.Max(S(8), m.OutputRect.Top - S(4) - height);
            }

            dropdownRect = Rect(m.OutputRect.Left, top, m.OutputRect.Right, top + height);
        }

        private int HitTest(int x, int y)
        {
            Metrics m = ComputeMetrics();

            if (settingsMode)
            {
                if (Contains(m.BackRect, x, y))
                {
                    return AreaBack;
                }

                if (Contains(m.LogsButtonRect, x, y))
                {
                    return AreaOpenLogs;
                }

                int rowsTop = m.SettingRowsTop;
                if (x >= m.Left && x <= m.Left + m.ContentWidth &&
                    y >= rowsTop && y < rowsTop + S(SettingRowHeight) * 2)
                {
                    int index = (y - rowsTop) / S(SettingRowHeight);
                    return AreaAutoStart + index;
                }

                return AreaNone;
            }

            if (Contains(m.ModeBluetoothRect, x, y))
            {
                return AreaModeBluetooth;
            }

            if (Contains(m.ModeWifiRect, x, y))
            {
                return AreaModeWifi;
            }

            if (dropdownOpen)
            {
                ComputeDropdown(m);
                if (Contains(dropdownRect, x, y))
                {
                    int index = (y - dropdownRect.Top - S(4)) / S(DropdownRowHeight);
                    if (index >= 0 && index < dropdownRows)
                    {
                        return AreaDropdownBase + index;
                    }
                    return AreaNone;
                }
            }

            if (Contains(m.ConnectRect, x, y)) { return AreaConnect; }
            if (Contains(m.DisconnectRect, x, y)) { return AreaDisconnect; }
            if (Contains(m.RefreshRect, x, y)) { return AreaRefresh; }
            if (Contains(m.SettingsRect, x, y)) { return AreaSettings; }
            if (Contains(m.OutputRect, x, y)) { return AreaOutput; }

            if (x >= m.Left && x <= m.Left + m.ContentWidth &&
                y >= m.DeviceListTop && y < m.DeviceListTop + m.VisibleRows * S(DeviceRowHeight))
            {
                int index = (y - m.DeviceListTop) / S(DeviceRowHeight);
                if (index >= 0 && index < controller.Devices.Count && index < m.VisibleRows)
                {
                    return AreaDeviceBase + index;
                }
            }

            return AreaNone;
        }

        // ===== 输入 =====

        private void OnMouseMove(int x, int y)
        {
            if (!mouseInside)
            {
                mouseInside = true;
                Win32.TRACKMOUSEEVENT tme = new Win32.TRACKMOUSEEVENT();
                tme.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.TRACKMOUSEEVENT));
                tme.dwFlags = Win32.TME_LEAVE;
                tme.hwndTrack = hwnd;
                Win32.TrackMouseEvent(ref tme);
            }

            int area = HitTest(x, y);
            if (area != hoverArea)
            {
                hoverArea = area;
                RequestRepaint(true);
            }
        }

        private void OnMouseLeave()
        {
            mouseInside = false;
            if (hoverArea != AreaNone)
            {
                hoverArea = AreaNone;
                RequestRepaint(true);
            }
        }

        private void OnMouseDown(int x, int y)
        {
            pressedArea = HitTest(x, y);
            RequestRepaint(true);
        }

        private void OnMouseUp(int x, int y)
        {
            int releaseArea = HitTest(x, y);
            int area = pressedArea;
            pressedArea = AreaNone;

            if (area != AreaNone && area == releaseArea)
            {
                Activate(area);
            }
            else if (dropdownOpen)
            {
                dropdownOpen = false;
            }

            RequestRepaint(true);
        }

        private void Activate(int area)
        {
            if (area >= AreaDropdownBase && area < AreaBack)
            {
                int index = area - AreaDropdownBase;
                if (index >= 0 && index < cachedOutputs.Count)
                {
                    EndpointInfo pick = cachedOutputs[index];
                    controller.SetPreferredRender(pick.Id, pick.Name);
                    cachedPreferredName = pick.Name;
                    lastOutputRefresh = DateTime.MinValue;
                    log.Info("Ui.OutputPicked", ("endpoint", pick.Name));
                }
                dropdownOpen = false;
                return;
            }

            switch (area)
            {
                case AreaModeBluetooth:
                    SetMode(false);
                    return;

                case AreaModeWifi:
                    SetMode(true);
                    return;

                case AreaOutput:
                    dropdownOpen = !dropdownOpen;
                    RefreshOutputCache(true);
                    return;

                case AreaSettings:
                    settingsMode = true;
                    dropdownOpen = false;
                    log.Info("Ui.SettingsOpened");
                    return;

                case AreaBack:
                    settingsMode = false;
                    return;

                case AreaAutoStart:
                {
                    bool enabled = !AutoStart.IsEnabled();
                    AutoStart.SetEnabled(enabled);
                    log.Info("Ui.AutoStartToggled", ("enabled", enabled));
                    return;
                }

                case AreaAutoConnect:
                {
                    Settings settings = Settings.Load();
                    settings.AutoConnect = !settings.AutoConnect;
                    settings.Save();
                    log.Info("Ui.AutoConnectToggled", ("enabled", settings.AutoConnect));
                    return;
                }

                case AreaOpenLogs:
                    OpenLogDirectory();
                    return;

                case AreaConnect:
                {
                    IReadOnlyList<SinkDevice> devices = controller.Devices;
                    string deviceId = selectedDevice >= 0 && selectedDevice < devices.Count
                        ? devices[selectedDevice].Id
                        : null;
                    controller.RequestConnect(deviceId);
                    log.Info("Ui.ConnectClicked", ("device", deviceId ?? "(默认)"));
                    return;
                }

                case AreaDisconnect:
                    controller.RequestDisconnect();
                    log.Info("Ui.DisconnectClicked");
                    return;

                case AreaRefresh:
                    controller.RequestRefreshDevices();
                    lastOutputRefresh = DateTime.MinValue;
                    return;
            }

            if (area >= AreaDeviceBase && area < AreaDropdownBase)
            {
                int index = area - AreaDeviceBase;
                IReadOnlyList<SinkDevice> devices = controller.Devices;
                if (index >= 0 && index < devices.Count)
                {
                    selectedDevice = index;
                    controller.RequestConnect(devices[index].Id);
                    log.Info("Ui.DevicePicked", ("device", devices[index].Name));
                }
            }
        }

        /// <summary>
        /// 切换蓝牙 / Wi-Fi。两种模式互斥：进入 Wi-Fi 先断开蓝牙，回到蓝牙先停止 Wi-Fi。
        /// </summary>
        private void SetMode(bool useWifi)
        {
            if (wifiMode == useWifi)
            {
                return;
            }

            if (useWifi)
            {
                // 先绑定 Wi-Fi 端口；失败则保持蓝牙不动（协议 §8.1）。
                if (!wifi.Start())
                {
                    RequestRepaint(true);
                    return;
                }

                controller.RequestDisconnect();
                wifiMode = true;
                SaveMode("wifi");
            }
            else
            {
                // 先停 Wi-Fi 输出与 socket，再走蓝牙连接（协议 §8.2）。
                wifi.Stop("bluetooth");
                wifiMode = false;
                SaveMode("bluetooth");

                // 与启动时的自动连接语义保持一致；用户仍可手动点"连接"。
                Settings settings = Settings.Load();
                if (settings.AutoConnect)
                {
                    IReadOnlyList<SinkDevice> devices = controller.Devices;
                    string deviceId = selectedDevice >= 0 && selectedDevice < devices.Count
                        ? devices[selectedDevice].Id
                        : null;
                    controller.RequestConnect(deviceId);
                }
            }

            dropdownOpen = false;
            RequestRepaint(true);
        }

        private static void SaveMode(string mode)
        {
            try
            {
                Settings settings = Settings.Load();
                if (string.Equals(settings.Mode, mode, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                settings.Mode = mode;
                settings.Save();
            }
            catch
            {
            }
        }

        private void OpenLogDirectory()
        {
            try
            {
                Directory.CreateDirectory(Log.DefaultDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = Log.DefaultDirectory,
                    UseShellExecute = true
                });
                log.Info("Ui.LogsOpened");
            }
            catch (Exception ex)
            {
                log.Warn("Ui.LogsOpenFailed", ("error", ex.Message));
            }
        }

        private void OnKeyDown(int vk)
        {
            if (vk == 0x1B)
            {
                if (dropdownOpen)
                {
                    dropdownOpen = false;
                    RequestRepaint(true);
                }
                else if (settingsMode)
                {
                    settingsMode = false;
                    RequestRepaint(true);
                }
            }
        }

        private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            // 仅鼠标消息的 lParam 是坐标；其他消息可能是 64 位指针，直接 ToInt32 会溢出崩溃
            int x = 0;
            int y = 0;
            if (msg == Win32.WM_MOUSEMOVE || msg == Win32.WM_LBUTTONDOWN || msg == Win32.WM_LBUTTONUP)
            {
                long lp = lParam.ToInt64();
                x = (short)(lp & 0xFFFF);
                y = (short)((lp >> 16) & 0xFFFF);
            }

            switch (msg)
            {
                case Win32.WM_PAINT:
                    Paint(hWnd);
                    return IntPtr.Zero;

                case Win32.WM_ERASEBKGND:
                    return new IntPtr(1);

                case Win32.WM_SIZE:
                    RequestRepaint(true);
                    return IntPtr.Zero;

                case 0x02E0: // WM_DPICHANGED
                    uint dpi = (uint)(wParam.ToInt32() & 0xFFFF);
                    if (dpi >= 72 && dpi <= 480)
                    {
                        scale = dpi / 96.0;
                        RecreateFonts();
                    }
                    RequestRepaint(true);
                    return IntPtr.Zero;

                case Win32.WM_TIMER:
                    OnTimer();
                    return IntPtr.Zero;

                case Win32.WM_MOUSEMOVE:
                    OnMouseMove(x, y);
                    return IntPtr.Zero;

                case Win32.WM_MOUSELEAVE:
                    OnMouseLeave();
                    return IntPtr.Zero;

                case Win32.WM_LBUTTONDOWN:
                    OnMouseDown(x, y);
                    return IntPtr.Zero;

                case Win32.WM_LBUTTONUP:
                    OnMouseUp(x, y);
                    return IntPtr.Zero;

                case Win32.WM_KEYDOWN:
                    OnKeyDown(wParam.ToInt32());
                    return IntPtr.Zero;

                case Win32.WM_CLOSE:
                    // 没有托盘了：关闭即退出，交给默认处理销毁窗口
                    break;

                case Win32.WM_DESTROY:
                    Win32.KillTimer(hWnd, new IntPtr(IdTimer));
                    Win32.PostQuitMessage(0);
                    return IntPtr.Zero;
            }

            return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        // ===== 绘制 =====

        private void Paint(IntPtr hWnd)
        {
            Win32.PAINTSTRUCT ps;
            IntPtr hdcScreen = Win32.BeginPaint(hWnd, out ps);
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hold = IntPtr.Zero;

            try
            {
                Win32.RECT client;
                Win32.GetClientRect(hWnd, out client);
                int width = Math.Max(client.Right - client.Left, 1);
                int height = Math.Max(client.Bottom - client.Top, 1);

                hdcMem = Win32.CreateCompatibleDC(hdcScreen);
                hBitmap = Win32.CreateCompatibleBitmap(hdcScreen, width, height);
                hold = Win32.SelectObject(hdcMem, hBitmap);

                Win32.SetBkMode(hdcMem, Win32.TRANSPARENT);
                Win32.RECT full = Rect(0, 0, width, height);
                Win32.FillRect(hdcMem, ref full, brushBack);

                Metrics m = ComputeMetrics();

                if (settingsMode)
                {
                    DrawSettings(hdcMem, m);
                }
                else if (wifiMode)
                {
                    WifiSnapshot wifiSnapshot = wifi.Current;
                    DrawHeader(hdcMem, m, wifiSnapshot);
                    DrawModeTabs(hdcMem, m);
                    DrawWifiPage(hdcMem, m, wifiSnapshot);
                }
                else
                {
                    SinkSnapshot snapshot = controller.Current;
                    DrawHeader(hdcMem, m, snapshot);
                    DrawModeTabs(hdcMem, m);
                    DrawDeviceSection(hdcMem, m, snapshot);
                    DrawOutputSection(hdcMem, m);
                    DrawButtons(hdcMem, m);
                    if (dropdownOpen)
                    {
                        DrawDropdown(hdcMem);
                    }
                }

                Win32.BitBlt(hdcScreen, 0, 0, width, height, hdcMem, 0, 0, Win32.SRCCOPY);
            }
            finally
            {
                if (hold != IntPtr.Zero)
                {
                    Win32.SelectObject(hdcMem, hold);
                }
                if (hBitmap != IntPtr.Zero)
                {
                    Win32.DeleteObject(hBitmap);
                }
                if (hdcMem != IntPtr.Zero)
                {
                    Win32.DeleteDC(hdcMem);
                }
                Win32.EndPaint(hWnd, ref ps);
            }
        }

        private void DrawHeader(IntPtr hdc, Metrics m, SinkSnapshot snapshot)
        {
            string device = string.IsNullOrEmpty(snapshot.DeviceName) ? "无设备" : snapshot.DeviceName;
            string sub = snapshot.Peak > 0.0005f
                ? device + "  ·  " + snapshot.Peak.ToString("0.000")
                : device;
            DrawHeaderCore(hdc, m, DescribeState(snapshot.State), sub, StateColor(snapshot.State));
        }

        private void DrawHeader(IntPtr hdc, Metrics m, WifiSnapshot snapshot)
        {
            string sub;
            if (!snapshot.Enabled)
            {
                sub = string.IsNullOrEmpty(snapshot.Detail) ? "Wi-Fi 模式未启用" : snapshot.Detail;
            }
            else if (!string.IsNullOrEmpty(snapshot.Remote))
            {
                sub = "手机 " + snapshot.Remote;
                if (snapshot.State == SinkState.LinkReady || snapshot.State == SinkState.Flowing)
                {
                    sub += "  ·  缓冲 " + snapshot.BufferMs + " / " + snapshot.TargetMs + " ms";
                }
            }
            else
            {
                sub = "本机 " + (snapshot.LocalAddress ?? "?") + "  ·  UDP " + snapshot.Port;
            }

            DrawHeaderCore(hdc, m, DescribeWifiState(snapshot), sub, WifiStateColor(snapshot));
        }

        private void DrawHeaderCore(IntPtr hdc, Metrics m, string title, string subtitle, int color)
        {
            IntPtr dotBrush = Win32.CreateSolidBrush(color);
            IntPtr old = Win32.SelectObject(hdc, dotBrush);
            int dotTop = S(24);
            Win32.Ellipse(hdc, m.Left, dotTop, m.Left + S(12), dotTop + S(12));
            Win32.SelectObject(hdc, old);
            Win32.DeleteObject(dotBrush);

            // 右侧要给模式切换留位，避免标题与"蓝牙 / Wi-Fi"按钮重叠。
            int textRight = Math.Max(m.Left + S(80), m.ModeBluetoothRect.Left - S(8));

            IntPtr oldFont = Win32.SelectObject(hdc, fontTitle);
            Win32.SetTextColor(hdc, Win32.Rgb(24, 24, 24));
            Win32.RECT titleRect = Rect(m.Left + S(22), S(8), textRight, S(40));
            Win32.DrawText(hdc, title, -1, ref titleRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(118, 118, 118));
            Win32.RECT subRect = Rect(m.Left + S(22), S(40), textRight, S(60));
            Win32.DrawText(hdc, subtitle, -1, ref subRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            Win32.SelectObject(hdc, oldFont);
        }

        private void DrawModeTabs(IntPtr hdc, Metrics m)
        {
            DrawModeTab(hdc, m.ModeBluetoothRect, "蓝牙", !wifiMode, AreaModeBluetooth);
            DrawModeTab(hdc, m.ModeWifiRect, "Wi-Fi", wifiMode, AreaModeWifi);
        }

        private void DrawModeTab(IntPtr hdc, Win32.RECT rect, string text, bool active, int area)
        {
            bool hover = !active && hoverArea == area;
            bool pressed = pressedArea == area;
            IntPtr fill;
            if (active)
            {
                fill = pressed ? brushAccentPress : (hover ? brushAccentHover : brushAccent);
            }
            else
            {
                fill = pressed ? brushBtnPress : (hover ? brushBtnHover : brushCard);
            }

            RoundFill(hdc, rect, rect.Bottom - rect.Top, fill);
            if (!active)
            {
                RoundBorder(hdc, rect, rect.Bottom - rect.Top, hover ? brushAccent : brushBorder);
            }

            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, active ? Win32.Rgb(255, 255, 255) : Win32.Rgb(60, 60, 60));
            Win32.RECT textRect = rect;
            Win32.DrawText(hdc, text, -1, ref textRect,
                Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
        }

        private void DrawWifiPage(IntPtr hdc, Metrics m, WifiSnapshot s)
        {
            int top = S(HeaderHeight) + S(8);
            DrawCaption(hdc, "Wi-Fi 接收", m.Left, top);

            int cardTop = top + S(24);
            int cardBottom = Math.Min(m.Height - S(76), cardTop + S(250));
            if (cardBottom < cardTop + S(150))
            {
                cardBottom = cardTop + S(150);
            }

            Win32.RECT card = Rect(m.Left, cardTop, m.Left + m.ContentWidth, cardBottom);
            RoundFill(hdc, card, S(10), brushCard);
            RoundBorder(hdc, card, S(10), brushBorder);

            int left = card.Left + S(16);
            int right = card.Right - S(16);
            int y = card.Top + S(12);

            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(110, 110, 110));
            string device = string.IsNullOrEmpty(s.DeviceName) ? string.Empty : "（" + s.DeviceName + "）";
            string remote = string.IsNullOrEmpty(s.Remote) ? "未连接" : s.Remote;
            DrawWifiLine(hdc, "手机：" + remote + device, left, y, right);
            y += S(20);
            DrawWifiLine(hdc, "本机：" + (s.LocalAddress ?? "?") + ":" + s.Port +
                "   ·   发现广播端口 " + WifiTransport.ControlPort, left, y, right);
            y += S(26);

            int statWidth = (right - left - S(12)) / 2;
            DrawWifiStat(hdc, left, y, statWidth, "RTT", FormatMs(s.RttMs));
            DrawWifiStat(hdc, left + statWidth + S(12), y, statWidth, "缓冲深度",
                s.BufferMs + " / " + s.TargetMs + " ms");
            y += S(46);
            DrawWifiStat(hdc, left, y, statWidth, "估算延迟", FormatMs(s.LatencyMs));
            DrawWifiStat(hdc, left + statWidth + S(12), y, statWidth, "丢包率",
                FormatPercent(s.LossPercent));
            y += S(48);

            int barTop = Math.Min(y, card.Bottom - S(46));
            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(128, 128, 128));
            Win32.RECT barLabel = Rect(left, barTop - S(4), right, barTop + S(14));
            string barText = s.Enabled ? "缓冲（自适应目标 " + s.TargetMs + " ms）" : "缓冲（Wi-Fi 未启用）";
            Win32.DrawText(hdc, barText, -1, ref barLabel,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            int barY = barTop + S(18);
            Win32.RECT barBack = Rect(left, barY, right, barY + S(6));
            RoundFill(hdc, barBack, S(3), brushBorder);
            int capacity = Math.Max(1, s.TargetMs);
            double ratio = s.BufferMs / (double)capacity;
            if (ratio > 1.0)
            {
                ratio = 1.0;
            }
            int fillWidth = (int)Math.Round((right - left) * ratio);
            if (fillWidth > S(2))
            {
                Win32.RECT barFill = Rect(left, barY, left + fillWidth, barY + S(6));
                RoundFill(hdc, barFill, S(3), brushAccent);
            }

            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(128, 128, 128));
            Win32.RECT counterRect = Rect(left, barY + S(10), right, barY + S(28));
            string counters = "收包 " + s.Received + " · 丢包 " + s.Missing +
                " · 断流 " + s.Discontinuities + " · 欠载 " + s.Underruns;
            if (s.Dropped > 0)
            {
                counters += " · 溢出丢弃 " + s.Dropped;
            }
            Win32.DrawText(hdc, counters, -1, ref counterRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            int hintTop = card.Bottom + S(10);
            if (hintTop + S(36) <= m.Height)
            {
                Win32.SelectObject(hdc, fontSmall);
                Win32.SetTextColor(hdc, Win32.Rgb(140, 140, 140));
                Win32.RECT hint1 = Rect(m.Left, hintTop, m.Left + m.ContentWidth, hintTop + S(18));
                Win32.RECT hint2 = Rect(m.Left, hintTop + S(18), m.Left + m.ContentWidth, hintTop + S(36));
                Win32.DrawText(hdc, WifiHint(s), -1, ref hint1,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
                Win32.DrawText(hdc, "蓝牙与 Wi-Fi 互斥：切换模式会先停止当前模式。", -1, ref hint2,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
            }
        }

        private void DrawWifiLine(IntPtr hdc, string text, int left, int top, int right)
        {
            Win32.RECT rect = Rect(left, top, right, top + S(18));
            Win32.DrawText(hdc, text, -1, ref rect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
        }

        private void DrawWifiStat(IntPtr hdc, int left, int top, int width, string label, string value)
        {
            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(128, 128, 128));
            Win32.RECT labelRect = Rect(left, top, left + width, top + S(16));
            Win32.DrawText(hdc, label, -1, ref labelRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);

            Win32.SelectObject(hdc, fontTitle);
            Win32.SetTextColor(hdc, Win32.Rgb(32, 32, 32));
            Win32.RECT valueRect = Rect(left, top + S(16), left + width, top + S(40));
            Win32.DrawText(hdc, value, -1, ref valueRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
        }

        private static string FormatMs(double value)
        {
            if (double.IsNaN(value) || value < 0)
            {
                return "—";
            }
            return value.ToString("0") + " ms";
        }

        private static string FormatPercent(double value)
        {
            if (double.IsNaN(value))
            {
                return "—";
            }
            return value.ToString("0.0") + " %";
        }

        private static string WifiHint(WifiSnapshot s)
        {
            if (!s.Enabled)
            {
                return s.Detail ?? "Wi-Fi 模式未启用。";
            }

            switch (s.State)
            {
                case SinkState.Flowing:
                    return "接收中；手机停止后会播放完缓冲再回到待机。";
                case SinkState.LinkReady:
                    return "正在预缓冲/重缓冲，攒够目标缓冲后自动播放。";
                case SinkState.Faulted:
                    return string.IsNullOrEmpty(s.Detail) ? "Wi-Fi 故障，请重新选择 Wi-Fi 模式。" : s.Detail;
                case SinkState.Busy:
                    return "正在切换模式，请稍候。";
                default:
                    return "正在广播设备名，请在手机 App 中选择本机；5 秒无音频自动待机。";
            }
        }

        private void DrawDeviceSection(IntPtr hdc, Metrics m, SinkSnapshot snapshot)
        {
            DrawCaption(hdc, "音频源", m.Left, m.SourceLabelTop);

            IReadOnlyList<SinkDevice> devices = controller.Devices;
            int drawn = 0;
            for (int i = 0; i < devices.Count && drawn < m.VisibleRows; i++, drawn++)
            {
                int top = m.DeviceListTop + drawn * S(DeviceRowHeight);
                Win32.RECT row = Rect(m.Left, top, m.Left + m.ContentWidth, top + S(DeviceRowHeight) - S(2));

                bool isActive = devices[i].Id == snapshot.DeviceId;
                int area = AreaDeviceBase + i;

                if (isActive || hoverArea == area)
                {
                    RoundFill(hdc, row, S(6), isActive ? brushRowSelected : brushRowHover);
                }

                Win32.SelectObject(hdc, fontBody);
                Win32.SetTextColor(hdc, isActive ? Win32.Rgb(0, 100, 40) : Win32.Rgb(38, 38, 38));
                Win32.RECT textRect = Rect(row.Left + S(10), row.Top, row.Right - S(10), row.Bottom);
                Win32.DrawText(hdc, devices[i].Name, -1, ref textRect,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
            }

            if (devices.Count == 0)
            {
                Win32.SelectObject(hdc, fontSmall);
                Win32.SetTextColor(hdc, Win32.Rgb(150, 150, 150));
                Win32.RECT emptyRect = Rect(m.Left + S(2), m.DeviceListTop,
                    m.Left + m.ContentWidth, m.DeviceListTop + S(DeviceRowHeight));
                Win32.DrawText(hdc, "未发现设备，请先在 Windows 设置中配对手机", -1, ref emptyRect,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
            }
        }

        private void DrawOutputSection(IntPtr hdc, Metrics m)
        {
            DrawCaption(hdc, "输出", m.Left, m.OutputLabelTop);

            bool hover = hoverArea == AreaOutput;
            bool pressed = pressedArea == AreaOutput;
            RoundFill(hdc, m.OutputRect, S(8), pressed ? brushBtnPress : (hover ? brushBtnHover : brushCard));
            RoundBorder(hdc, m.OutputRect, S(8), hover ? brushAccent : brushBorder);

            Win32.SelectObject(hdc, fontBody);
            Win32.SetTextColor(hdc, Win32.Rgb(32, 32, 32));
            Win32.RECT textRect = Rect(m.OutputRect.Left + S(12), m.OutputRect.Top,
                m.OutputRect.Right - S(30), m.OutputRect.Bottom);
            Win32.DrawText(hdc, cachedPreferredName, -1, ref textRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(110, 110, 110));
            Win32.RECT arrowRect = Rect(m.OutputRect.Right - S(26), m.OutputRect.Top,
                m.OutputRect.Right - S(10), m.OutputRect.Bottom);
            Win32.DrawText(hdc, dropdownOpen ? "▴" : "▾", -1, ref arrowRect,
                Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
        }

        private void DrawDropdown(IntPtr hdc)
        {
            ComputeDropdown(ComputeMetrics());
            if (cachedOutputs.Count == 0)
            {
                return;
            }

            RoundFill(hdc, dropdownRect, S(8), brushCard);
            RoundBorder(hdc, dropdownRect, S(8), brushBorder);

            for (int i = 0; i < dropdownRows; i++)
            {
                int top = dropdownRect.Top + S(4) + i * S(DropdownRowHeight);
                Win32.RECT row = Rect(dropdownRect.Left + S(4), top,
                    dropdownRect.Right - S(4), top + S(DropdownRowHeight));

                bool isCurrent = cachedOutputs[i].Name == cachedPreferredName;
                bool isHover = hoverArea == AreaDropdownBase + i;

                if (isHover || isCurrent)
                {
                    RoundFill(hdc, row, S(6), isCurrent ? brushRowSelected : brushRowHover);
                }

                Win32.SelectObject(hdc, isHover ? fontBody : fontSmall);
                Win32.SetTextColor(hdc, isCurrent ? Win32.Rgb(0, 100, 40) : Win32.Rgb(40, 40, 40));
                Win32.RECT textRect = Rect(row.Left + S(10), row.Top, row.Right - S(10), row.Bottom);
                Win32.DrawText(hdc, cachedOutputs[i].Name, -1, ref textRect,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
            }
        }

        private void DrawButtons(IntPtr hdc, Metrics m)
        {
            DrawButton(hdc, m.ConnectRect, "连接", AreaConnect, true);
            DrawButton(hdc, m.DisconnectRect, "断开", AreaDisconnect, false);
            DrawButton(hdc, m.RefreshRect, "刷新", AreaRefresh, false);
            DrawButton(hdc, m.SettingsRect, "设置", AreaSettings, false);
        }

        /// <summary>
        /// 设置页。开关类功能用经典划钮（左=关，右=开，开启时槽变主色），
        /// 不再做成“可点进下一级”的列表项。
        /// </summary>
        private void DrawSettings(IntPtr hdc, Metrics m)
        {
            DrawButton(hdc, m.BackRect, "← 返回", AreaBack, false);

            IntPtr oldFont = Win32.SelectObject(hdc, fontTitle);
            Win32.SetTextColor(hdc, Win32.Rgb(24, 24, 24));
            Win32.RECT titleRect = Rect(m.Left + S(90), S(18), m.Left + m.ContentWidth, S(18) + S(32));
            Win32.DrawText(hdc, "设置", -1, ref titleRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
            Win32.SelectObject(hdc, oldFont);

            bool autoStart = AutoStart.IsEnabled();
            Settings settings = Settings.Load();

            DrawSwitchRow(hdc, m, 0, AreaAutoStart, "开机自启", autoStart);
            DrawSwitchRow(hdc, m, 1, AreaAutoConnect, "启动时自动连接", settings.AutoConnect);

            // 动作项：日志目录
            DrawButton(hdc, m.LogsButtonRect, "打开日志目录", AreaOpenLogs, false);

            // 底部信息
            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(140, 140, 140));
            int infoTop = m.LogsButtonRect.Bottom + S(22);
            Win32.RECT versionRect = Rect(m.Left, infoTop, m.Left + m.ContentWidth, infoTop + S(20));
            Win32.DrawText(hdc, "BlueAudioSink 0.1.0", -1, ref versionRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);

            Win32.RECT pathRect = Rect(m.Left, infoTop + S(20), m.Left + m.ContentWidth, infoTop + S(42));
            Win32.DrawText(hdc, "日志：" + Log.DefaultDirectory, -1, ref pathRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
        }

        /// <summary>一行设置：左侧名称，右侧划钮。整行可点击切换。</summary>
        private void DrawSwitchRow(IntPtr hdc, Metrics m, int index, int area, string label, bool on)
        {
            int top = m.SettingRowsTop + index * S(SettingRowHeight);
            Win32.RECT row = Rect(m.Left, top, m.Left + m.ContentWidth, top + S(SettingRowHeight));

            bool hover = hoverArea == area;
            if (hover)
            {
                Win32.RECT inner = Rect(row.Left + S(2), row.Top + S(2), row.Right - S(2), row.Bottom - S(2));
                RoundFill(hdc, inner, S(8), brushRowHover);
            }

            Win32.SelectObject(hdc, fontBody);
            Win32.SetTextColor(hdc, Win32.Rgb(32, 32, 32));
            Win32.RECT labelRect = Rect(row.Left + S(10), row.Top, row.Right - S(72), row.Bottom);
            Win32.DrawText(hdc, label, -1, ref labelRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            DrawSwitch(hdc, row.Right - S(12), (row.Top + row.Bottom) / 2, on, hover);
        }

        /// <summary>经典划钮：关闭时滑块在左且槽为灰色，开启时滑块在右且槽为主色。</summary>
        private void DrawSwitch(IntPtr hdc, int right, int centerY, bool on, bool hover)
        {
            int trackWidth = S(44);
            int trackHeight = S(24);
            int knobSize = S(18);
            int padding = S(3);

            int left = right - trackWidth;
            int top = centerY - trackHeight / 2;

            Win32.RECT track = Rect(left, top, left + trackWidth, top + trackHeight);
            IntPtr trackBrush;
            if (on)
            {
                trackBrush = hover ? brushAccentHover : brushAccent;
            }
            else
            {
                trackBrush = hover ? brushSwitchOffHover : brushSwitchOff;
            }

            RoundFill(hdc, track, trackHeight / 2, trackBrush);

            int knobLeft = on
                ? left + trackWidth - padding - knobSize
                : left + padding;
            Win32.RECT knob = Rect(knobLeft, top + padding,
                knobLeft + knobSize, top + padding + knobSize);

            IntPtr knobBrush = Win32.CreateSolidBrush(Win32.Rgb(255, 255, 255));
            IntPtr old = Win32.SelectObject(hdc, knobBrush);
            IntPtr pen = Win32.CreatePen(Win32.PS_SOLID, 1, Win32.Rgb(255, 255, 255));
            IntPtr oldPen = Win32.SelectObject(hdc, pen);
            Win32.Ellipse(hdc, knob.Left, knob.Top, knob.Right, knob.Bottom);
            Win32.SelectObject(hdc, oldPen);
            Win32.SelectObject(hdc, old);
            Win32.DeleteObject(pen);
            Win32.DeleteObject(knobBrush);
        }

        private void DrawButton(IntPtr hdc, Win32.RECT rect, string text, int area, bool primary)
        {
            bool hover = hoverArea == area;
            bool pressed = pressedArea == area;

            IntPtr fill;
            int textColor;

            if (primary)
            {
                fill = pressed ? brushAccentPress : (hover ? brushAccentHover : brushAccent);
                textColor = Win32.Rgb(255, 255, 255);
            }
            else
            {
                fill = pressed ? brushBtnPress : (hover ? brushBtnHover : brushBtn);
                textColor = Win32.Rgb(38, 38, 38);
            }

            RoundFill(hdc, rect, S(8), fill);
            if (!primary)
            {
                RoundBorder(hdc, rect, S(8), hover ? brushAccent : brushBorder);
            }

            Win32.SelectObject(hdc, fontBody);
            Win32.SetTextColor(hdc, textColor);
            Win32.RECT textRect = rect;
            if (pressed)
            {
                textRect.Top += S(1);
                textRect.Bottom += S(1);
            }
            Win32.DrawText(hdc, text, -1, ref textRect,
                Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
        }

        private void DrawCaption(IntPtr hdc, string text, int x, int top)
        {
            Win32.SelectObject(hdc, fontCaption);
            Win32.SetTextColor(hdc, Win32.Rgb(128, 128, 128));
            Win32.RECT rect = Rect(x, top, x + S(200), top + S(18));
            Win32.DrawText(hdc, text, -1, ref rect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
        }

        private void RoundFill(IntPtr hdc, Win32.RECT rect, int radius, IntPtr brush)
        {
            IntPtr old = Win32.SelectObject(hdc, brush);
            IntPtr pen = Win32.CreatePen(Win32.PS_SOLID, 1, Win32.Rgb(246, 246, 246));
            IntPtr oldPen = Win32.SelectObject(hdc, pen);
            Win32.RoundRect(hdc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius);
            Win32.SelectObject(hdc, oldPen);
            Win32.SelectObject(hdc, old);
            Win32.DeleteObject(pen);
        }

        private void RoundBorder(IntPtr hdc, Win32.RECT rect, int radius, IntPtr brush)
        {
            IntPtr pen = Win32.CreatePen(Win32.PS_SOLID, 1, Win32.Rgb(214, 214, 214));
            IntPtr oldPen = Win32.SelectObject(hdc, pen);
            IntPtr oldBrush = Win32.SelectObject(hdc, Win32.GetStockObject(Win32.HOLLOW_BRUSH));
            Win32.RoundRect(hdc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius);
            Win32.SelectObject(hdc, oldBrush);
            Win32.SelectObject(hdc, oldPen);
            Win32.DeleteObject(pen);
        }

        private static Win32.RECT Rect(int left, int top, int right, int bottom)
        {
            Win32.RECT r = new Win32.RECT();
            r.Left = left;
            r.Top = top;
            r.Right = right;
            r.Bottom = bottom;
            return r;
        }

        private static bool Contains(Win32.RECT r, int x, int y)
        {
            return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
        }

        private static int WifiStateColor(WifiSnapshot snapshot)
        {
            if (!snapshot.Enabled || snapshot.State == SinkState.Idle)
            {
                return Win32.Rgb(150, 150, 150);
            }

            return StateColor(snapshot.State);
        }

        private static string DescribeWifiState(WifiSnapshot snapshot)
        {
            if (!snapshot.Enabled)
            {
                return "Wi-Fi 未启用";
            }

            switch (snapshot.State)
            {
                case SinkState.Busy: return "正在切换模式";
                case SinkState.Armed: return "等待手机连接";
                case SinkState.LinkReady: return "正在缓冲";
                case SinkState.Flowing: return "正在接收 Wi-Fi 音频";
                case SinkState.Faulted: return "Wi-Fi 故障";
                case SinkState.Backoff: return "Wi-Fi 重试中";
                default: return "Wi-Fi 未启用";
            }
        }

        private static int StateColor(SinkState state)
        {
            switch (state)
            {
                case SinkState.Flowing: return Win32.Rgb(16, 150, 70);
                case SinkState.LinkReady: return Win32.Rgb(0, 120, 215);
                case SinkState.Armed: return Win32.Rgb(224, 150, 0);
                case SinkState.Busy:
                case SinkState.Backoff: return Win32.Rgb(230, 130, 30);
                case SinkState.Faulted: return Win32.Rgb(200, 50, 50);
                default: return Win32.Rgb(150, 150, 150);
            }
        }

        private static string DescribeState(SinkState state)
        {
            switch (state)
            {
                case SinkState.Idle: return "未连接";
                case SinkState.Busy: return "正在连接";
                case SinkState.Armed: return "等待手机发送音频";
                case SinkState.LinkReady: return "链路已建立";
                case SinkState.Flowing: return "正在传输音频";
                case SinkState.Backoff: return "重试中";
                case SinkState.Faulted: return "连接失败";
                default: return state.ToString();
            }
        }

        public void Dispose()
        {
            try
            {
                controller.SnapshotChanged -= OnSnapshotChanged;
                Win32.DeleteObject(fontTitle);
                Win32.DeleteObject(fontBody);
                Win32.DeleteObject(fontSmall);
                Win32.DeleteObject(fontCaption);
                Win32.DeleteObject(brushBack);
                Win32.DeleteObject(brushCard);
                Win32.DeleteObject(brushBorder);
                Win32.DeleteObject(brushAccent);
                Win32.DeleteObject(brushAccentHover);
                Win32.DeleteObject(brushAccentPress);
                Win32.DeleteObject(brushBtn);
                Win32.DeleteObject(brushBtnHover);
                Win32.DeleteObject(brushBtnPress);
                Win32.DeleteObject(brushRowHover);
                Win32.DeleteObject(brushRowSelected);
                Win32.DeleteObject(brushSwitchOff);
                Win32.DeleteObject(brushSwitchOffHover);
            }
            catch
            {
            }
        }
    }
}
