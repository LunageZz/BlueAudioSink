using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace SinkProbe
{
    /// <summary>
    /// 独立诊断工具：在不启动界面的情况下复现与定位问题。
    /// 子命令：list / arm / open [秒] / watch [秒] / meter [秒] / endpoints
    /// </summary>
    internal static class Program
    {
        private static readonly string[] VirtualMarkers = new[]
        {
            "VB-Audio", "CABLE Input", "CABLE Output", "Steam Streaming",
            "虚拟音频", "Nahimic", "WO Mic", "网易"
        };

        private static int Main(string[] args)
        {
            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            int seconds = 30;
            if (args.Length > 1 && int.TryParse(args[1], out int parsed) && parsed > 0)
            {
                seconds = parsed;
            }

            try
            {
                switch (command)
                {
                    case "list": return ListDevices();
                    case "arm": return Arm();
                    case "open": return Open(seconds);
                    case "watch": return Watch(seconds);
                    case "meter": return Meter(seconds);
                    case "endpoints": return Endpoints();
                    case "help":
                    case "--help":
                    case "-h":
                        Console.WriteLine(Help());
                        return 0;
                    default:
                        Console.WriteLine("未知子命令：" + command);
                        Console.WriteLine();
                        Console.WriteLine(Help());
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("失败：" + ex.GetType().Name + " - " + ex.Message);
                return 3;
            }
        }

        private static string Help()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "SinkProbe - BlueAudioSink 诊断工具",
                "",
                "  list                列出可接收音频的已配对设备",
                "  arm                 武装接收通道（StartAsync）后释放",
                "  open [秒]           武装并打开连接，观察状态变化（默认 30 秒）",
                "  watch [秒]          只观察音频端点与峰值，不做任何连接动作",
                "  meter [秒]          只读取默认输出端点的峰值电平",
                "  endpoints           列出全部音频端点（含未插入/禁用）",
                "  help                显示帮助"
            });
        }

        private static DeviceInformationCollection FindSinks()
        {
            string selector = AudioPlaybackConnection.GetDeviceSelector();
            return DeviceInformation.FindAllAsync(selector).GetAwaiter().GetResult();
        }

        private static int ListDevices()
        {
            DeviceInformationCollection devices = FindSinks();
            Console.WriteLine("可接收音频的已配对设备：" + devices.Count);
            foreach (DeviceInformation d in devices)
            {
                Console.WriteLine("  " + d.Name);
                Console.WriteLine("    " + d.Id);
            }
            if (devices.Count == 0)
            {
                Console.WriteLine("  （无）请先在 Windows 蓝牙设置中配对手机。");
                return 1;
            }
            return 0;
        }

        private static int Arm()
        {
            DeviceInformationCollection devices = FindSinks();
            if (devices.Count == 0)
            {
                Console.WriteLine("没有可用设备。");
                return 1;
            }

            DeviceInformation d = devices[0];
            Console.WriteLine("设备：" + d.Name);
            AudioPlaybackConnection conn = AudioPlaybackConnection.TryCreateFromId(d.Id);
            if (conn == null)
            {
                Console.WriteLine("TryCreateFromId 返回 null：该设备不支持音频接收。");
                return 1;
            }

            Console.WriteLine("StartAsync（武装）…");
            DateTime start = DateTime.Now;
            conn.StartAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine("已武装，耗时 " + (DateTime.Now - start).TotalMilliseconds.ToString("0") + " ms，state=" + (int)conn.State);
            conn.Dispose();
            Console.WriteLine("已释放。");
            return 0;
        }

        private static int Open(int seconds)
        {
            DeviceInformationCollection devices = FindSinks();
            if (devices.Count == 0)
            {
                Console.WriteLine("没有可用设备。");
                return 1;
            }

            DeviceInformation d = devices[0];
            Console.WriteLine("设备：" + d.Name);
            AudioPlaybackConnection conn = AudioPlaybackConnection.TryCreateFromId(d.Id);
            if (conn == null)
            {
                Console.WriteLine("TryCreateFromId 返回 null。");
                return 1;
            }

            conn.StateChanged += (s, e) =>
                Console.WriteLine("  [" + DateTime.Now.ToString("HH:mm:ss.fff") + "] StateChanged -> " + (int)s.State);

            DateTime armStart = DateTime.Now;
            conn.StartAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine("武装完成 " + (DateTime.Now - armStart).TotalMilliseconds.ToString("0") + " ms");

            DateTime openStart = DateTime.Now;
            var result = conn.OpenAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine("OpenAsync status=" + result.Status +
                "，耗时 " + (DateTime.Now - openStart).TotalMilliseconds.ToString("0") + " ms" +
                "，state=" + (int)conn.State);

            for (int i = 0; i < seconds; i++)
            {
                Thread.Sleep(1000);
                Console.WriteLine("  t=" + (i + 1) + "s state=" + (int)conn.State + " 峰值=" + Peak().ToString("0.0000"));
            }

            conn.Dispose();
            Console.WriteLine("已释放。");
            return 0;
        }

        private static int Watch(int seconds)
        {
            Console.WriteLine("只观察 " + seconds + " 秒（不做连接动作）。");
            Dictionary<string, string> seen = new Dictionary<string, string>();
            DateTime start = DateTime.Now;

            while ((DateTime.Now - start).TotalSeconds < seconds)
            {
                using (MMDeviceEnumerator en = new MMDeviceEnumerator())
                {
                    foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
                    {
                        MMDeviceCollection coll;
                        try { coll = en.EnumerateAudioEndPoints(flow, DeviceState.All); }
                        catch { continue; }

                        foreach (MMDevice dev in coll)
                        {
                            string id;
                            try { id = dev.ID; } catch { continue; }
                            string name;
                            try { name = dev.FriendlyName; } catch { name = "(读取失败)"; }
                            string state = "(?)";
                            try { state = dev.State.ToString(); } catch { }

                            string now = flow + "|" + state + "|" + name;
                            string key = flow + "|" + id;
                            if (seen.TryGetValue(key, out string prev))
                            {
                                if (prev != now)
                                {
                                    Console.WriteLine("[" + Elapsed(start) + "] 变化 " + prev + " => " + now);
                                    seen[key] = now;
                                }
                            }
                            else
                            {
                                seen[key] = now;
                                bool interesting = id.IndexOf("BTHENUM", StringComparison.OrdinalIgnoreCase) >= 0
                                    || name.IndexOf("A2DP", StringComparison.OrdinalIgnoreCase) >= 0
                                    || name.IndexOf("Redmi", StringComparison.OrdinalIgnoreCase) >= 0;
                                if (interesting)
                                {
                                    Console.WriteLine("[" + Elapsed(start) + "] 新端点 " + now);
                                    Console.WriteLine("      id=" + id);
                                }
                            }
                        }
                    }
                }

                Console.WriteLine("[" + Elapsed(start) + "] 峰值=" + Peak().ToString("0.0000") + " 默认输出=" + DefaultRenderName());
                Thread.Sleep(2000);
            }

            Console.WriteLine("观察结束。");
            return 0;
        }

        private static int Meter(int seconds)
        {
            Console.WriteLine("读取默认输出端点峰值 " + seconds + " 秒。请在电脑上播放任意音频验证读表是否工作。");
            for (int i = 0; i < seconds; i++)
            {
                Console.WriteLine("  t=" + i + "s 峰值=" + Peak().ToString("0.0000") + " 端点=" + DefaultRenderName());
                Thread.Sleep(1000);
            }
            return 0;
        }

        private static int Endpoints()
        {
            using (MMDeviceEnumerator en = new MMDeviceEnumerator())
            {
                List<string> lines = new List<string>();
                foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
                {
                    MMDeviceCollection coll;
                    try { coll = en.EnumerateAudioEndPoints(flow, DeviceState.All); }
                    catch { continue; }

                    foreach (MMDevice dev in coll)
                    {
                        string id;
                        try { id = dev.ID; } catch { continue; }
                        string name;
                        try { name = dev.FriendlyName; } catch { name = "(读取失败)"; }
                        string state = "(?)";
                        try { state = dev.State.ToString(); } catch { }

                        bool isVirtual = VirtualMarkers.Any(m => name.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);
                        bool isBt = id.IndexOf("BTHENUM", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("A2DP", StringComparison.OrdinalIgnoreCase) >= 0;

                        string tag = isBt ? "[蓝牙]" : (isVirtual ? "[虚拟]" : "      ");
                        lines.Add(tag + " " + flow + " | " + state + " | " + name);
                    }
                }

                lines.Sort(StringComparer.OrdinalIgnoreCase);
                Console.WriteLine("音频端点共 " + lines.Count + " 个：");
                foreach (string line in lines)
                {
                    Console.WriteLine("  " + line);
                }
            }
            return 0;
        }

        private static float Peak()
        {
            try
            {
                using (MMDeviceEnumerator en = new MMDeviceEnumerator())
                {
                    MMDevice d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    return d.AudioMeterInformation.MasterPeakValue;
                }
            }
            catch
            {
                return -1f;
            }
        }

        private static string DefaultRenderName()
        {
            try
            {
                using (MMDeviceEnumerator en = new MMDeviceEnumerator())
                {
                    return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
                }
            }
            catch
            {
                return "(读取失败)";
            }
        }

        private static string Elapsed(DateTime start)
        {
            return (DateTime.Now - start).TotalSeconds.ToString("0.0").PadLeft(6) + "s";
        }
    }
}
