using System;
using System.Collections.Generic;
using SinkLite.Core;
using SinkLite.Platform;

namespace SinkLite.Tray
{
    /// <summary>
    /// 主窗口：Win32 + GDI 手写。针对性修正三类问题：
    ///   1) 卡顿 —— 布局与端点采样结果缓存，仅在数据变化时重绘；使用双缓冲。
    ///   2) 发糊 —— 进程已声明 Per-Monitor V2 DPI 感知，字体与坐标按窗口 DPI 缩放。
    ///   3) 不自适应 —— 所有位置依据客户区尺寸计算，窗口可任意缩放。
    /// </summary>
    internal sealed class UiHost : SinkHost, IDisposable
    {
        private const int IdTimer = 1;
        private const int MaxDeviceRows = 6;
        private const uint DefaultDpi = 96;

        private readonly SinkController controller;
        private readonly TrayHost tray;
        private readonly Log log;

        private IntPtr hwnd;
        private Win32.WndProcDelegate wndProc;
        private IntPtr fontTitle;
        private IntPtr fontBody;
        private IntPtr fontSmall;
        private IntPtr brushBack;
        private IntPtr brushPanel;
        private IntPtr brushAccent;
        private IntPtr brushButton;
        private IntPtr brushHover;
        private IntPtr brushSelect;

        private uint dpi = DefaultDpi;
        private double scale = 1.0;
        private int fontTitleHeight;
        private bool exiting;

        private int selectedDevice;
        private int hoverZone = -1;      // 0=连接 1=断开 2=刷新 3=输出 4=设备行
        private int hoverRow = -1;

        // 缓存：避免在重绘路径里枚举音频端点（那是重活，是卡顿的主因）
        private IReadOnlyList<EndpointInfo> cachedOutputs = new List<EndpointInfo>();
        private string cachedPreferredName = "（未选择输出设备）";
        private DateTime lastOutputRefresh = DateTime.MinValue;
        private DateTime lastDeviceRefresh = DateTime.MinValue;

        // 重绘去抖
        private string lastPaintKey = string.Empty;

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

            dpi = Win32.GetDpiForSystem();
            if (dpi < 72 || dpi > 480)
            {
                dpi = DefaultDpi;
            }
            scale = dpi / (double)DefaultDpi;

            wndProc = WindowProc;
            IntPtr instance = Win32.GetModuleHandle(null);

            Win32.WNDCLASSEX wc = new Win32.WNDCLASSEX();
            wc.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.WNDCLASSEX));
            wc.lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(wndProc);
            wc.hInstance = instance;
            wc.hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW);
            wc.lpszClassName = "BlueAudioSinkMainWindow";
            Win32.RegisterClassEx(ref wc);

            RecreateFonts();

            // 依据 DPI 计算初始窗口尺寸（客户区 + 标题栏）
            int clientWidth = S(600);
            int clientHeight = S(480);
            hwnd = Win32.CreateWindowEx(
                0,
                "BlueAudioSinkMainWindow",
                "BlueAudioSink — 蓝牙音频接收",
                Win32.WS_CAPTION | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | 0x00040000 /*WS_THICKFRAME*/,
                S(120), S(120), clientWidth, clientHeight,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            Win32.SetTimer(hwnd, new IntPtr(IdTimer), 1000, IntPtr.Zero);
            controller.SnapshotChanged += OnSnapshotChanged;
            log.Info("Ui.Created", ("dpi", (int)dpi), ("scale", scale));
        }

        private void RecreateFonts()
        {
            if (fontTitle != IntPtr.Zero)
            {
                Win32.DeleteObject(fontTitle);
                Win32.DeleteObject(fontBody);
                Win32.DeleteObject(fontSmall);
            }

            fontTitleHeight = S(22);
            fontTitle = Win32.CreateFontW(-fontTitleHeight, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontBody = Win32.CreateFontW(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontSmall = Win32.CreateFontW(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");

            if (brushBack == IntPtr.Zero)
            {
                brushBack = Win32.CreateSolidBrush(Win32.Rgb(250, 250, 250));
                brushPanel = Win32.CreateSolidBrush(Win32.Rgb(255, 255, 255));
                brushAccent = Win32.CreateSolidBrush(Win32.Rgb(0, 120, 215));
                brushButton = Win32.CreateSolidBrush(Win32.Rgb(234, 234, 234));
                brushHover = Win32.CreateSolidBrush(Win32.Rgb(216, 216, 216));
                brushSelect = Win32.CreateSolidBrush(Win32.Rgb(229, 241, 251));
            }
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
            // 关键：重绘请求不在这里直接做重活，只标脏；
            // 真正的绘制由消息循环在空闲时合并执行。
            RequestRepaint(false);
        }

        /// <summary>请求重绘。force=false 时若内容未变则跳过，避免无谓重绘造成卡顿。</summary>
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
                selectedDevice, "|", hoverZone, "|", hoverRow, "|",
                controller.Devices.Count, "|", cachedOutputs.Count, "|", cachedPreferredName);
        }

        private void OnTimer()
        {
            // 刻意不在定时器里枚举蓝牙设备：每次枚举都会创建大量 WinRT COM 对象，
            // 高频轮询会让句柄持续增长（实测 15 秒一次时约 15 个/秒）。
            // 已配对设备几乎不变化，因此只在启动与用户点“刷新设备”时枚举。
            RefreshOutputCache(false);
            RequestRepaint(false);
        }

        /// <summary>
        /// 刷新输出端点缓存。默认 5 秒最多一次，且避开重绘路径，
        /// 因为 MMDeviceEnumerator 枚举是相对昂贵的调用。
        /// </summary>
        private void RefreshOutputCache(bool force)
        {
            if (!force && DateTime.Now - lastOutputRefresh < TimeSpan.FromSeconds(5))
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
                    (cachedOutputs.Count > 0 ? cachedOutputs[0].Name : "（未选择输出设备）");
            }
            catch
            {
            }
        }

        private void OnMouseMove(IntPtr lParam)
        {
            int x = (short)(lParam.ToInt32() & 0xFFFF);
            int y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

            Layout l = ComputeLayout();
            int zone = HitZone(l, x, y);
            int row = zone == 4 ? (y - l.ListTop) / l.RowHeight : -1;

            if (zone != hoverZone || row != hoverRow)
            {
                hoverZone = zone;
                hoverRow = row;
                RequestRepaint(true);
            }
        }

        private int HitZone(Layout l, int x, int y)
        {
            if (Contains(l.ConnectRect, x, y)) { return 0; }
            if (Contains(l.DisconnectRect, x, y)) { return 1; }
            if (Contains(l.RefreshRect, x, y)) { return 2; }
            if (Contains(l.OutputRect, x, y)) { return 3; }
            if (x >= l.PadX && x <= l.PadX + l.ContentWidth &&
                y >= l.ListTop && y < l.ListTop + l.RowHeight * l.VisibleRows)
            {
                return 4;
            }
            return -1;
        }

        private static bool Contains(Win32.RECT r, int x, int y)
        {
            return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
        }

        private void OnClick(IntPtr lParam)
        {
            int x = (short)(lParam.ToInt32() & 0xFFFF);
            int y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

            Layout l = ComputeLayout();
            int zone = HitZone(l, x, y);

            if (zone == 4)
            {
                int index = (y - l.ListTop) / l.RowHeight;
                IReadOnlyList<SinkDevice> devices = controller.Devices;
                if (index >= 0 && index < devices.Count)
                {
                    selectedDevice = index;
                    controller.RequestConnect(devices[index].Id);
                    log.Info("Ui.ConnectClicked", ("device", devices[index].Name));
                    RequestRepaint(true);
                }
                return;
            }

            switch (zone)
            {
                case 0:
                {
                    IReadOnlyList<SinkDevice> devices = controller.Devices;
                    string deviceId = selectedDevice >= 0 && selectedDevice < devices.Count
                        ? devices[selectedDevice].Id
                        : null;
                    controller.RequestConnect(deviceId);
                    log.Info("Ui.ConnectClicked", ("device", deviceId ?? "(默认)"));
                    break;
                }

                case 1:
                    controller.RequestDisconnect();
                    log.Info("Ui.DisconnectClicked");
                    break;

                case 2:
                    controller.RequestRefreshDevices();
                    lastDeviceRefresh = DateTime.Now;
                    break;

                case 3:
                    ShowOutputMenu();
                    break;
            }
        }

        private void ShowOutputMenu()
        {
            RefreshOutputCache(true);
            IntPtr menu = Win32.CreatePopupMenu();
            if (cachedOutputs.Count == 0)
            {
                Win32.AppendMenu(menu, 0, IntPtr.Zero, "（未发现可用输出设备）");
            }
            else
            {
                for (int i = 0; i < cachedOutputs.Count; i++)
                {
                    bool preferred = cachedOutputs[i].Id == controller.PreferredRenderId;
                    Win32.AppendMenu(menu, 0, new IntPtr(TrayHost.CmdOutputBase + i),
                        (preferred ? "● " : "   ") + cachedOutputs[i].Name);
                }
            }

            Win32.POINT p;
            Win32.GetCursorPos(out p);
            Win32.SetForegroundWindow(hwnd);
            int cmd = Win32.TrackPopupMenu(menu, 0x0100 /*TPM_RETURNCMD*/, p.X, p.Y, 0, hwnd, IntPtr.Zero);
            Win32.DestroyMenu(menu);
            if (cmd != 0)
            {
                tray.HandleCommand(cmd);
                RefreshOutputCache(true);
                RequestRepaint(true);
            }
        }

        private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
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
                    dpi = (uint)(wParam.ToInt32() & 0xFFFF);
                    if (dpi < 72 || dpi > 480)
                    {
                        dpi = DefaultDpi;
                    }
                    scale = dpi / (double)DefaultDpi;
                    RecreateFonts();
                    RequestRepaint(true);
                    return IntPtr.Zero;

                case Win32.WM_TIMER:
                    OnTimer();
                    return IntPtr.Zero;

                case Win32.WM_LBUTTONDOWN:
                    OnClick(lParam);
                    return IntPtr.Zero;

                case Win32.WM_MOUSEMOVE:
                    OnMouseMove(lParam);
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

        /// <summary>依据客户区尺寸计算的布局。所有坐标都随窗口大小与 DPI 变化。</summary>
        private struct Layout
        {
            public int PadX;
            public int ContentWidth;
            public int HeaderHeight;
            public int ListTitleTop;
            public int ListTop;
            public int RowHeight;
            public int VisibleRows;
            public int OutputTitleTop;
            public int PeakTop;
            public int ButtonTop;
            public int HintTop;
            public Win32.RECT OutputRect;
            public Win32.RECT ConnectRect;
            public Win32.RECT DisconnectRect;
            public Win32.RECT RefreshRect;
        }

        private Layout ComputeLayout()
        {
            Win32.RECT client;
            Win32.GetClientRect(hwnd, out client);
            int width = Math.Max(client.Right - client.Left, S(320));
            int height = Math.Max(client.Bottom - client.Top, S(240));

            Layout l = new Layout();
            l.PadX = S(16);
            l.ContentWidth = width - l.PadX * 2;
            l.HeaderHeight = S(72);
            l.ListTitleTop = l.HeaderHeight + S(2);
            l.ListTop = l.HeaderHeight + S(22);
            l.RowHeight = S(26);

            // 底部区高度固定，剩余空间全部给设备列表 —— 这是自适应缩放的关键
            int bottomHeight = S(132);
            int availForList = height - l.ListTop - bottomHeight;
            l.VisibleRows = Math.Max(1, Math.Min(MaxDeviceRows, availForList / l.RowHeight));

            l.OutputTitleTop = l.ListTop + l.RowHeight * l.VisibleRows + S(8);
            int outputTop = l.OutputTitleTop + S(20);
            l.OutputRect = Rect(l.PadX, outputTop, l.PadX + l.ContentWidth, outputTop + S(30));

            l.PeakTop = outputTop + S(38);
            l.ButtonTop = height - S(76);
            l.HintTop = l.ButtonTop + S(40);

            int buttonWidth = Math.Max(S(96), Math.Min(S(140), (l.ContentWidth - S(24)) / 3));
            int gap = S(12);
            l.ConnectRect = Rect(l.PadX, l.ButtonTop, l.PadX + buttonWidth, l.ButtonTop + S(34));
            l.DisconnectRect = Rect(l.ConnectRect.Right + gap, l.ButtonTop,
                l.ConnectRect.Right + gap + buttonWidth, l.ButtonTop + S(34));
            l.RefreshRect = Rect(l.DisconnectRect.Right + gap, l.ButtonTop,
                l.DisconnectRect.Right + gap + buttonWidth, l.ButtonTop + S(34));

            return l;
        }

        private void Paint(IntPtr hWnd)
        {
            Win32.PAINTSTRUCT ps;
            IntPtr hdcScreen = Win32.BeginPaint(hWnd, out ps);
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hOldBitmap = IntPtr.Zero;

            try
            {
                // 双缓冲：先在内存 DC 上画完整帧，再一次性拷贝，消除闪烁与撕裂
                Win32.RECT client;
                Win32.GetClientRect(hWnd, out client);
                int width = Math.Max(client.Right - client.Left, 1);
                int height = Math.Max(client.Bottom - client.Top, 1);

                hdcMem = Win32.CreateCompatibleDC(hdcScreen);
                hBitmap = Win32.CreateCompatibleBitmap(hdcScreen, width, height);
                hOldBitmap = Win32.SelectObject(hdcMem, hBitmap);

                SinkSnapshot snapshot = controller.Current;
                Layout l = ComputeLayout();

                Win32.SetBkMode(hdcMem, Win32.TRANSPARENT);
                Win32.RECT full = Rect(0, 0, width, height);
                Win32.FillRect(hdcMem, ref full, brushBack);

                DrawHeader(hdcMem, l, snapshot);
                DrawDeviceList(hdcMem, l, snapshot);
                DrawOutputRow(hdcMem, l);
                DrawPeak(hdcMem, l, snapshot);
                DrawButtons(hdcMem, l);
                DrawHint(hdcMem, l, snapshot);

                Win32.BitBlt(hdcScreen, 0, 0, width, height, hdcMem, 0, 0, Win32.SRCCOPY);
            }
            finally
            {
                if (hOldBitmap != IntPtr.Zero)
                {
                    Win32.SelectObject(hdcMem, hOldBitmap);
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

        private void DrawHeader(IntPtr hdc, Layout l, SinkSnapshot snapshot)
        {
            IntPtr dotBrush = Win32.CreateSolidBrush(StateColor(snapshot.State));
            IntPtr old = Win32.SelectObject(hdc, dotBrush);
            int dotTop = S(16);
            Win32.Ellipse(hdc, l.PadX, dotTop, l.PadX + S(16), dotTop + S(16));
            Win32.SelectObject(hdc, old);
            Win32.DeleteObject(dotBrush);

            IntPtr oldFont = Win32.SelectObject(hdc, fontTitle);
            Win32.SetTextColor(hdc, Win32.Rgb(28, 28, 28));
            Win32.RECT titleRect = Rect(l.PadX + S(28), S(10), l.PadX + l.ContentWidth, S(38));
            Win32.DrawText(hdc, DescribeState(snapshot.State), -1, ref titleRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            Win32.SelectObject(hdc, fontBody);
            Win32.SetTextColor(hdc, Win32.Rgb(72, 72, 72));
            Win32.RECT detailRect = Rect(l.PadX + S(28), S(40), l.PadX + l.ContentWidth, S(66));
            string device = string.IsNullOrEmpty(snapshot.DeviceName) ? "未选择设备" : snapshot.DeviceName;
            Win32.DrawText(hdc, "设备：" + device + "　│　" + (snapshot.Detail ?? ""),
                -1, ref detailRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            Win32.SelectObject(hdc, oldFont);
        }

        private void DrawDeviceList(IntPtr hdc, Layout l, SinkSnapshot snapshot)
        {
            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(108, 108, 108));
            Win32.RECT titleRect = Rect(l.PadX, l.ListTitleTop, l.PadX + l.ContentWidth, l.ListTitleTop + S(20));
            Win32.DrawText(hdc, "音频源设备（点击即连接）", -1, ref titleRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);

            IReadOnlyList<SinkDevice> devices = controller.Devices;
            int drawn = 0;
            for (int i = 0; i < devices.Count && drawn < l.VisibleRows; i++, drawn++)
            {
                int top = l.ListTop + drawn * l.RowHeight;
                bool isSelected = i == selectedDevice;
                bool isActive = devices[i].Id == snapshot.DeviceId;
                bool isHover = hoverZone == 4 && hoverRow == drawn;

                if (isSelected || isHover)
                {
                    IntPtr fill = isSelected ? brushSelect : brushHover;
                    IntPtr ob = Win32.SelectObject(hdc, fill);
                    Win32.Rectangle(hdc, l.PadX, top, l.PadX + l.ContentWidth, top + l.RowHeight - S(2));
                    Win32.SelectObject(hdc, ob);
                }

                Win32.SelectObject(hdc, fontBody);
                Win32.SetTextColor(hdc, isActive ? Win32.Rgb(0, 110, 0) : Win32.Rgb(36, 36, 36));
                Win32.RECT textRect = Rect(l.PadX + S(8), top, l.PadX + l.ContentWidth - S(8),
                    top + l.RowHeight - S(2));
                Win32.DrawText(hdc, (isActive ? "● " : "   ") + devices[i].Name, -1, ref textRect,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
            }

            if (devices.Count == 0)
            {
                Win32.SelectObject(hdc, fontBody);
                Win32.SetTextColor(hdc, Win32.Rgb(150, 150, 150));
                Win32.RECT emptyRect = Rect(l.PadX + S(8), l.ListTop, l.PadX + l.ContentWidth,
                    l.ListTop + l.RowHeight);
                Win32.DrawText(hdc, "未发现设备，请先在 Windows 蓝牙设置中配对手机", -1, ref emptyRect,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
            }

            if (devices.Count > l.VisibleRows)
            {
                Win32.SelectObject(hdc, fontSmall);
                Win32.SetTextColor(hdc, Win32.Rgb(140, 140, 140));
                Win32.RECT moreRect = Rect(l.PadX, l.ListTop + drawn * l.RowHeight,
                    l.PadX + l.ContentWidth, l.ListTop + (drawn + 1) * l.RowHeight);
                Win32.DrawText(hdc, "…… 还有 " + (devices.Count - drawn) + " 个设备", -1, ref moreRect,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
            }
        }

        private void DrawOutputRow(IntPtr hdc, Layout l)
        {
            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(108, 108, 108));
            Win32.RECT titleRect = Rect(l.PadX, l.OutputTitleTop, l.PadX + l.ContentWidth,
                l.OutputTitleTop + S(20));
            Win32.DrawText(hdc, "声音输出设备（选定后自动锁定，防止被系统切走）", -1, ref titleRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

            IntPtr ob = Win32.SelectObject(hdc, hoverZone == 3 ? brushHover : brushPanel);
            Win32.Rectangle(hdc, l.OutputRect.Left, l.OutputRect.Top, l.OutputRect.Right, l.OutputRect.Bottom);
            Win32.SelectObject(hdc, ob);

            Win32.SelectObject(hdc, fontBody);
            Win32.SetTextColor(hdc, Win32.Rgb(28, 28, 28));
            Win32.RECT textRect = Rect(l.OutputRect.Left + S(10), l.OutputRect.Top,
                l.OutputRect.Right - S(24), l.OutputRect.Bottom);
            Win32.DrawText(hdc, cachedPreferredName + "   ▾", -1, ref textRect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
        }

        private void DrawPeak(IntPtr hdc, Layout l, SinkSnapshot snapshot)
        {
            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(108, 108, 108));
            Win32.RECT rect = Rect(l.PadX, l.PeakTop, l.PadX + l.ContentWidth, l.PeakTop + S(20));
            Win32.DrawText(hdc, "音频电平：" + snapshot.Peak.ToString("0.0000") +
                (snapshot.Peak > 0.0005f ? "　正在出声" : "　无音频"),
                -1, ref rect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
        }

        private void DrawButtons(IntPtr hdc, Layout l)
        {
            DrawButton(hdc, l.ConnectRect, "连接 / 重连", hoverZone == 0, true);
            DrawButton(hdc, l.DisconnectRect, "断开", hoverZone == 1, false);
            DrawButton(hdc, l.RefreshRect, "刷新设备", hoverZone == 2, false);
        }

        private void DrawButton(IntPtr hdc, Win32.RECT rect, string text, bool hover, bool primary)
        {
            IntPtr brush = primary ? brushAccent : (hover ? brushHover : brushButton);
            IntPtr old = Win32.SelectObject(hdc, brush);
            Win32.RoundRect(hdc, rect.Left, rect.Top, rect.Right, rect.Bottom, S(6), S(6));
            Win32.SelectObject(hdc, old);

            Win32.SelectObject(hdc, fontBody);
            Win32.SetTextColor(hdc, primary ? Win32.Rgb(255, 255, 255) : Win32.Rgb(28, 28, 28));
            Win32.RECT textRect = rect;
            Win32.DrawText(hdc, text, -1, ref textRect,
                Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
        }

        private void DrawHint(IntPtr hdc, Layout l, SinkSnapshot snapshot)
        {
            Win32.SelectObject(hdc, fontSmall);
            Win32.SetTextColor(hdc, Win32.Rgb(128, 128, 128));
            Win32.RECT rect = Rect(l.PadX, l.HintTop, l.PadX + l.ContentWidth, l.HintTop + S(24));
            string hint = string.IsNullOrEmpty(snapshot.Hint)
                ? "关闭窗口会隐藏到托盘继续运行，音频不中断；完全退出请用托盘右键菜单。"
                : "建议：" + snapshot.Hint;
            Win32.DrawText(hdc, hint, -1, ref rect,
                Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
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

        private static int StateColor(SinkState state)
        {
            switch (state)
            {
                case SinkState.Flowing: return Win32.Rgb(40, 170, 70);
                case SinkState.LinkReady: return Win32.Rgb(40, 140, 220);
                case SinkState.Armed: return Win32.Rgb(230, 170, 30);
                case SinkState.Busy:
                case SinkState.Backoff: return Win32.Rgb(240, 140, 40);
                case SinkState.Faulted: return Win32.Rgb(210, 60, 60);
                default: return Win32.Rgb(150, 150, 150);
            }
        }

        private static string DescribeState(SinkState state)
        {
            switch (state)
            {
                case SinkState.Idle: return "未连接";
                case SinkState.Busy: return "正在连接…";
                case SinkState.Armed: return "已就绪，等待手机发送音频";
                case SinkState.LinkReady: return "音频链路已建立，等待播放";
                case SinkState.Flowing: return "音频正在传输";
                case SinkState.Backoff: return "连接失败，正在重试";
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
                Win32.DeleteObject(brushBack);
                Win32.DeleteObject(brushPanel);
                Win32.DeleteObject(brushAccent);
                Win32.DeleteObject(brushButton);
                Win32.DeleteObject(brushHover);
                Win32.DeleteObject(brushSelect);
            }
            catch
            {
            }
        }
    }
}
