using System;
using System.Collections.Generic;
using Microsoft.Win32;
using SinkLite.Core;

namespace SinkLite.Platform
{
    /// <summary>
    /// 从注册表枚举音频端点。
    ///
    /// 为什么不用 NAudio 枚举：实测 NAudio 的 EnumerateAudioEndPoints + MMDevice.Dispose()
    /// 仍会为每个端点泄漏一个 COM 句柄（48 个端点每次约 +48 句柄，
    /// 每秒多次轮询时句柄会以 14/秒 增长，长跑后拖垮蓝牙栈）。
    /// 注册表读取不产生 COM 对象，因此零泄漏。
    ///
    /// 端点 ID 规则与 Core Audio 一致：
    ///   Render  -> {0.0.0.00000000}.{GUID}
    ///   Capture -> {0.0.1.00000000}.{GUID}
    /// </summary>
    internal static class EndpointRegistry
    {
        private const string Root =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";

        // 注册表把端点名称拆成两个字符串存储，Core Audio 的 FriendlyName 是它们的组合：
        //   {a45c254e-...},2  -> 设备类型，如“头戴式耳机”
        //   {b3f8fa53-...},6  -> 设备名，如“H180 Plus (Type C)”
        // 拼装结果为“头戴式耳机 (H180 Plus (Type C))”，与 NAudio 的 FriendlyName 一致。
        private const string DeviceTypeKey =
            "{a45c254e-df1c-4efd-8020-67d146a850e0},2";

        private const string DeviceNameKey =
            "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

        private static readonly string[] VirtualMarkers = new[]
        {
            "VB-Audio", "CABLE Input", "CABLE Output", "Steam Streaming",
            "虚拟音频", "Nahimic", "WO Mic", "网易"
        };

        private const int StateActive = 1;

        /// <summary>枚举全部音频端点（含未插入/禁用）。</summary>
        public static List<EndpointInfo> EnumerateAll()
        {
            List<EndpointInfo> list = new List<EndpointInfo>(64);
            Collect("Render", "{0.0.0.00000000}.", true, list);
            Collect("Capture", "{0.0.1.00000000}.", false, list);
            return list;
        }

        /// <summary>枚举处于活动状态的物理渲染端点（用于输出设备选择）。</summary>
        public static List<EndpointInfo> EnumerateActivePhysicalRenders()
        {
            List<EndpointInfo> result = new List<EndpointInfo>(16);
            foreach (EndpointInfo e in EnumerateAll())
            {
                if (e.IsRender && e.IsActive && !e.IsVirtual)
                {
                    result.Add(e);
                }
            }
            return result;
        }

        private static void Collect(string flowName, string idPrefix, bool isRender, List<EndpointInfo> list)
        {
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(Root + "\\" + flowName))
                {
                    if (root == null)
                    {
                        return;
                    }

                    foreach (string guid in root.GetSubKeyNames())
                    {
                        if (string.IsNullOrEmpty(guid) || guid[0] != '{')
                        {
                            continue;
                        }

                        using (RegistryKey device = root.OpenSubKey(guid))
                        {
                            if (device == null)
                            {
                                continue;
                            }

                            int state = 0;
                            try
                            {
                                object raw = device.GetValue("DeviceState");
                                if (raw != null)
                                {
                                    state = Convert.ToInt32(raw);
                                }
                            }
                            catch
                            {
                            }

                            string name = string.Empty;
                            using (RegistryKey props = device.OpenSubKey("Properties"))
                            {
                                if (props != null)
                                {
                                    name = BuildFriendlyName(props);
                                }
                            }

                            string id = idPrefix + guid;
                            list.Add(new EndpointInfo
                            {
                                Id = id,
                                Name = name,
                                IsActive = (state & StateActive) == StateActive,
                                IsRender = isRender,
                                IsBluetoothA2dp = Contains(id, "BTHENUM") ||
                                                  Contains(name, "A2DP") || Contains(name, "A2dp"),
                                IsVirtual = ContainsAny(name, VirtualMarkers) || ContainsAny(id, VirtualMarkers)
                            });
                        }
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>把“设备类型”与“设备名”拼成 Core Audio 风格的 FriendlyName。</summary>
        private static string BuildFriendlyName(RegistryKey props)
        {
            string type = ReadString(props, DeviceTypeKey);
            string deviceName = ReadString(props, DeviceNameKey);

            if (string.IsNullOrEmpty(type))
            {
                return deviceName ?? string.Empty;
            }

            if (string.IsNullOrEmpty(deviceName) ||
                string.Equals(type, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }

            return type + " (" + deviceName + ")";
        }

        private static string ReadString(RegistryKey props, string key)
        {
            try
            {
                object raw = props.GetValue(key);
                if (raw == null)
                {
                    return null;
                }

                // 某些属性以 PROPVARIANT 二进制存储，这里只取可直接转为字符串的情况
                if (raw is byte[])
                {
                    return null;
                }

                string value = raw.ToString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        private static bool Contains(string text, string token)
        {
            return !string.IsNullOrEmpty(text) &&
                   text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsAny(string text, string[] tokens)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (string token in tokens)
            {
                if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
