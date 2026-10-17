using System;
using System.Collections.Generic;
using SinkLite.Core;
using SinkLite.Platform;

namespace SinkLite.Tray
{
    /// <summary>
    /// 主窗口：Win32 + GDI 手绘。
    ///
    /// 设计要点：
    ///   - 输出设备下拉是“就地展开”的自绘列表，不再弹系统菜单，避免割裂感；
    ///   - 按钮具备 常态 / 悬停 / 按下 / 禁用 四种视觉状态；
    ///   - 文案精简：只保留状态与必要标签，冗长说明全部移除；
    ///   - 双缓冲绘制 + 仅内容变化时重绘，保证拖动与点击跟手。
    /// </summary>
    internal sealed class UiHost : SinkHost, IDisposable
    {
        private const int IdTimer = 1;
        private const int MaxDeviceRows = 5;
        private const int MaxDropdownRows = 6;

        private const int PadX = 16;
        private const int HeaderHeight = 64;
        private const int DeviceRowHeight = 30;
        private const int DropdownRowHeight = 32;
        private const int ButtonHeight = 36;

        // 悬停/按下/下拉区域标识
        private const int AreaNone = -1;
        private const int AreaConnect = 1;
        private const int AreaDisconnect = 2;
        private const int AreaRefresh = 3;
        private const int AreaOutput = 4;
        private const int AreaDeviceBase = 100;
        private const int AreaDropdownBase = 200;

        private readonly SinkController controller;
        private readonly TrayHost tray;
        private readonly Log log;

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

        private double scale = 1.0;
        private bool exiting;
        private bool mouseInside;

        private int selectedDevice;
        private int hoverArea = AreaNone;
        private int pressedArea = AreaNone;
        private bool dropdownOpen;

        private IReadOnlyList<EndpointInfo> cachedOutputs = new List<EndpointInfo>();
        private string cachedPreferredName = "选择输出设备";
        private DateTime lastOutputRefresh = DateTime.MinValue;
        private DateTime lastDeviceRefresh = DateTime.MinValue;
        private string lastPaintKey = string.Empty;

        private Win32.RECT dropdownRect;
        private int dropdownRows;

        public UiHost(SinkController controller, TrayHost tray, Log log)
        {
            this.controller = controller;
            this.tray = tray;
            this.log = log;
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
            wc.lpszClassName = "BlueAudioSinkMainWindow";
            Win32.RegisterClassEx(ref wc);

            hwnd = Win32.CreateWindowEx(
                0,
                "BlueAudioSinkMainWindow",
                "BlueAudioSink",
                Win32.WS_CAPTION | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | 0x00040000,
                S(140), S(140), S(560), S(420),
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            Win32.SetTimer(hwnd, new IntPtr(IdTimer), 1000, IntPtr.Zero);
            controller.SnapshotChanged += OnSnapshotChanged;

            Win32.RECT created;
            Win32.GetClientRect(hwnd, out created);
            log.Info("Ui.Created",
                ("dpi", (int)dpi),
                ("scale", scale),
                ("requestedW", S(560)),
                ("requestedH", S(420)),
                ("clientW", created.Right - created.Left),
                ("clientH", created.Bottom - created.Top));
        }

        private void CreateGdiObjects()
        {
            brushBack = Win32.CreateSolidBrush(Win32.Rgb(246, 246, 246));
            brushCard = Win32.CreateSolidBrush(Win32.Rgb(255, 255, 255));
            brushBorder = Win32.CreateSolidBrush(Win32.Rgb(226, 226, 226));
            // 三态对比刻意拉开：悬停更亮、按下更深，让点击反馈一眼可辨
            brushAccent = Win32.CreateSolidBrush(Win32.Rgb(0, 120, 215));
            brushAccentHover = Win32.CreateSolidBrush(Win32.Rgb(40, 150, 240));
            brushAccentPress = Win32.CreateSolidBrush(Win32.Rgb(0, 85, 160));
            brushBtn = Win32.CreateSolidBrush(Win32.Rgb(255, 255, 255));
            brushBtnHover = Win32.CreateSolidBrush(Win32.Rgb(232, 240, 250));
            brushBtnPress = Win32.CreateSolidBrush(Win32.Rgb(206, 222, 240));
            brushRowHover = Win32.CreateSolidBrush(Win32.Rgb(245, 245, 245));
            brushRowSelected = Win32.CreateSolidBrush(Win32.Rgb(229, 241, 251));

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
            Win32.SetForegroundWindow(hwnd);
            RefreshOutputCache(true);
            RequestRepaint(true);
        }

        public void PrepareExit()
        {
            exiting = true;
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
            return string.Concat(
                (int)s.State, "|", s.Detail ?? "", "|", s.DeviceName ?? "", "|",
                s.Peak.ToString("0.000"), "|", s.Hint ?? "", "|",
                selectedDevice, "|", hoverArea, "|", pressedArea, "|",
                dropdownOpen ? 1 : 0, "|",
                controller.Devices.Count, "|", cachedOutputs.Count, "|", cachedPreferredName);
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
            public int ContentLeft;
            public int ContentWidth;
            public int SourceLabelTop;
            public int DeviceListTop;
            public int VisibleRows;
            public int OutputLabelTop;
            public int OutputBoxTop;
            public int OutputBoxBottom;
            public int ButtonTop;
            public int ButtonWidth;
            public Win32.RECT ConnectRect;
            public Win32.RECT DisconnectRect;
            public Win32.RECT RefreshRect;
            public Win32.RECT OutputRect;
        }

        private Metrics ComputeMetrics()
        {
            Win32.RECT client;
            Win32.GetClientRect(hwnd, out client);
            int width = Math.Max(client.Right - client.Left, S(360));
            int height = Math.Max(client.Bottom - client.Top, S(280));

            Metrics m = new Metrics();
            m.Width = width;
            m.Height = height;
            m.ContentLeft = S(PadX);
            m.ContentWidth = width - S(PadX) * 2;

            m.SourceLabelTop = S(HeaderHeight) + S(10);
            m.DeviceListTop = m.SourceLabelTop + S(22);

            // 列表高度按“实际设备数”收缩，而不是占满可用空间，避免大片空白。
            int bottomZone = S(ButtonHeight) + S(28);
            int available = height - m.DeviceListTop - bottomZone - S(70);
            int fitRows = Math.Max(1, available / S(DeviceRowHeight));
            int wantRows = Math.Max(1, controller.Devices.Count);
            m.VisibleRows = Math.Max(1, Math.Min(Math.Min(MaxDeviceRows, wantRows), fitRows));

            int listBottom = m.DeviceListTop + m.VisibleRows * S(DeviceRowHeight);
            m.OutputLabelTop = listBottom + S(14);
            m.OutputBoxTop = m.OutputLabelTop + S(22);
            m.OutputBoxBottom = m.OutputBoxTop + S(34);
            m.OutputRect = Rect(m.ContentLeft, m.OutputBoxTop,
                m.ContentLeft + m.ContentWidth, m.OutputBoxBottom);

            // 按钮紧跟输出框下方（而不是钉在窗口底部），避免内容与按钮之间出现大片空白；
            // 若窗口被压得很矮，则退回“贴底”以保证按钮始终可见。
            m.ButtonTop = m.OutputBoxBottom + S(18);
            int maxButtonTop = height - S(ButtonHeight) - S(14);
            if (m.ButtonTop > maxButtonTop)
            {
                m.ButtonTop = maxButtonTop;
            }
            int gap = S(10);
            m.ButtonWidth = Math.Max(S(88), (m.ContentWidth - gap * 2) / 3);
            m.ConnectRect = Rect(m.ContentLeft, m.ButtonTop,
                m.ContentLeft + m.ButtonWidth, m.ButtonTop + S(ButtonHeight));
            m.DisconnectRect = Rect(m.ConnectRect.Right + gap, m.ButtonTop,
                m.ConnectRect.Right + gap + m.ButtonWidth, m.ButtonTop + S(ButtonHeight));
            m.RefreshRect = Rect(m.DisconnectRect.Right + gap, m.ButtonTop,
                m.DisconnectRect.Right + gap + m.ButtonWidth, m.ButtonTop + S(ButtonHeight));

            return m;
        }

        /// <summary>计算下拉浮层：优先向下展开，空间不足则向上。</summary>
        private void ComputeDropdown(Metrics m)
        {
            dropdownRows = Math.Max(1, Math.Min(MaxDropdownRows, cachedOutputs.Count));
            if (dropdownRows == 0)
            {
                dropdownRect = Rect(m.OutputRect.Left, m.OutputRect.Bottom + S(4),
                    m.OutputRect.Right, m.OutputRect.Bottom + S(4));
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
            if (Contains(m.OutputRect, x, y)) { return AreaOutput; }

            if (x >= m.ContentLeft && x <= m.ContentLeft + m.ContentWidth &&
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

        // ===== 输入处理 =====

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
                // 在下拉之外松开鼠标（含空白区域）一律收起
                dropdownOpen = false;
            }

            RequestRepaint(true);
        }

        private void Activate(int area)
        {
            if (area >= AreaDropdownBase)
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
                case AreaOutput:
                    dropdownOpen = !dropdownOpen;
                    RefreshOutputCache(true);
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
                    lastDeviceRefresh = DateTime.Now;
                    lastOutputRefresh = DateTime.MinValue;
                    return;
            }

            if (area >= AreaDeviceBase)
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

        private void OnKeyDown(int vk)
        {
            if (vk == 0x1B /*VK_ESCAPE*/ && dropdownOpen)
            {
                dropdownOpen = false;
                RequestRepaint(true);
            }
        }

        private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            // 只有鼠标消息的 lParam 才是坐标；其他消息（如 WM_DPICHANGED）的 lParam
            // 可能是 64 位指针，直接 ToInt32() 会抛 OverflowException 导致进程崩溃。
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
                    if (!exiting)
                    {
                        Win32.ShowWindow(hWnd, Win32.SW_HIDE);
                        return IntPtr.Zero;
                    }
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

                SinkSnapshot snapshot = controller.Current;
                Metrics m = ComputeMetrics();

                DrawHeader(hdcMem, m, snapshot);
                DrawDeviceSection(hdcMem, m, snapshot);
                DrawOutputSection(hdcMem, m);
                DrawButtons(hdcMem, m);

                if (dropdownOpen)
                {
                    DrawDropdown(hdcMem);
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
            // 状态灯
            IntPtr dotBrush = Win32.CreateSolidBrush(StateColor(snapshot.State));
            IntPtr old = Win32.SelectObject(hdc, dotBrush);
            int dotTop = S(22);
            Win32.Ellipse(hdc, m.ContentLeft, dotTop, m.ContentLeft + S(12), dotTop + S(12));
            Win32.SelectObject(hdc, old);
            Win32.DeleteObject(dotBrush);

            // 主状态
            IntPtr oldFont = Win32.SelectObject(hdc, fontTitle);
            Win32.SetTextColor(hdc, Win32.Rgb(24, 24, 24));
            Win32.RECT titleRect = Rect(m.ContentLeft + S(22), S(6), m.ContentLeft + m.ContentWidth, S(38));
            Win32.DrawText(hdc, DescribeState(snapshot.State), -1, ref titleRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            // 副标题：设备名 · 峰值（合并原本散落的信息）
            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(118, 118, 118));
            Win32.RECT subRect = Rect(m.ContentLeft + S(22), S(38), m.ContentLeft + m.ContentWidth, S(58));
            string device = string.IsNullOrEmpty(snapshot.DeviceName) ? "无设备" : snapshot.DeviceName;
            // 无声时不必显示 0.000，只在确实有电平或正在传输时显示
            string sub = snapshot.Peak > 0.0005f
                ? device + "  ·  " + snapshot.Peak.ToString("0.000")
                : device;
            Win32.DrawText(hdc, sub, -1, ref subRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            Win32.SelectObject(hdc, oldFont);
        }

        private void DrawDeviceSection(IntPtr hdc, Metrics m, SinkSnapshot snapshot)
        {
            DrawCaption(hdc, "音频源", m.ContentLeft, m.SourceLabelTop);

            IReadOnlyList<SinkDevice> devices = controller.Devices;
            int drawn = 0;
            for (int i = 0; i < devices.Count && drawn < m.VisibleRows; i++, drawn++)
            {
                int top = m.DeviceListTop + drawn * S(DeviceRowHeight);
                Win32.RECT row = Rect(m.ContentLeft, top,
                    m.ContentLeft + m.ContentWidth, top + S(DeviceRowHeight) - S(2));

                bool isActive = devices[i].Id == snapshot.DeviceId;
                int area = AreaDeviceBase + i;

                if (isActive || hoverArea == area)
                {
                    IntPtr fill = isActive ? brushRowSelected : brushRowHover;
                    RoundFill(hdc, row, S(6), fill);
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
                Win32.RECT emptyRect = Rect(m.ContentLeft + S(2), m.DeviceListTop,
                    m.ContentLeft + m.ContentWidth, m.DeviceListTop + S(DeviceRowHeight));
                Win32.DrawText(hdc, "未发现设备，请先在 Windows 设置中配对手机", -1, ref emptyRect,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
            }
        }

        private void DrawOutputSection(IntPtr hdc, Metrics m)
        {
            DrawCaption(hdc, "输出", m.ContentLeft, m.OutputLabelTop);

            bool hover = hoverArea == AreaOutput;
            bool pressed = pressedArea == AreaOutput;
            RoundFill(hdc, m.OutputRect, S(8), pressed ? brushBtnPress : (hover ? brushBtnHover : brushCard));
            RoundBorder(hdc, m.OutputRect, S(8), brushBorder);

            Win32.SelectObject(hdc, fontBody);
            Win32.SetTextColor(hdc, Win32.Rgb(32, 32, 32));
            Win32.RECT textRect = Rect(m.OutputRect.Left + S(12), m.OutputRect.Top,
                m.OutputRect.Right - S(30), m.OutputRect.Bottom);
            Win32.DrawText(hdc, cachedPreferredName, -1, ref textRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            // 展开指示箭头
            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(110, 110, 110));
            Win32.RECT arrowRect = Rect(m.OutputRect.Right - S(26), m.OutputRect.Top,
                m.OutputRect.Right - S(10), m.OutputRect.Bottom);
            Win32.DrawText(hdc, dropdownOpen ? "▴" : "▾", -1, ref arrowRect,
                Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
        }

        /// <summary>就地展开的下拉列表，直接绘制在主窗口内，不再弹系统菜单。</summary>
        private void DrawDropdown(IntPtr hdc)
        {
            ComputeDropdown(ComputeMetrics());
            if (cachedOutputs.Count == 0)
            {
                return;
            }

            // 外框 + 白底，模拟轻量浮层
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
            // 按下时文字轻微下沉，强化反馈
            if (pressed)
            {
                textRect.Top += S(1);
                textRect.Bottom += S(1);
            }
            Win32.DrawText(hdc, text, -1, ref textRect,
                Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
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
            IntPtr oldBrush = Win32.SelectObject(hdc, Win32.GetStockObject(5 /*HOLLOW_BRUSH*/));
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
            }
            catch
            {
            }
        }
    }
}
