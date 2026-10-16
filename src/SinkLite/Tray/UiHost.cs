using System;
using System.Collections.Generic;
using SinkLite.Core;
using SinkLite.Platform;

namespace SinkLite.Tray
{
    /// <summary>
    /// 主窗口：用 Win32 + GDI 手写，避免引入 WinForms/WPF 运行时
    /// （那会把发布体积从 10.7 MB 拉到 78 MB）。
    /// 提供：设备选择、输出设备选择、实时状态、手动连接。
    /// </summary>
    internal sealed class UiHost : SinkHost, IDisposable
    {
        private const int IdTimer = 1;
        private const int MaxDevices = 5;

        private const int PadX = 16;
        private const int ContentWidth = 528;

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

        private double scale = 1.0;
        private bool exiting;
        private int selectedDevice;
        private bool connectHover;
        private bool outputHover;
        private bool refreshHover;
        private DateTime lastDeviceRefresh = DateTime.MinValue;

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
            scale = 1.0;

            wndProc = WindowProc;
            IntPtr instance = Win32.GetModuleHandle(null);

            Win32.WNDCLASSEX wc = new Win32.WNDCLASSEX();
            wc.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.WNDCLASSEX));
            wc.lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(wndProc);
            wc.hInstance = instance;
            wc.hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW);
            wc.lpszClassName = "BlueAudioSinkMainWindow";
            Win32.RegisterClassEx(ref wc);

            CreateGdiObjects();

            int width = S(560);
            int height = S(472);
            hwnd = Win32.CreateWindowEx(
                0,
                "BlueAudioSinkMainWindow",
                "BlueAudioSink — 蓝牙音频接收",
                Win32.WS_CAPTION | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX,
                S(120), S(120), width, height,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            Win32.SetTimer(hwnd, new IntPtr(IdTimer), 500, IntPtr.Zero);
            controller.SnapshotChanged += OnSnapshotChanged;
            log.Info("Ui.Created");
        }

        private void CreateGdiObjects()
        {
            brushBack = Win32.CreateSolidBrush(Win32.Rgb(250, 250, 250));
            brushPanel = Win32.CreateSolidBrush(Win32.Rgb(255, 255, 255));
            brushAccent = Win32.CreateSolidBrush(Win32.Rgb(0, 120, 215));
            brushButton = Win32.CreateSolidBrush(Win32.Rgb(232, 232, 232));
            brushHover = Win32.CreateSolidBrush(Win32.Rgb(214, 214, 214));

            fontTitle = Win32.CreateFontW(-S(21), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontBody = Win32.CreateFontW(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            fontSmall = Win32.CreateFontW(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
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
            Win32.InvalidateRect(hwnd, IntPtr.Zero, true);
        }

        public void PrepareExit()
        {
            exiting = true;
        }

        private void OnSnapshotChanged(object sender, SinkSnapshot snapshot)
        {
            if (hwnd != IntPtr.Zero)
            {
                Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
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
                        // 关闭窗口只是隐藏：保持连接与音频不中断
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

        private void OnTimer()
        {
            if (DateTime.Now - lastDeviceRefresh > TimeSpan.FromSeconds(10))
            {
                lastDeviceRefresh = DateTime.Now;
                controller.RequestRefreshDevices();
            }

            if (hwnd != IntPtr.Zero)
            {
                Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
            }
        }

        private void OnMouseMove(IntPtr lParam)
        {
            int x = (short)(lParam.ToInt32() & 0xFFFF);
            int y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

            bool ch = HitConnect(x, y);
            bool oh = HitOutput(x, y);
            bool rh = HitRefresh(x, y);

            if (ch != connectHover || oh != outputHover || rh != refreshHover)
            {
                connectHover = ch;
                outputHover = oh;
                refreshHover = rh;
                Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
            }
        }

        private void OnClick(IntPtr lParam)
        {
            int x = (short)(lParam.ToInt32() & 0xFFFF);
            int y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

            // 设备列表
            int listTop = S(96);
            int rowHeight = S(26);
            IReadOnlyList<SinkDevice> devices = controller.Devices;
            int rows = Math.Min(devices.Count, MaxDevices);
            if (x >= PadX && x <= S(560) - PadX && y >= listTop && y < listTop + rows * rowHeight)
            {
                int index = (y - listTop) / rowHeight;
                if (index >= 0 && index < devices.Count)
                {
                    selectedDevice = index;
                    controller.RequestConnect(devices[index].Id);
                    log.Info("Ui.ConnectClicked", ("device", devices[index].Name));
                    Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
                }
                return;
            }

            if (HitConnect(x, y))
            {
                string deviceId = null;
                if (selectedDevice >= 0 && selectedDevice < devices.Count)
                {
                    deviceId = devices[selectedDevice].Id;
                }
                controller.RequestConnect(deviceId);
                log.Info("Ui.ConnectClicked", ("device", deviceId ?? "(默认)"));
                return;
            }

            if (HitDisconnect(x, y))
            {
                controller.RequestDisconnect();
                log.Info("Ui.DisconnectClicked");
                return;
            }

            if (HitRefresh(x, y))
            {
                controller.RequestRefreshDevices();
                lastDeviceRefresh = DateTime.Now;
                return;
            }

            if (HitOutput(x, y))
            {
                ShowOutputMenu(x, y);
                return;
            }
        }

        private bool HitConnect(int x, int y)
        {
            int top = S(400);
            return x >= PadX && x <= PadX + S(120) && y >= top && y <= top + S(34);
        }

        private bool HitDisconnect(int x, int y)
        {
            int top = S(400);
            return x >= PadX + S(132) && x <= PadX + S(252) && y >= top && y <= top + S(34);
        }

        private bool HitRefresh(int x, int y)
        {
            int top = S(400);
            return x >= PadX + S(264) && x <= PadX + S(384) && y >= top && y <= top + S(34);
        }

        private bool HitOutput(int x, int y)
        {
            int top = S(322);
            return x >= PadX && x <= PadX + S(ContentWidth) && y >= top && y <= top + S(30);
        }

        private void ShowOutputMenu(int x, int y)
        {
            IReadOnlyList<EndpointInfo> outputs = controller.ListPhysicalOutputs();
            IntPtr menu = Win32.CreatePopupMenu();
            if (outputs.Count == 0)
            {
                Win32.AppendMenu(menu, 0, IntPtr.Zero, "（未发现可用输出设备）");
            }
            else
            {
                for (int i = 0; i < outputs.Count; i++)
                {
                    bool preferred = outputs[i].Id == controller.PreferredRenderId;
                    Win32.AppendMenu(menu, 0, new IntPtr(TrayHost.CmdOutputBase + i),
                        (preferred ? "● " : "   ") + outputs[i].Name);
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
            }
        }

        private void Paint(IntPtr hWnd)
        {
            Win32.PAINTSTRUCT ps;
            IntPtr hdc = Win32.BeginPaint(hWnd, out ps);
            try
            {
                SinkSnapshot snapshot = controller.Current;
                Win32.SetBkMode(hdc, Win32.TRANSPARENT);

                // 背景
                Win32.RECT full;
                Win32.GetClientRect(hWnd, out full);
                IntPtr oldBrush = Win32.SelectObject(hdc, brushBack);
                Win32.Rectangle(hdc, full.Left - 1, full.Top - 1, full.Right + 1, full.Bottom + 1);
                Win32.SelectObject(hdc, oldBrush);

                // 状态灯 + 标题
                int dotColor = StateColor(snapshot.State);
                IntPtr dotBrush = Win32.CreateSolidBrush(dotColor);
                IntPtr old = Win32.SelectObject(hdc, dotBrush);
                Win32.Ellipse(hdc, PadX, S(18), PadX + S(16), S(34));
                Win32.SelectObject(hdc, old);
                Win32.DeleteObject(dotBrush);

                IntPtr oldFont = Win32.SelectObject(hdc, fontTitle);
                Win32.SetTextColor(hdc, Win32.Rgb(30, 30, 30));
                Win32.RECT titleRect = Rect(PadX + S(28), S(12), S(560) - PadX, S(44));
                Win32.DrawTextW(hdc, DescribeState(snapshot.State), -1, ref titleRect,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

                Win32.SelectObject(hdc, fontBody);
                Win32.SetTextColor(hdc, Win32.Rgb(70, 70, 70));
                Win32.RECT detailRect = Rect(PadX + S(28), S(44), S(560) - PadX, S(70));
                string device = string.IsNullOrEmpty(snapshot.DeviceName) ? "未选择设备" : snapshot.DeviceName;
                Win32.DrawTextW(hdc, "设备：" + device + "　│　" + (snapshot.Detail ?? ""),
                    -1, ref detailRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

                // 设备列表标题
                Win32.SelectObject(hdc, fontSmall);
                Win32.SetTextColor(hdc, Win32.Rgb(110, 110, 110));
                Win32.RECT listTitle = Rect(PadX, S(74), S(560) - PadX, S(94));
                Win32.DrawTextW(hdc, "音频源设备（点击即连接）", -1, ref listTitle,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);

                // 设备行
                IReadOnlyList<SinkDevice> devices = controller.Devices;
                int listTop = S(96);
                int rowHeight = S(26);
                for (int i = 0; i < devices.Count && i < MaxDevices; i++)
                {
                    int top = listTop + i * rowHeight;
                    bool isSelected = i == selectedDevice;
                    bool isActive = devices[i].Id == snapshot.DeviceId;

                    Win32.RECT row = Rect(PadX, top, S(560) - PadX, top + rowHeight - S(2));
                    if (isSelected)
                    {
                        IntPtr selBrush = Win32.CreateSolidBrush(Win32.Rgb(229, 241, 251));
                        IntPtr ob = Win32.SelectObject(hdc, selBrush);
                        Win32.Rectangle(hdc, row.Left, row.Top, row.Right, row.Bottom);
                        Win32.SelectObject(hdc, ob);
                        Win32.DeleteObject(selBrush);
                    }

                    Win32.SelectObject(hdc, fontBody);
                    Win32.SetTextColor(hdc, isActive ? Win32.Rgb(0, 100, 0) : Win32.Rgb(40, 40, 40));
                    Win32.RECT textRect = Rect(PadX + S(8), top, S(560) - PadX - S(8), top + rowHeight - S(2));
                    string label = (isActive ? "● " : "   ") + devices[i].Name;
                    Win32.DrawTextW(hdc, label, -1, ref textRect,
                        Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
                }

                if (devices.Count == 0)
                {
                    Win32.SelectObject(hdc, fontBody);
                    Win32.SetTextColor(hdc, Win32.Rgb(150, 150, 150));
                    Win32.RECT emptyRect = Rect(PadX + S(8), listTop, S(560) - PadX, listTop + rowHeight);
                    Win32.DrawTextW(hdc, "未发现设备，请先在 Windows 蓝牙设置中配对手机", -1, ref emptyRect,
                        Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
                }

                // 输出设备
                Win32.SelectObject(hdc, fontSmall);
                Win32.SetTextColor(hdc, Win32.Rgb(110, 110, 110));
                Win32.RECT outTitle = Rect(PadX, S(292), S(560) - PadX, S(312));
                Win32.DrawTextW(hdc, "声音输出设备（选定后自动锁定，防止被系统切走）", -1, ref outTitle,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);

                int outTop = S(322);
                Win32.RECT outputRect = Rect(PadX, outTop, PadX + S(ContentWidth), outTop + S(30));
                IntPtr ob2 = Win32.SelectObject(hdc, outputHover ? brushHover : brushPanel);
                Win32.Rectangle(hdc, outputRect.Left, outputRect.Top, outputRect.Right, outputRect.Bottom);
                Win32.SelectObject(hdc, ob2);

                Win32.SelectObject(hdc, fontBody);
                Win32.SetTextColor(hdc, Win32.Rgb(30, 30, 30));
                Win32.RECT outText = Rect(PadX + S(10), outTop, PadX + S(ContentWidth) - S(24), outTop + S(30));
                Win32.DrawTextW(hdc, PreferredOutputName() + "   ▾", -1, ref outText,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);

                // 电平
                Win32.SelectObject(hdc, fontSmall);
                Win32.SetTextColor(hdc, Win32.Rgb(110, 110, 110));
                Win32.RECT peakRect = Rect(PadX, S(358), S(560) - PadX, S(378));
                Win32.DrawTextW(hdc, "音频电平：" + snapshot.Peak.ToString("0.0000") +
                    (snapshot.Peak > 0.0005f ? "　正在出声" : "　无音频"),
                    -1, ref peakRect, Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE);

                // 按钮
                DrawButton(hdc, PadX, S(400), S(120), S(34), "连接 / 重连", connectHover, true);
                DrawButton(hdc, PadX + S(132), S(400), S(120), S(34), "断开", false, false);
                DrawButton(hdc, PadX + S(264), S(400), S(120), S(34), "刷新设备", refreshHover, false);

                // 底部提示
                Win32.SelectObject(hdc, fontSmall);
                Win32.SetTextColor(hdc, Win32.Rgb(130, 130, 130));
                Win32.RECT hintRect = Rect(PadX, S(442), S(560) - PadX, S(468));
                string hint = string.IsNullOrEmpty(snapshot.Hint)
                    ? "关闭窗口会隐藏到托盘继续运行，音频不中断；完全退出请用托盘右键菜单。"
                    : "建议：" + snapshot.Hint;
                Win32.DrawTextW(hdc, hint, -1, ref hintRect,
                    Win32.DT_LEFT | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS);
            }
            finally
            {
                Win32.EndPaint(hWnd, ref ps);
            }
        }

        private void DrawButton(IntPtr hdc, int left, int top, int width, int height, string text, bool hover, bool primary)
        {
            Win32.RECT rect = Rect(left, top, left + width, top + height);
            IntPtr brush = primary ? brushAccent : (hover ? brushHover : brushButton);
            IntPtr old = Win32.SelectObject(hdc, brush);
            Win32.RoundRect(hdc, rect.Left, rect.Top, rect.Right, rect.Bottom, S(6), S(6));
            Win32.SelectObject(hdc, old);

            Win32.SelectObject(hdc, fontBody);
            Win32.SetTextColor(hdc, primary ? Win32.Rgb(255, 255, 255) : Win32.Rgb(30, 30, 30));
            Win32.RECT textRect = Rect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            Win32.DrawTextW(hdc, text, -1, ref textRect,
                Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE);
        }

        private string PreferredOutputName()
        {
            IReadOnlyList<EndpointInfo> outputs = controller.ListPhysicalOutputs();
            foreach (EndpointInfo o in outputs)
            {
                if (o.Id == controller.PreferredRenderId)
                {
                    return o.Name;
                }
            }
            return outputs.Count > 0 ? outputs[0].Name : "（未选择输出设备）";
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
            }
            catch
            {
            }
        }
    }
}
