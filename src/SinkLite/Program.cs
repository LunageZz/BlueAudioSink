using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SinkLite.Core;
using SinkLite.Platform;

namespace SinkLite
{
    internal static class Program
    {
        private static Log log;

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [STAThread]
        private static int Main(string[] args)
        {
            CliOptions options = Cli.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(Cli.HelpText());
                return 0;
            }

            // 默认（或 --tray）即托盘模式：释放控制台，双击启动时不出现黑窗。
            // 带显式参数（--list/--diagnose 等）时保留控制台，便于查看输出。
            bool launchedWithArgs = args != null && args.Length > 0;
            bool isTrayMode = options.NoTray == false;
            if (isTrayMode && !launchedWithArgs)
            {
                try { FreeConsole(); } catch { }
            }

            if (isTrayMode)
            {
                // 单实例：已在运行则直接退出，避免两个进程争抢同一个接收通道。
                bool createdNew;
                using (Mutex singleInstance = new Mutex(true, @"Global\BlueAudioSink.SingleInstance", out createdNew))
                {
                    if (!createdNew)
                    {
                        return 0;
                    }

                    try
                    {
                        return RunCore(options);
                    }
                    finally
                    {
                        try { singleInstance.ReleaseMutex(); } catch { }
                    }
                }
            }

            return RunCore(options);
        }

        private static int RunCore(CliOptions options)
        {
            // WinRT 需要初始化 COM 单元；在后台线程上 MTA 是安全选择。
            WinRtCompat.Initialize();

            log = new Log(Log.DefaultDirectory);
            Settings settings = Settings.Load();
            Policy policy = Policy.Default;
            if (settings.SampleSeconds >= 1)
            {
                policy.SampleInterval = TimeSpan.FromSeconds(settings.SampleSeconds);
            }

            using (SinkController controller = new SinkController(policy, log))
            {
                try
                {
                    controller.Start();
                }
                catch (Exception ex)
                {
                    log.Error("App.StartFailed", ("error", ex.Message));
                    Console.WriteLine("启动失败：" + ex.Message);
                    return 2;
                }

                if (options.ListOnly)
                {
                    return RunList(controller);
                }

                if (options.DiagnoseSeconds > 0)
                {
                    return RunDiagnose(controller, options.DiagnoseSeconds);
                }

                if (options.ConnectOnce)
                {
                    return RunConnected(controller);
                }

                return RunTray(controller);
            }
        }

        private static int RunList(SinkController controller)
        {
            WaitForDevices(controller);
            Console.WriteLine("可接收音频的已配对设备：" + controller.Devices.Count);
            int index = 1;
            foreach (SinkDevice device in controller.Devices)
            {
                Console.WriteLine("  " + index + ") " + device.Name);
                Console.WriteLine("     " + device.Id);
                index++;
            }

            if (controller.Devices.Count == 0)
            {
                Console.WriteLine("  （无）请先在 Windows 蓝牙设置中配对手机。");
                return 1;
            }
            return 0;
        }

        private static int RunDiagnose(SinkController controller, int seconds)
        {
            WaitForDevices(controller);
            if (controller.Devices.Count == 0)
            {
                Console.WriteLine("没有可接收音频的设备，请先配对手机。");
                return 1;
            }

            using (EndpointWatcher watcher = new EndpointWatcher())
            {
                Console.WriteLine("诊断开始，采样 " + seconds + " 秒（2 秒一次）");
                Console.WriteLine("提示：如需验证音频到达，请在手机上开始播放音乐。");
                Console.WriteLine();

                controller.RequestConnect(null);

                DateTime end = DateTime.Now.AddSeconds(seconds);
                while (DateTime.Now < end)
                {
                    SinkSnapshot snapshot = controller.Current;
                    float peak = watcher.GetDefaultPeak();
                    EndpointInfo render = watcher.GetDefaultRender();

                    bool hasA2dp = false;
                    string a2dpName = null;
                    foreach (EndpointInfo e in watcher.Sample())
                    {
                        if (e.IsBluetoothA2dp)
                        {
                            hasA2dp = true;
                            a2dpName = e.Name;
                            break;
                        }
                    }

                    Console.WriteLine(string.Format(
                        "{0:HH:mm:ss}  状态={1,-10} 峰值={2:0.0000} 蓝牙端点={3} 默认输出={4}",
                        DateTime.Now,
                        snapshot.State,
                        peak < 0 ? 0f : peak,
                        hasA2dp ? (string.IsNullOrEmpty(a2dpName) ? "有" : a2dpName) : "无",
                        render == null ? "?" : render.Name));

                    Thread.Sleep(2000);
                }

                Console.WriteLine();
                Console.WriteLine("归因结论：");
                SinkSnapshot final = controller.Current;
                Console.WriteLine("  最终状态：" + final.State);
                Console.WriteLine("  说明：" + final.Detail);
                if (!string.IsNullOrEmpty(final.Hint))
                {
                    Console.WriteLine("  建议：" + final.Hint);
                }
                return 0;
            }
        }

        private static int RunConnected(SinkController controller)
        {
            WaitForDevices(controller);
            Console.WriteLine("按 Ctrl+C 退出。");
            controller.RequestConnect(null);

            ManualResetEventSlim exit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                exit.Set();
            };

            SinkState last = SinkState.Idle;
            while (!exit.Wait(500))
            {
                SinkSnapshot snapshot = controller.Current;
                if (snapshot.State != last)
                {
                    last = snapshot.State;
                    Console.WriteLine(string.Format("{0:HH:mm:ss}  {1}  {2}", DateTime.Now, snapshot.State, snapshot.Detail));
                    if (!string.IsNullOrEmpty(snapshot.Hint))
                    {
                        Console.WriteLine("           提示：" + snapshot.Hint);
                    }
                }
            }

            return 0;
        }

        private static int RunTray(SinkController controller)
        {
            WaitForDevices(controller);
            using (EndpointWatcher watcher = new EndpointWatcher())
            {
                RouteControl route = new RouteControl(watcher);
                using (SinkLite.Tray.TrayHost host = new SinkLite.Tray.TrayHost(controller, route, watcher, log))
                {
                    log.Info("Tray.Starting", ("devices", controller.Devices.Count));

                    // 按配置自动连接：默认开启，让用户不必每次手动点连接。
                    Settings settings = Settings.Load();
                    if (settings.AutoConnect)
                    {
                        string target = string.IsNullOrEmpty(settings.DeviceId) ? null : settings.DeviceId;
                        controller.RequestConnect(target);
                        log.Info("Tray.AutoConnectRequested", ("device", string.IsNullOrEmpty(target) ? "(第一个可用)" : target));
                    }

                    host.Run();
                }
            }
            return 0;
        }

        private static void WaitForDevices(SinkController controller)
        {
            DateTime deadline = DateTime.Now.AddSeconds(8);
            while (controller.Devices.Count == 0 && DateTime.Now < deadline)
            {
                controller.RequestRefreshDevices();
                Thread.Sleep(400);
            }
        }
    }

    internal static class WinRtCompat
    {
        public static void Initialize()
        {
            try
            {
                Windows.Media.Audio.AudioPlaybackConnection.GetDeviceSelector();
            }
            catch
            {
            }
        }
    }
}
