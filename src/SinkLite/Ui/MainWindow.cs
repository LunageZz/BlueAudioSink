using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SinkLite.Core;
using SinkLite.Platform;

namespace SinkLite.UI
{
    /// <summary>
    /// 主窗口：Win32 + GDI 手绘，无托盘、无外部依赖。
    ///
    /// 设计：
    ///   - 主页面：只读连接状态和设备流向、连接/断开操作；
    ///   - 设置页面：统一播放输出、连接设备切换和通用偏好；
    ///   - 输出下拉为就地展开的自绘列表；
    ///   - 按钮具备 常态 / 悬停 / 按下 三态；
    ///   - 关闭窗口即退出程序（不再常驻托盘）。
    /// </summary>
    internal sealed class MainWindow : IDisposable
    {
        private const int IdTimer = 1;
        private const int MaxDeviceRows = 5;
        private const int MaxDropdownRows = 6;

        private const int PadX = 16;
        private const int DeviceRowHeight = 34;
        private const int DropdownRowHeight = 32;

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
        private const int AreaDetails = 312;
        private const int AreaCopyAddress = 313;
        private const int AreaRetryWifi = 314;
        private const int AreaBuffer80 = 320;
        private const int AreaBuffer120 = 321;
        private const int AreaBuffer160 = 322;
        private const int AreaSettingsGeneral = 330;
        private const int AreaSettingsDevices = 331;
        private const int AreaPreviousPage = 332;
        private const int AreaNextPage = 333;
        private const int AreaDeviceBluetooth = 334;
        private const int AreaDeviceWifi = 335;
        private const int AreaSettingsNetwork = 340;
        private const int AreaFirewallEnable = 341;
        private const int AreaFirewallDisable = 342;
        private const int AreaFirewallCheck = 343;

        private readonly SinkController controller;
        private readonly Log log;
        private readonly WifiController wifi;

        private IntPtr hwnd;
        private Win32.WndProcDelegate wndProc;
        private IntPtr fontTitle;
        private IntPtr fontValue;
        private IntPtr fontHeading;
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
        private bool wifiDetails;
        private bool modeSwitching;
        private int focusArea = AreaNone;
        private bool keyboardFocus;
        private Settings pageSettings;
        private bool autoStartEnabled;
        private string notice;
        private DateTime noticeUntil;

        private bool deviceSettings;
        private bool networkSettings;
        private FirewallStatus firewallStatus = new FirewallStatus();
        private Task<FirewallResult> firewallTask;
        private bool firewallChanging;
        private string firewallMessage;
        private int devicePage;
        private sealed class DeviceChoice
        {
            public string Id;
            public string Name;
            public string Status;
            public bool Selected;
        }
        private List<DeviceChoice> connectionDevices = new List<DeviceChoice>();
        private string deviceListKey = "";
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
            pageSettings = settings;
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
                Win32.WS_CAPTION | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX | 0x00040000,
                S(120), S(80), S(520), S(500),
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
            brushBack = Win32.CreateSolidBrush(Win32.Rgb(245, 247, 251));
            brushCard = Win32.CreateSolidBrush(Win32.Rgb(255, 255, 255));
            brushBorder = Win32.CreateSolidBrush(Win32.Rgb(220, 227, 237));
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

            fontValue = Win32.CreateFontW(-S(24), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontHeading = Win32.CreateFontW(-S(14), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontTitle = Win32.CreateFontW(-S(20), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontBody = Win32.CreateFontW(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontSmall = Win32.CreateFontW(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontCaption = Win32.CreateFontW(-S(13), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
        }

        private void RecreateFonts()
        {
            Win32.DeleteObject(fontTitle);
            Win32.DeleteObject(fontValue);
            Win32.DeleteObject(fontHeading);
            Win32.DeleteObject(fontBody);
            Win32.DeleteObject(fontSmall);
            Win32.DeleteObject(fontCaption);
            fontValue = Win32.CreateFontW(-S(24), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontHeading = Win32.CreateFontW(-S(14), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontTitle = Win32.CreateFontW(-S(20), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontBody = Win32.CreateFontW(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontSmall = Win32.CreateFontW(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontCaption = Win32.CreateFontW(-S(13), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
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
                deviceSettings, "|", networkSettings, "|", firewallTask != null, "|", firewallMessage, "|", firewallStatus.Available, "|", firewallStatus.RuleReady, "|", devicePage, "|", deviceListKey, "|", hoverArea, "|", pressedArea, "|",
                dropdownOpen ? 1 : 0, "|", settingsMode ? 1 : 0, "|",
                controller.Devices.Count, "|", cachedOutputs.Count, "|", cachedPreferredName, "|",
                wifiMode ? 1 : 0, "|", (int)w.State, "|", w.Enabled ? 1 : 0, "|",
                w.Detail ?? "", "|", w.Remote ?? "", "|", w.DeviceName ?? "", "|",
                double.IsNaN(w.RttMs) ? "-1" : Math.Round(w.RttMs).ToString(), "|",
                w.BufferMs, "|", w.TargetMs, "|",
                double.IsNaN(w.LatencyMs) ? "-1" : Math.Round(w.LatencyMs).ToString(), "|",
                double.IsNaN(w.LossPercent) ? "-1" : Math.Round(w.LossPercent, 1).ToString(), "|",
                w.Received, "|", w.Missing, "|", w.Discontinuities, "|", w.Underruns, "|",
                w.HasAudibleSignal, "|", w.OutputActive, "|", w.Recovering, "|", w.Draining, "|",
                w.OutputName, "|", (int)(w.LastAudioAgeMs / 1000), "|", (int)(w.ControlAgeMs / 1000), "|",
                wifiDetails, "|", focusArea, "|", keyboardFocus, "|", modeSwitching, "|",
                DateTime.Now < noticeUntil ? notice : "");
        }

        private void OnTimer()
        {
            RefreshOutputCache(false);
            RefreshConnectionDevices();
            if (firewallTask != null && firewallTask.IsCompleted)
            {
                try
                {
                    FirewallResult result = firewallTask.GetAwaiter().GetResult();
                    if (result.Status != null) firewallStatus = result.Status;
                    firewallMessage = result.Message;
                }
                catch { firewallMessage = "操作失败，请重新检测。"; }
                firewallTask = null;
                RequestRepaint(true);
            }
            RequestRepaint(false);
        }

        private void BeginFirewallOperation(int area)
        {
            if (firewallTask != null) return;
            int port = wifi.Port;
            IntPtr owner = hwnd;
            firewallChanging = area != AreaFirewallCheck;
            log.Info("Ui.FirewallRequested", ("operation", area == AreaFirewallCheck ? "query" : area == AreaFirewallEnable ? "enable" : "disable"), ("port", port));
            firewallMessage = null;
            firewallTask = area == AreaFirewallCheck
                ? Task.Run(() => new FirewallResult { Status = WifiFirewall.Query(port) })
                : Task.Run(() => WifiFirewall.RequestChange(area == AreaFirewallEnable, port, owner));
            RequestRepaint(true);
        }

        private void RefreshConnectionDevices()
        {
            var choices = new List<DeviceChoice>();
            if (wifiMode)
            {
                foreach (WifiPeer peer in wifi.ListPeers())
                    choices.Add(new DeviceChoice { Id = peer.Id, Name = peer.Name, Selected = peer.Selected || peer.Active,
                        Status = peer.Selected ? "已选择" : peer.Active ? "当前设备" : peer.Recent ? "近期活动" : "未确认在线" });
            }
            else
            {
                SinkSnapshot snapshot = controller.Current;
                foreach (SinkDevice device in controller.Devices)
                {
                    bool active = device.Id == snapshot.DeviceId;
                    bool connected = active && (snapshot.State == SinkState.LinkReady || snapshot.State == SinkState.Flowing);
                    bool preferred = device.Id == pageSettings.DeviceId;
                    choices.Add(new DeviceChoice { Id = device.Id, Name = device.Name, Selected = preferred,
                        Status = active && snapshot.State == SinkState.Busy ? "连接中" : connected ? "已连接" : active && snapshot.State == SinkState.Armed ? "等待音频" : preferred ? "首选设备" : "已配对" });
                }
            }
            connectionDevices = choices;
            devicePage = Math.Clamp(devicePage, 0, Math.Max(0, (choices.Count - 1) / MaxDeviceRows));
            deviceListKey = string.Join("|", choices.ConvertAll(d => d.Id + d.Name + d.Status + d.Selected));
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
                cachedPreferredName = preferred ?? "跟随电脑默认输出";
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
            public int DeviceListTop;
            public int VisibleRows;
            public int OutputLabelTop;
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
            public Win32.RECT DetailsRect;
            public Win32.RECT CopyAddressRect;
            public Win32.RECT Buffer80Rect;
            public Win32.RECT Buffer120Rect;
            public Win32.RECT Buffer160Rect;
        }

        private Metrics ComputeMetrics()
        {
            Win32.RECT client;
            Win32.GetClientRect(hwnd, out client);
            int width = client.Right - client.Left, height = client.Bottom - client.Top;
            Metrics m = new Metrics();
            m.Width = width; m.Height = height;
            m.ContentWidth = Math.Max(S(300), Math.Min(width - S(PadX) * 2, S(680)));
            m.Left = (width - m.ContentWidth) / 2;
            int right = m.Left + m.ContentWidth;
            m.SettingsRect = Rect(right - S(56), S(10), right, S(42));
            m.ModeWifiRect = Rect(m.SettingsRect.Left - S(70), S(10), m.SettingsRect.Left - S(10), S(42));
            m.ModeBluetoothRect = Rect(m.ModeWifiRect.Left - S(62), S(10), m.ModeWifiRect.Left - S(6), S(42));
            m.DeviceListTop = S(170);
            m.VisibleRows = Math.Min(MaxDeviceRows, Math.Max(0, connectionDevices.Count - devicePage * MaxDeviceRows));
            m.ButtonTop = S(246);
            m.ButtonGap = S(8);
            m.ButtonWidth = (m.ContentWidth - m.ButtonGap) / 2;
            int x = m.Left;
            m.ConnectRect = Rect(x, m.ButtonTop, x + m.ButtonWidth, m.ButtonTop + S(34));
            x = m.ConnectRect.Right + m.ButtonGap;
            m.DisconnectRect = Rect(x, m.ButtonTop, x + m.ButtonWidth, m.ButtonTop + S(34));
            m.RefreshRect = Rect(m.Left, S(350), m.Left + S(118), S(382));
            m.BackRect = Rect(m.Left, S(60), m.Left + S(62), S(90));
            m.LogsButtonRect = Rect(m.Left, S(412), m.Left + S(140), S(444));
            m.Buffer80Rect = Rect(m.Left + S(12), S(336), m.Left + S(98), S(368));
            m.Buffer120Rect = Rect(m.Left + S(106), S(336), m.Left + S(192), S(368));
            m.Buffer160Rect = Rect(m.Left + S(200), S(336), m.Left + S(286), S(368));
            m.DetailsRect = Rect(right - S(84), S(328), right, S(358));
            m.CopyAddressRect = Rect(m.Left + S(174), S(328), m.Left + S(222), S(358));
            m.OutputLabelTop = S(112);
            m.OutputRect = Rect(m.Left + S(12), S(134), right - S(12), S(168));
            return m;
        }

        private Win32.RECT SettingsControlRect(Metrics m, int area)
        {
            int right = m.Left + m.ContentWidth;
            switch (area)
            {
                case AreaSettingsNetwork: return Rect(m.Left + S(268), S(60), m.Left + S(340), S(90));
                case AreaFirewallEnable: return Rect(m.Left, S(240), m.Left + (m.ContentWidth - S(8)) / 2, S(274));
                case AreaFirewallDisable: return Rect(m.Left + (m.ContentWidth + S(8)) / 2, S(240), right, S(274));
                case AreaFirewallCheck: return Rect(m.Left, S(288), m.Left + S(118), S(320));
                case AreaSettingsGeneral: return Rect(m.Left + S(76), S(60), m.Left + S(148), S(90));
                case AreaSettingsDevices: return Rect(m.Left + S(156), S(60), m.Left + S(260), S(90));
                case AreaDeviceBluetooth: return Rect(m.Left, S(104), m.Left + S(88), S(136));
                case AreaDeviceWifi: return Rect(m.Left + S(96), S(104), m.Left + S(184), S(136));
                case AreaPreviousPage: return Rect(right - S(150), S(350), right - S(80), S(382));
                default: return Rect(right - S(70), S(350), right, S(382));
            }
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
            if (modeSwitching) return AreaNone;
            if (Contains(m.ModeBluetoothRect, x, y)) return AreaModeBluetooth;
            if (Contains(m.ModeWifiRect, x, y)) return AreaModeWifi;
            if (Contains(m.SettingsRect, x, y)) return AreaSettings;
            if (dropdownOpen)
            {
                ComputeDropdown(m);
                if (Contains(dropdownRect, x, y))
                {
                    int i = (y - dropdownRect.Top - S(4)) / S(DropdownRowHeight);
                    return i >= 0 && i < dropdownRows ? AreaDropdownBase + i : AreaNone;
                }
            }
            if (settingsMode)
            {
                if (Contains(m.BackRect, x, y)) return AreaBack;
                foreach (int tab in new[] { AreaSettingsGeneral, AreaSettingsDevices, AreaSettingsNetwork })
                    if (Contains(SettingsControlRect(m, tab), x, y)) return tab;
                if (networkSettings)
                {
                    foreach (int button in new[] { AreaFirewallEnable, AreaFirewallDisable, AreaFirewallCheck })
                        if (Contains(SettingsControlRect(m, button), x, y)) return IsEnabled(button) ? button : AreaNone;
                    return AreaNone;
                }
                if (deviceSettings)
                {
                    foreach (int button in new[] { AreaDeviceBluetooth, AreaDeviceWifi, AreaPreviousPage, AreaNextPage })
                        if (Contains(SettingsControlRect(m, button), x, y)) return IsEnabled(button) ? button : AreaNone;
                    if (Contains(m.RefreshRect, x, y)) return AreaRefresh;
                    if (x >= m.Left && x < m.Left + m.ContentWidth && y >= m.DeviceListTop && y < m.DeviceListTop + m.VisibleRows * S(DeviceRowHeight))
                    {
                        int row = (y - m.DeviceListTop) / S(DeviceRowHeight);
                        return IsEnabled(AreaDeviceBase + row) ? AreaDeviceBase + row : AreaNone;
                    }
                    return AreaNone;
                }
                if (Contains(m.OutputRect, x, y)) return IsEnabled(AreaOutput) ? AreaOutput : AreaNone;
                if (Contains(m.LogsButtonRect, x, y)) return AreaOpenLogs;
                if (Contains(Rect(m.Left + S(8), S(174), m.Left + m.ContentWidth - S(8), S(222)), x, y)) return AreaAutoStart;
                if (Contains(Rect(m.Left + S(8), S(250), m.Left + m.ContentWidth - S(8), S(298)), x, y)) return AreaAutoConnect;
                if (Contains(m.Buffer80Rect, x, y)) return AreaBuffer80;
                if (Contains(m.Buffer120Rect, x, y)) return AreaBuffer120;
                if (Contains(m.Buffer160Rect, x, y)) return AreaBuffer160;
                return AreaNone;
            }
            if (wifiMode)
            {
                if (wifi.Current.State == SinkState.Faulted && Contains(RetryRect(m), x, y)) return AreaRetryWifi;
                if (Contains(m.DetailsRect, x, y)) return AreaDetails;
                if (Contains(m.CopyAddressRect, x, y)) return AreaCopyAddress;
                return AreaNone;
            }
            if (Contains(m.ConnectRect, x, y)) return IsEnabled(AreaConnect) ? AreaConnect : AreaNone;
            if (Contains(m.DisconnectRect, x, y)) return IsEnabled(AreaDisconnect) ? AreaDisconnect : AreaNone;
            return AreaNone;
        }


        private bool IsEnabled(int area)
        {
            if (modeSwitching) return false;
            if (area == AreaOutput) return settingsMode && !deviceSettings && !networkSettings && cachedOutputs.Count > 0;
            if (area == AreaFirewallEnable || area == AreaFirewallCheck) return firewallTask == null;
            if (area == AreaFirewallDisable) return firewallTask == null && firewallStatus.Available && firewallStatus.RuleExists;
            if (area == AreaPreviousPage) return devicePage > 0;
            if (area == AreaNextPage) return (devicePage + 1) * MaxDeviceRows < connectionDevices.Count;
            if (area >= AreaDeviceBase && area < AreaDropdownBase)
                return wifiMode || controller.Current.State != SinkState.Busy;
            if (area == AreaConnect) return controller.Devices.Count > 0 && controller.Current.State != SinkState.Busy;
            if (area == AreaDisconnect) return controller.Current.State != SinkState.Idle && controller.Current.State != SinkState.Faulted;
            return true;
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
            focusArea = pressedArea;
            keyboardFocus = false;
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
                    bool changed = controller.SetPreferredRender(pick.Id, pick.Name);
                    if (changed)
                    {
                        wifi.RefreshOutput();
                        cachedPreferredName = pick.Name;
                        ShowNotice("播放设备已更新");
                    }
                    else ShowNotice("播放设备切换失败，请重试");
                    lastOutputRefresh = DateTime.MinValue;
                    log.Info("Ui.OutputPicked", ("endpoint", pick.Name));
                }
                dropdownOpen = false;
                focusArea = AreaOutput;
                return;
            }

            switch (area)
            {
                case AreaSettingsGeneral:
                case AreaSettingsDevices:
                case AreaSettingsNetwork:
                    deviceSettings = area == AreaSettingsDevices;
                    networkSettings = area == AreaSettingsNetwork;
                    if (networkSettings) BeginFirewallOperation(AreaFirewallCheck);
                    dropdownOpen = false;
                    RefreshConnectionDevices();
                    return;
                case AreaFirewallEnable:
                case AreaFirewallDisable:
                case AreaFirewallCheck:
                    BeginFirewallOperation(area);
                    return;
                case AreaDeviceBluetooth:
                case AreaDeviceWifi:
                    SetMode(area == AreaDeviceWifi);
                    devicePage = 0;
                    RefreshConnectionDevices();
                    return;
                case AreaPreviousPage:
                case AreaNextPage:
                    devicePage += area == AreaNextPage ? 1 : -1;
                    RefreshConnectionDevices();
                    return;
                case AreaModeBluetooth:
                    settingsMode = false;
                    SetMode(false);
                    return;

                case AreaModeWifi:
                    settingsMode = false;
                    SetMode(true);
                    return;

                case AreaOutput:
                    dropdownOpen = !dropdownOpen;
                    RefreshOutputCache(true);
                    if (keyboardFocus && dropdownOpen && cachedOutputs.Count > 0) focusArea = AreaDropdownBase;
                    return;

                case AreaSettings:
                    settingsMode = true;
                    pageSettings = Settings.Load();
                    autoStartEnabled = AutoStart.IsEnabled();
                    if (networkSettings) BeginFirewallOperation(AreaFirewallCheck);
                    RefreshConnectionDevices();
                    focusArea = keyboardFocus ? AreaBack : AreaSettings;
                    dropdownOpen = false;
                    log.Info("Ui.SettingsOpened");
                    return;

                case AreaBack:
                    settingsMode = false;
                    focusArea = AreaSettings;
                    return;

                case AreaAutoStart:
                {
                    bool enabled = !autoStartEnabled;
                    AutoStart.SetEnabled(enabled);
                    autoStartEnabled = AutoStart.IsEnabled();
                    ShowNotice(autoStartEnabled == enabled ? "设置已保存" : "设置未能保存，请重试");
                    log.Info("Ui.AutoStartToggled", ("enabled", enabled));
                    return;
                }

                case AreaAutoConnect:
                {
                    Settings settings = Settings.Load();
                    settings.AutoConnect = !settings.AutoConnect;
                    settings.Save();
                    pageSettings = settings;
                    ShowNotice("蓝牙连接偏好已保存");
                    log.Info("Ui.AutoConnectToggled", ("enabled", settings.AutoConnect));
                    return;
                }

                case AreaRetryWifi:
                    wifi.Stop("retry");
                    ShowNotice(wifi.Start() ? "正在重新准备 Wi-Fi" : "无法开启 Wi-Fi，请检查端口占用");
                    return;
                case AreaDetails:
                    wifiDetails = !wifiDetails;
                    return;
                case AreaCopyAddress:
                    ShowNotice(CopyAddress(wifi.Current.LocalAddress) ? "电脑地址已复制" : "复制失败，请重试");
                    return;
                case AreaBuffer80:
                case AreaBuffer120:
                case AreaBuffer160:
                {
                    int value = area == AreaBuffer80 ? 80 : area == AreaBuffer120 ? 120 : 160;
                    Settings preferences = Settings.Load();
                    preferences.BufferBaseMs = value;
                    preferences.BufferMaxMs = Math.Max(value, preferences.BufferMaxMs);
                    preferences.Save(); pageSettings = preferences;
                    wifi.SetBufferBase(value);
                    ShowNotice("Wi-Fi 缓冲偏好已保存");
                    return;
                }

                case AreaOpenLogs:
                    OpenLogDirectory();
                    return;

                case AreaConnect:
                {
                    string deviceId = string.IsNullOrEmpty(pageSettings.DeviceId) ? null : pageSettings.DeviceId;
                    controller.RequestConnect(deviceId);
                    log.Info("Ui.ConnectClicked", ("device", deviceId ?? "(默认)"));
                    return;
                }

                case AreaDisconnect:
                    controller.RequestDisconnect();
                    log.Info("Ui.DisconnectClicked");
                    return;

                case AreaRefresh:
                    if (wifiMode) ShowNotice(wifi.RefreshDiscovery() ? "已广播电脑信息，请在手机端选择这台电脑" : "广播失败，请检查 Wi-Fi 连接");
                    else { controller.RequestRefreshDevices(); ShowNotice("正在刷新已配对设备"); }
                    RefreshConnectionDevices();
                    lastOutputRefresh = DateTime.MinValue;
                    return;
            }

            if (area >= AreaDeviceBase && area < AreaDropdownBase)
            {
                int index = devicePage * MaxDeviceRows + area - AreaDeviceBase;
                if (index >= 0 && index < connectionDevices.Count)
                {
                    DeviceChoice pick = connectionDevices[index];
                    if (wifiMode)
                    {
                        ShowNotice(wifi.SelectPeer(pick.Id) ? "已选择手机，请在手机端开始发送" : "设备不可用，请刷新后重试");
                    }
                    else
                    {
                        Settings preferences = Settings.Load();
                        preferences.DeviceId = pick.Id;
                        preferences.Save(); pageSettings = preferences;
                        controller.RequestConnect(pick.Id);
                        ShowNotice("正在连接 " + pick.Name);
                    }
                    RefreshConnectionDevices();
                    log.Info("Ui.DevicePicked", ("mode", wifiMode ? "wifi" : "bluetooth"), ("device", pick.Name));
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

            modeSwitching = true;
            RequestRepaint(true);
            Win32.UpdateWindow(hwnd);
            try
            {
                if (useWifi)
                {
                    // 先绑定 Wi-Fi 端口；失败则保持蓝牙不动（协议 §8.1）。
                    if (!wifi.Start())
                    {
                        ShowNotice("无法开启 Wi-Fi，请检查端口占用");
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
                        string deviceId = string.IsNullOrEmpty(settings.DeviceId) ? null : settings.DeviceId;
                        controller.RequestConnect(deviceId);
                    }
                }

                dropdownOpen = false;
            }
            finally { modeSwitching = false; RefreshConnectionDevices(); RequestRepaint(true); }
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
            if (modeSwitching) return;
            if (vk == 0x1B)
            {
                if (dropdownOpen) { dropdownOpen = false; focusArea = AreaOutput; }
                else if (settingsMode) { settingsMode = false; focusArea = AreaSettings; }
                else if (wifiDetails) { wifiDetails = false; focusArea = AreaDetails; }
                RequestRepaint(true); return;
            }
            if (dropdownOpen && (vk == 0x26 || vk == 0x28))
            {
                int index = focusArea - AreaDropdownBase;
                int direction = vk == 0x26 ? -1 : 1;
                if (index < 0 || index >= dropdownRows) index = direction > 0 ? -1 : 0;
                focusArea = AreaDropdownBase + (index + direction + dropdownRows) % dropdownRows;
                keyboardFocus = true; RequestRepaint(true); return;
            }
            if (vk == 0x09)
            {
                List<int> areas = new List<int> { AreaModeBluetooth, AreaModeWifi, AreaSettings };
                if (dropdownOpen)
                {
                    areas.Clear();
                    for (int i = 0; i < dropdownRows; i++) areas.Add(AreaDropdownBase + i);
                }
                else if (settingsMode)
                {
                    areas.AddRange(new[] { AreaBack, AreaSettingsGeneral, AreaSettingsDevices, AreaSettingsNetwork });
                    if (networkSettings) areas.AddRange(new[] { AreaFirewallEnable, AreaFirewallDisable, AreaFirewallCheck });
                    else if (deviceSettings)
                    {
                        areas.AddRange(new[] { AreaDeviceBluetooth, AreaDeviceWifi });
                        for (int i = 0; i < ComputeMetrics().VisibleRows; i++) areas.Add(AreaDeviceBase + i);
                        areas.AddRange(new[] { AreaRefresh, AreaPreviousPage, AreaNextPage });
                    }
                    else areas.AddRange(new[] { AreaOutput, AreaAutoStart, AreaAutoConnect, AreaBuffer80, AreaBuffer120, AreaBuffer160, AreaOpenLogs });
                }
                else if (wifiMode)
                {
                    if (wifi.Current.State == SinkState.Faulted) areas.Add(AreaRetryWifi);
                    areas.AddRange(new[] { AreaCopyAddress, AreaDetails });
                }
                else
                {
                    areas.AddRange(new[] { AreaConnect, AreaDisconnect });
                }
                areas.RemoveAll(a => !IsEnabled(a));
                int direction = Win32.GetKeyState(0x10) < 0 ? -1 : 1;
                int current = areas.IndexOf(focusArea);
                focusArea = areas[current < 0 ? (direction > 0 ? 0 : areas.Count - 1) : (current + direction + areas.Count) % areas.Count];
                keyboardFocus = true; RequestRepaint(true); return;
            }
            if ((vk == 0x0D || vk == 0x20) && keyboardFocus && IsEnabled(focusArea))
            {
                Activate(focusArea); RequestRepaint(true);
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
                case Win32.WM_SETCURSOR:
                    if ((lParam.ToInt64() & 0xFFFF) == 1)
                    {
                        Win32.SetCursor(Win32.LoadCursor(IntPtr.Zero, hoverArea == AreaNone ? Win32.IDC_ARROW : Win32.IDC_HAND));
                        return new IntPtr(1);
                    }
                    break;
                case 0x0024: // WM_GETMINMAXINFO: native minimum track size, in current DPI.
                    Win32.MINMAXINFO limits = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.MINMAXINFO>(lParam);
                    limits.ptMinTrackSize.X = S(480); limits.ptMinTrackSize.Y = S(500);
                    System.Runtime.InteropServices.Marshal.StructureToPtr(limits, lParam, false);
                    return IntPtr.Zero;
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
                    DrawWifiPage(hdcMem, m, wifiSnapshot);
                }
                else
                {
                    SinkSnapshot snapshot = controller.Current;
                    DrawHeader(hdcMem, m, snapshot);
                    DrawBluetoothPage(hdcMem, m, snapshot);
                }

                DrawModeTabs(hdcMem, m);
                if (settingsMode && dropdownOpen) DrawDropdown(hdcMem);
                DrawKeyboardFocus(hdcMem, m);
                if (DateTime.Now < noticeUntil)
                    Text(hdcMem, notice, fontBody, Win32.Rgb(0, 93, 165), Rect(m.Left, m.Height - S(24), m.Left + m.ContentWidth, m.Height - S(4)));
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
            string title = DescribeState(snapshot.State);
            string detail = string.IsNullOrEmpty(snapshot.DeviceName) ? "前往设置选择手机，再点击连接。" : snapshot.DeviceName;
            if (snapshot.State == SinkState.Idle) detail = "点击连接；更换手机请前往设置。";
            else if (snapshot.State == SinkState.Armed) detail = "连接已就绪，请在手机上播放音频。";
            else if (snapshot.State == SinkState.LinkReady) detail = "已建立音频链路，正在等待声音。";
            else if (snapshot.State == SinkState.Faulted) detail = snapshot.Detail ?? "请重试连接。";
            ConnectionPresentation view = new ConnectionPresentation
            {
                Title = title, Description = detail,
                Tone = snapshot.State == SinkState.Flowing ? ConnectionTone.Active :
                    snapshot.State == SinkState.Faulted ? ConnectionTone.Error :
                    snapshot.State == SinkState.Busy || snapshot.State == SinkState.Backoff ? ConnectionTone.Warning : ConnectionTone.Ready,
                PhoneReady = snapshot.State == SinkState.Armed || snapshot.State == SinkState.LinkReady || snapshot.State == SinkState.Flowing,
                StreamReady = snapshot.State == SinkState.LinkReady || snapshot.State == SinkState.Flowing,
                OutputReady = snapshot.State == SinkState.Flowing
            };
            DrawStatusCard(hdc, m, view);
        }

        private void DrawHeader(IntPtr hdc, Metrics m, WifiSnapshot snapshot)
        {
            DrawStatusCard(hdc, m, ConnectionPresentation.Wifi(snapshot));
        }

        private void DrawStatusCard(IntPtr hdc, Metrics m, ConnectionPresentation view)
        {
            if (modeSwitching) { view.Title = "正在切换模式"; view.Description = "正在准备连接，请稍候。"; view.Tone = ConnectionTone.Ready; }
            int right = m.Left + m.ContentWidth;
            Dot(hdc, m.Left + S(4), S(78), S(8), ToneColor(view.Tone));
            bool retry = wifiMode && !settingsMode && wifi.Current.State == SinkState.Faulted;
            Text(hdc, view.Title, fontTitle, Win32.Rgb(23, 40, 63), Rect(m.Left + S(20), S(66), right - S(retry ? 88 : 4), S(94)));
            if (retry) DrawButton(hdc, RetryRect(m), "重试", AreaRetryWifi, false);
            Text(hdc, view.Description, fontBody, Win32.Rgb(80, 94, 114), Rect(m.Left + S(4), S(98), right - S(4), S(120)));
            int col = (m.ContentWidth - S(8)) / 3;
            Stage(hdc, m.Left + S(4), S(124), col, view.PhoneReady, "手机已连接", "等待手机");
            Stage(hdc, m.Left + S(4) + col, S(124), col, view.StreamReady, "音频已接收", "等待音频");
            Stage(hdc, m.Left + S(4) + col * 2, S(124), col, view.OutputReady, "输出已就绪", "等待播放");
        }

        private void Stage(IntPtr hdc, int left, int top, int width, bool ready, string active, string waiting)
        {
            Dot(hdc, left, top + S(5), S(5), ready ? Win32.Rgb(0, 112, 194) : Win32.Rgb(165, 177, 193));
            Text(hdc, ready ? active : waiting, fontSmall, ready ? Win32.Rgb(34, 76, 114) : Win32.Rgb(100, 112, 129),
                Rect(left + S(12), top, left + width - S(4), top + S(18)));
        }

        private void DrawModeTabs(IntPtr hdc, Metrics m)
        {
            Text(hdc, "BlueAudioSink", fontHeading, Win32.Rgb(26, 45, 70), Rect(m.Left, S(10), m.ModeBluetoothRect.Left - S(10), S(42)));
            DrawModeTab(hdc, m.ModeBluetoothRect, "蓝牙", !settingsMode && !wifiMode, AreaModeBluetooth);
            DrawModeTab(hdc, m.ModeWifiRect, "Wi-Fi", !settingsMode && wifiMode, AreaModeWifi);
            DrawModeTab(hdc, m.SettingsRect, "设置", settingsMode, AreaSettings);
            Win32.RECT line = Rect(m.Left, S(52), m.Left + m.ContentWidth, S(53));
            Win32.FillRect(hdc, ref line, brushBorder);
        }

        private void DrawModeTab(IntPtr hdc, Win32.RECT rect, string text, bool active, int area)
        {
            bool hover = hoverArea == area;
            bool pressed = pressedArea == area;
            IntPtr fill = active ? (pressed ? brushAccentPress : hover ? brushAccentHover : brushAccent)
                : pressed ? brushBtnPress : hover ? brushBtnHover : brushCard;
            RoundFill(hdc, rect, S(8), fill);
            if (!active) RoundBorder(hdc, rect, S(8), hover ? brushAccent : brushBorder);
            Text(hdc, text, fontBody, active ? Win32.Rgb(255, 255, 255) : Win32.Rgb(47, 64, 86), rect, true);
        }

        private void DrawWifiPage(IntPtr hdc, Metrics m, WifiSnapshot snapshot)
        {
            int left = m.Left, right = left + m.ContentWidth, half = m.ContentWidth / 2;
            Win32.RECT devices = Rect(left, S(154), right, S(230));
            RoundFill(hdc, devices, S(8), brushCard); RoundBorder(hdc, devices, S(8), brushBorder);
            Text(hdc, "发送手机", fontSmall, Win32.Rgb(88, 102, 122), Rect(left + S(12), S(162), left + half - S(8), S(180)));
            string phone = string.IsNullOrEmpty(snapshot.DeviceName) ? (string.IsNullOrEmpty(snapshot.Remote) ? "尚未连接" : "Android 手机") : snapshot.DeviceName;
            Text(hdc, phone, fontHeading, Win32.Rgb(23, 40, 63), Rect(left + S(12), S(184), left + half - S(8), S(208)));
            string address = snapshot.Remote;
            int colon = string.IsNullOrEmpty(address) ? -1 : address.LastIndexOf(':');
            if (colon > 0) address = address.Substring(0, colon);
            Text(hdc, address ?? "等待手机发送端", fontSmall, Win32.Rgb(88, 102, 122), Rect(left + S(12), S(208), left + half - S(8), S(226)));
            Text(hdc, "播放到", fontSmall, Win32.Rgb(88, 102, 122), Rect(left + half + S(8), S(162), right - S(12), S(180)));
            string destination = snapshot.OutputName ?? cachedPreferredName;
            Win32.RECT destinationRect = Rect(left + half + S(8), S(184), right - S(12), S(226));
            Win32.SelectObject(hdc, fontBody); Win32.SetTextColor(hdc, Win32.Rgb(23, 40, 63));
            Win32.DrawText(hdc, destination, -1, ref destinationRect, Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_END_ELLIPSIS);
            Win32.RECT stats = Rect(left, S(242), right, S(316));
            RoundFill(hdc, stats, S(8), brushCard); RoundBorder(hdc, stats, S(8), brushBorder);
            int col = m.ContentWidth / 3;
            MetricTile(hdc, Rect(left, S(242), left + col, S(316)), "网络延迟", FormatMs(snapshot.RttMs), "往返时间", false);
            MetricTile(hdc, Rect(left + col, S(242), left + col * 2, S(316)), "音频缓冲", snapshot.BufferMs + " ms", "预缓冲 " + snapshot.TargetMs + " ms", false);
            MetricTile(hdc, Rect(left + col * 2, S(242), right, S(316)), "丢包率", FormatPercent(snapshot.LossPercent), "最近 10 秒", snapshot.LossPercent > 0.5);
            for (int i = 1; i < 3; i++)
            {
                Win32.RECT divider = Rect(left + col * i, S(254), left + col * i + 1, S(304));
                Win32.FillRect(hdc, ref divider, brushBorder);
            }
            Text(hdc, "电脑地址  " + (snapshot.LocalAddress ?? "—"), fontSmall, Win32.Rgb(80, 94, 114), Rect(left, S(328), m.CopyAddressRect.Left - S(6), S(358)));
            DrawButton(hdc, m.CopyAddressRect, "复制", AreaCopyAddress, false);
            DrawButton(hdc, m.DetailsRect, wifiDetails ? "收起详情" : "连接详情", AreaDetails, false);
            if (wifiDetails)
            {
                Win32.RECT panel = Rect(left, S(370), right, Math.Min(m.Height - S(36), S(430)));
                RoundFill(hdc, panel, S(8), brushCard); RoundBorder(hdc, panel, S(8), brushBorder);
                Text(hdc, "手机端点 " + (snapshot.Remote ?? "未连接") + "  ·  端口 " + snapshot.Port,
                    fontSmall, Win32.Rgb(80, 94, 114), Rect(left + S(12), panel.Top + S(9), right - S(12), panel.Top + S(27)));
                Text(hdc, "收到 " + snapshot.Received + "  ·  缺失 " + snapshot.Missing + "  ·  迟到 " + snapshot.Late + "  ·  欠载 " + snapshot.Underruns + " 次",
                    fontSmall, Win32.Rgb(80, 94, 114), Rect(left + S(12), panel.Top + S(31), right - S(12), panel.Top + S(49)));
            }
        }

        private void MetricTile(IntPtr hdc, Win32.RECT rect, string label, string value, string hint, bool warning)
        {
            Text(hdc, label, fontSmall, Win32.Rgb(88, 102, 122), Rect(rect.Left + S(12), rect.Top + S(6), rect.Right - S(8), rect.Top + S(24)));
            Text(hdc, value, fontValue, warning ? Win32.Rgb(170, 85, 0) : Win32.Rgb(23, 40, 63), Rect(rect.Left + S(12), rect.Top + S(24), rect.Right - S(8), rect.Top + S(56)));
            Text(hdc, hint, fontSmall, Win32.Rgb(88, 102, 122), Rect(rect.Left + S(12), rect.Top + S(56), rect.Right - S(8), rect.Bottom - S(2)));
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

        private void DrawBluetoothPage(IntPtr hdc, Metrics m, SinkSnapshot snapshot)
        {
            int right = m.Left + m.ContentWidth, half = m.ContentWidth / 2;
            Win32.RECT card = Rect(m.Left, S(154), right, S(230));
            RoundFill(hdc, card, S(8), brushCard); RoundBorder(hdc, card, S(8), brushBorder);
            Text(hdc, "发送手机", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left + S(12), S(162), m.Left + half, S(180)));
            Text(hdc, snapshot.DeviceName ?? "尚未选择", fontHeading, Win32.Rgb(23, 40, 63), Rect(m.Left + S(12), S(184), m.Left + half - S(8), S(208)));
            Text(hdc, "播放到", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left + half + S(8), S(162), right - S(12), S(180)));
            Text(hdc, cachedPreferredName, fontBody, Win32.Rgb(23, 40, 63), Rect(m.Left + half + S(8), S(184), right - S(12), S(208)));
            DrawButton(hdc, m.ConnectRect, snapshot.State == SinkState.Flowing ? "重新连接" : "连接", AreaConnect, true);
            DrawButton(hdc, m.DisconnectRect, "断开", AreaDisconnect, false);
            Text(hdc, "更换手机或播放设备，请前往设置。", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left, S(292), right, S(316)));
        }

        private void DrawDeviceSettings(IntPtr hdc, Metrics m)
        {
            DrawModeTab(hdc, SettingsControlRect(m, AreaDeviceBluetooth), "蓝牙手机", !wifiMode, AreaDeviceBluetooth);
            DrawModeTab(hdc, SettingsControlRect(m, AreaDeviceWifi), "Wi-Fi 手机", wifiMode, AreaDeviceWifi);
            Text(hdc, wifiMode ? "选择接收音频的手机" : "选择后立即连接并记住该手机", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left, S(142), m.Left + m.ContentWidth, S(164)));
            for (int rowIndex = 0; rowIndex < m.VisibleRows; rowIndex++)
            {
                DeviceChoice choice = connectionDevices[devicePage * MaxDeviceRows + rowIndex];
                int top = m.DeviceListTop + rowIndex * S(DeviceRowHeight);
                Win32.RECT row = Rect(m.Left, top, m.Left + m.ContentWidth, top + S(DeviceRowHeight) - S(2));
                if (choice.Selected || hoverArea == AreaDeviceBase + rowIndex)
                    RoundFill(hdc, row, S(6), choice.Selected ? brushRowSelected : brushRowHover);
                Text(hdc, choice.Name, fontBody, Win32.Rgb(23, 40, 63), Rect(row.Left + S(10), row.Top, row.Right - S(100), wifiMode ? row.Top + S(18) : row.Bottom));
                if (wifiMode) Text(hdc, choice.Id, fontSmall, Win32.Rgb(88, 102, 122), Rect(row.Left + S(10), row.Top + S(18), row.Right - S(100), row.Bottom));
                Text(hdc, choice.Status, fontSmall, choice.Selected ? Win32.Rgb(0, 100, 170) : Win32.Rgb(88, 102, 122), Rect(row.Right - S(96), row.Top, row.Right - S(8), row.Bottom), true);
            }
            if (connectionDevices.Count == 0)
                Text(hdc, wifiMode ? "尚未发现手机，请在发送端选择这台电脑。" : "暂无手机，请先在 Windows 蓝牙设置中配对。", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left, m.DeviceListTop, m.Left + m.ContentWidth, m.DeviceListTop + S(34)));
            DrawButton(hdc, m.RefreshRect, wifiMode ? "广播发现" : "刷新设备", AreaRefresh, false);
            Text(hdc, (devicePage + 1) + " / " + Math.Max(1, (connectionDevices.Count + MaxDeviceRows - 1) / MaxDeviceRows), fontSmall, Win32.Rgb(88, 102, 122), Rect(m.RefreshRect.Right + S(8), S(350), SettingsControlRect(m, AreaPreviousPage).Left - S(8), S(382)), true);
            DrawButton(hdc, SettingsControlRect(m, AreaPreviousPage), "上一页", AreaPreviousPage, false);
            DrawButton(hdc, SettingsControlRect(m, AreaNextPage), "下一页", AreaNextPage, false);
            Text(hdc, wifiMode ? "选择后仅接收该手机，其他手机不会抢占播放。" : "可切换多台已配对手机，同一时间连接一台。", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left, S(394), m.Left + m.ContentWidth, S(414)));
            Text(hdc, wifiMode ? "仍需在手机端授权并开始发送；离开 Wi-Fi 后重置选择。" : "播放设备在「常规」中统一调整。", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left, S(416), m.Left + m.ContentWidth, S(436)));
        }

        private void DrawOutputSection(IntPtr hdc, Metrics m)
        {
            DrawCaption(hdc, "播放设备", m.Left, m.OutputLabelTop);

            bool hover = IsEnabled(AreaOutput) && hoverArea == AreaOutput;
            bool pressed = IsEnabled(AreaOutput) && pressedArea == AreaOutput;
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

                Win32.SelectObject(hdc, fontBody);
                Win32.SetTextColor(hdc, isCurrent ? Win32.Rgb(0, 100, 170) : Win32.Rgb(40, 40, 40));
                Win32.RECT textRect = Rect(row.Left + S(10), row.Top, row.Right - S(10), row.Bottom);
                Win32.DrawText(hdc, cachedOutputs[i].Name, -1, ref textRect,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
            }
        }

        /// <summary>
        /// 设置页。开关类功能用经典划钮（左=关，右=开，开启时槽变主色），
        /// 不再做成“可点进下一级”的列表项。
        /// </summary>
        private void DrawSettings(IntPtr hdc, Metrics m)
        {
            DrawButton(hdc, m.BackRect, "返回", AreaBack, false);
            DrawModeTab(hdc, SettingsControlRect(m, AreaSettingsGeneral), "常规", !deviceSettings && !networkSettings, AreaSettingsGeneral);
            DrawModeTab(hdc, SettingsControlRect(m, AreaSettingsDevices), "连接设备", deviceSettings, AreaSettingsDevices);
            DrawModeTab(hdc, SettingsControlRect(m, AreaSettingsNetwork), "网络", networkSettings, AreaSettingsNetwork);
            if (networkSettings) { DrawNetworkSettings(hdc, m); return; }
            if (deviceSettings) { DrawDeviceSettings(hdc, m); return; }
            Win32.RECT general = Rect(m.Left, S(104), m.Left + m.ContentWidth, S(232));
            RoundFill(hdc, general, S(8), brushCard); RoundBorder(hdc, general, S(8), brushBorder);
            Metrics outputLayout = m; outputLayout.Left += S(12); outputLayout.ContentWidth -= S(24);
            DrawOutputSection(hdc, outputLayout);
            DrawPreference(hdc, m, S(174), AreaAutoStart, "开机启动", "登录 Windows 后自动打开", autoStartEnabled);
            Win32.RECT connection = Rect(m.Left, S(244), m.Left + m.ContentWidth, S(400));
            RoundFill(hdc, connection, S(8), brushCard); RoundBorder(hdc, connection, S(8), brushBorder);
            DrawPreference(hdc, m, S(250), AreaAutoConnect, "蓝牙自动连接", "仅蓝牙模式生效", pageSettings.AutoConnect);
            Text(hdc, "Wi-Fi 预缓冲", fontBody, Win32.Rgb(23, 40, 63), Rect(m.Left + S(12), S(306), m.Left + S(154), S(328)));
            Text(hdc, "用于开始播放与断流恢复", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left + S(162), S(306), m.Left + m.ContentWidth - S(12), S(328)));
            DrawModeTab(hdc, m.Buffer80Rect, "80 ms", pageSettings.BufferBaseMs == 80, AreaBuffer80);
            DrawModeTab(hdc, m.Buffer120Rect, "120 ms", pageSettings.BufferBaseMs == 120, AreaBuffer120);
            DrawModeTab(hdc, m.Buffer160Rect, "160 ms", pageSettings.BufferBaseMs == 160, AreaBuffer160);
            Text(hdc, "缓冲越高越抗抖动，开始播放会稍晚。", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left + S(12), S(376), m.Left + m.ContentWidth - S(12), S(394)));
            DrawButton(hdc, m.LogsButtonRect, "打开日志目录", AreaOpenLogs, false);
            Text(hdc, "BlueAudioSink 0.1.0", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left + S(154), S(412), m.Left + m.ContentWidth, S(444)));
        }

        private void DrawNetworkSettings(IntPtr hdc, Metrics m)
        {
            int right = m.Left + m.ContentWidth;
            Win32.RECT card = Rect(m.Left, S(104), right, S(224));
            RoundFill(hdc, card, S(8), brushCard); RoundBorder(hdc, card, S(8), brushBorder);
            Text(hdc, "Wi-Fi 防火墙", fontHeading, Win32.Rgb(23, 40, 63), Rect(m.Left + S(12), S(114), right - S(12), S(140)));
            string state = firewallTask != null ? firewallChanging ? "等待 Windows 授权与配置…" : "正在检测…"
                : !firewallStatus.Available ? "无法读取防火墙状态" : firewallStatus.RuleReady ? "已开启局域网放行"
                : firewallStatus.RuleExists ? "放行规则需要修复" : "尚未添加本程序的放行规则";
            Text(hdc, state, fontBody, firewallStatus.RuleReady ? Win32.Rgb(20, 120, 75) : Win32.Rgb(23, 40, 63), Rect(m.Left + S(12), S(146), right - S(12), S(172)));
            Text(hdc, "仅放行本程序 · 专用网络 · 同一局域网", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left + S(12), S(180), right - S(12), S(198)));
            Text(hdc, "UDP " + WifiFirewall.Ports(wifi.Port) + "；移动软件后请重新开启。", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left + S(12), S(200), right - S(12), S(218)));
            DrawButton(hdc, SettingsControlRect(m, AreaFirewallEnable), firewallStatus.RuleReady ? "重新配置" : "开启放行", AreaFirewallEnable, true);
            DrawButton(hdc, SettingsControlRect(m, AreaFirewallDisable), "移除本程序规则", AreaFirewallDisable, false);
            DrawButton(hdc, SettingsControlRect(m, AreaFirewallCheck), "重新检测", AreaFirewallCheck, false);
            Text(hdc, "修改时需要管理员授权", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left + S(132), S(288), right, S(320)));
            string hint = firewallTask != null ? "正在处理，请稍候。" : !firewallStatus.Available ? firewallStatus.Error ?? "点击重新检测以读取状态。"
                : firewallStatus.InboundBlocked || firewallStatus.ModifyState != 0 ? "系统策略限制入站连接，请联系管理员。"
                : !firewallStatus.PrivateActive ? "未检测到专用网络，规则暂不生效。"
                : !firewallStatus.PrivateFirewallEnabled ? "专用网络防火墙当前已关闭。" : "请确认手机与电脑所在网络设为专用网络。";
            Text(hdc, hint, fontSmall, Win32.Rgb(151, 91, 22), Rect(m.Left, S(338), right, S(360)));
            Text(hdc, "不关闭防火墙，也不修改公共网络或其他规则。", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left, S(366), right, S(386)));
            Text(hdc, "阻断规则、其他防火墙或网络隔离仍可能影响连接。", fontSmall, Win32.Rgb(88, 102, 122), Rect(m.Left, S(390), right, S(410)));
            if (!string.IsNullOrEmpty(firewallMessage))
                Text(hdc, firewallMessage, fontSmall, Win32.Rgb(0, 93, 165), Rect(m.Left, S(420), right, S(444)));
        }

        private void DrawPreference(IntPtr hdc, Metrics m, int top, int area, string label, string hint, bool value)
        {
            Win32.RECT row = Rect(m.Left + S(8), top, m.Left + m.ContentWidth - S(8), top + S(48));
            if (hoverArea == area || pressedArea == area) RoundFill(hdc, row, S(6), pressedArea == area ? brushBtnPress : brushBtnHover);
            Text(hdc, label, fontBody, Win32.Rgb(23, 40, 63), Rect(row.Left + S(4), top, row.Right - S(72), top + S(24)));
            Text(hdc, hint, fontSmall, Win32.Rgb(88, 102, 122), Rect(row.Left + S(4), top + S(24), row.Right - S(72), top + S(44)));
            DrawSwitch(hdc, row.Right - S(8), top + S(24), value, hoverArea == area);
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
            bool enabled = IsEnabled(area);
            bool hover = enabled && hoverArea == area;
            bool pressed = enabled && pressedArea == area;

            IntPtr fill;
            int textColor;

            if (!enabled)
            {
                fill = brushBack; textColor = Win32.Rgb(142, 152, 167);
            }
            else if (primary)
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
            Win32.SetTextColor(hdc, Win32.Rgb(88, 102, 122));
            Win32.RECT rect = Rect(x, top, x + S(200), top + S(18));
            Win32.DrawText(hdc, text, -1, ref rect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
        }

        private void RoundFill(IntPtr hdc, Win32.RECT rect, int radius, IntPtr brush)
        {
            IntPtr old = Win32.SelectObject(hdc, brush);
            IntPtr pen = Win32.GetStockObject(8); // NULL_PEN, never delete stock objects.
            IntPtr oldPen = Win32.SelectObject(hdc, pen);
            Win32.RoundRect(hdc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius);
            Win32.SelectObject(hdc, oldPen);
            Win32.SelectObject(hdc, old);
        }

        private void RoundBorder(IntPtr hdc, Win32.RECT rect, int radius, IntPtr brush)
        {
            IntPtr pen = Win32.CreatePen(Win32.PS_SOLID, Math.Max(1, S(1)), brush == brushAccent ? Win32.Rgb(0, 120, 215) : Win32.Rgb(220, 227, 237));
            IntPtr oldPen = Win32.SelectObject(hdc, pen);
            IntPtr oldBrush = Win32.SelectObject(hdc, Win32.GetStockObject(Win32.HOLLOW_BRUSH));
            Win32.RoundRect(hdc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius);
            Win32.SelectObject(hdc, oldBrush);
            Win32.SelectObject(hdc, oldPen);
            Win32.DeleteObject(pen);
        }

        private void Text(IntPtr hdc, string value, IntPtr font, int color, Win32.RECT rect, bool center = false)
        {
            Win32.SelectObject(hdc, font); Win32.SetTextColor(hdc, color);
            Win32.DrawText(hdc, value ?? "—", -1, ref rect, (center ? Win32.DT_CENTER : Win32.DT_LEFT) | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
        }

        private void Dot(IntPtr hdc, int x, int y, int size, int color)
        {
            IntPtr brush = Win32.CreateSolidBrush(color), pen = Win32.GetStockObject(8);
            IntPtr oldBrush = Win32.SelectObject(hdc, brush), oldPen = Win32.SelectObject(hdc, pen);
            Win32.Ellipse(hdc, x, y, x + size, y + size);
            Win32.SelectObject(hdc, oldBrush); Win32.SelectObject(hdc, oldPen); Win32.DeleteObject(brush);
        }

        private static int ToneColor(ConnectionTone tone)
        {
            switch (tone)
            {
                case ConnectionTone.Active: return Win32.Rgb(20, 139, 85);
                case ConnectionTone.Ready: return Win32.Rgb(0, 120, 215);
                case ConnectionTone.Warning: return Win32.Rgb(177, 105, 0);
                case ConnectionTone.Error: return Win32.Rgb(196, 51, 51);
                default: return Win32.Rgb(139, 154, 173);
            }
        }

        private void ShowNotice(string message) { notice = message; noticeUntil = DateTime.Now.AddSeconds(3); }

        private bool CopyAddress(string value)
        {
            if (string.IsNullOrEmpty(value) || !Win32.OpenClipboard(hwnd)) return false;
            IntPtr memory = IntPtr.Zero;
            try
            {
                char[] text = (value + "\0").ToCharArray();
                memory = Win32.GlobalAlloc(2, new UIntPtr((uint)(text.Length * 2)));
                if (memory == IntPtr.Zero) return false;
                IntPtr data = Win32.GlobalLock(memory);
                if (data == IntPtr.Zero) return false;
                System.Runtime.InteropServices.Marshal.Copy(text, 0, data, text.Length);
                Win32.GlobalUnlock(memory);
                if (!Win32.EmptyClipboard() || Win32.SetClipboardData(13, memory) == IntPtr.Zero) return false;
                memory = IntPtr.Zero; return true;
            }
            finally { if (memory != IntPtr.Zero) Win32.GlobalFree(memory); Win32.CloseClipboard(); }
        }

        private Win32.RECT RetryRect(Metrics m)
        { return Rect(m.Left + m.ContentWidth - S(76), S(66), m.Left + m.ContentWidth - S(4), S(96)); }

        private void DrawKeyboardFocus(IntPtr hdc, Metrics m)
        {
            if (!keyboardFocus || focusArea == AreaNone) return;
            Win32.RECT r;
            switch (focusArea)
            {
                case AreaSettingsGeneral:
                case AreaSettingsDevices:
                case AreaSettingsNetwork:
                case AreaFirewallEnable:
                case AreaFirewallDisable:
                case AreaFirewallCheck:
                case AreaPreviousPage:
                case AreaNextPage:
                case AreaDeviceBluetooth:
                case AreaDeviceWifi: r = SettingsControlRect(m, focusArea); break;
                case AreaModeBluetooth: r = m.ModeBluetoothRect; break;
                case AreaModeWifi: r = m.ModeWifiRect; break;
                case AreaSettings: r = m.SettingsRect; break;
                case AreaBack: r = m.BackRect; break;
                case AreaOutput: r = m.OutputRect; break;
                case AreaConnect: r = m.ConnectRect; break;
                case AreaDisconnect: r = m.DisconnectRect; break;
                case AreaRefresh: r = m.RefreshRect; break;
                case AreaOpenLogs: r = m.LogsButtonRect; break;
                case AreaCopyAddress: r = m.CopyAddressRect; break;
                case AreaDetails: r = m.DetailsRect; break;
                case AreaRetryWifi: r = RetryRect(m); break;
                case AreaBuffer80: r = m.Buffer80Rect; break;
                case AreaBuffer120: r = m.Buffer120Rect; break;
                case AreaBuffer160: r = m.Buffer160Rect; break;
                case AreaAutoStart: r = Rect(m.Left + S(8), S(174), m.Left + m.ContentWidth - S(8), S(222)); break;
                case AreaAutoConnect: r = Rect(m.Left + S(8), S(250), m.Left + m.ContentWidth - S(8), S(298)); break;
                default:
                    if (focusArea >= AreaDropdownBase && focusArea < AreaBack && dropdownOpen)
                    { int top = dropdownRect.Top + S(4) + (focusArea - AreaDropdownBase) * S(DropdownRowHeight); r = Rect(dropdownRect.Left + S(4), top, dropdownRect.Right - S(4), top + S(DropdownRowHeight)); }
                    else if (focusArea >= AreaDeviceBase && focusArea < AreaDropdownBase)
                    { int top = m.DeviceListTop + (focusArea - AreaDeviceBase) * S(DeviceRowHeight); r = Rect(m.Left, top, m.Left + m.ContentWidth, top + S(DeviceRowHeight)); }
                    else return;
                    break;
            }
            IntPtr pen = Win32.CreatePen(Win32.PS_SOLID, S(2), Win32.Rgb(0, 120, 215));
            IntPtr oldPen = Win32.SelectObject(hdc, pen), oldBrush = Win32.SelectObject(hdc, Win32.GetStockObject(Win32.HOLLOW_BRUSH));
            Win32.RoundRect(hdc, r.Left - S(2), r.Top - S(2), r.Right + S(2), r.Bottom + S(2), S(10), S(10));
            Win32.SelectObject(hdc, oldPen); Win32.SelectObject(hdc, oldBrush); Win32.DeleteObject(pen);
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
                case SinkState.Idle: return "蓝牙尚未连接";
                case SinkState.Busy: return "正在连接";
                case SinkState.Armed: return "连接就绪，等待音频";
                case SinkState.LinkReady: return "链路已建立";
                case SinkState.Flowing: return "蓝牙音频已接入";
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
                Win32.DeleteObject(fontValue);
                Win32.DeleteObject(fontHeading);
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
