using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SinkLite.Core;
using SinkLite.Platform;

namespace SinkLite.Platform
{
    /// <summary>
    /// 默认音频输出端点控制。
    /// 用途：实测本机有 5 个活动渲染端点 + 3 个虚拟声卡，手机声音很容易落到
    /// 虚拟声卡或 USB-C 耳机上，表现为"连上了却没声音"。这里提供显式切换能力。
    /// 使用未公开的 IPolicyConfig（Windows 长期可用），失败时降级为提示。
    /// </summary>
    internal sealed class RouteControl
    {
        private static readonly string[] VirtualMarkers = new[]
        {
            "VB-Audio", "CABLE Input", "CABLE Output", "Steam Streaming",
            "虚拟音频", "Nahimic", "WO Mic", "网易"
        };

        private readonly EndpointWatcher watcher;

        public RouteControl(EndpointWatcher watcher)
        {
            this.watcher = watcher;
        }

        /// <summary>是否存在处于活动状态的虚拟渲染端点。</summary>
        public EndpointInfo GetVirtualRender()
        {
            return watcher.GetDefaultRender() is EndpointInfo d && d.IsVirtual ? d : null;
        }

        /// <summary>挑选一个"真实"的渲染端点：优先物理设备，排除虚拟声卡。</summary>
        public EndpointInfo PickPhysicalRender()
        {
            List<EndpointInfo> renders = EndpointRegistry.EnumerateActivePhysicalRenders();

            EndpointInfo physical = renders.FirstOrDefault(e => !e.IsVirtual && !e.IsBluetoothA2dp);
            if (physical != null)
            {
                return physical;
            }

            return renders.FirstOrDefault(e => !e.IsVirtual);
        }

        /// <summary>列出可选的物理输出端点。走注册表读取，避免 NAudio 枚举的句柄泄漏。</summary>
        public IReadOnlyList<EndpointInfo> ListPhysicalRenders()
        {
            return EndpointRegistry.EnumerateActivePhysicalRenders();
        }

        /// <summary>把默认多媒体/通信输出切换到指定端点。</summary>
        public bool SetDefaultRender(string deviceId, out string error)
        {
            error = null;
            try
            {
                IPolicyConfig config = (IPolicyConfig)new PolicyConfigClient();
                int hr1 = config.SetDefaultEndpoint(deviceId, 0);
                int hr2 = config.SetDefaultEndpoint(deviceId, 1);
                int hr3 = config.SetDefaultEndpoint(deviceId, 2);
                Marshal.ReleaseComObject(config);

                if (hr1 != 0 && hr2 != 0 && hr3 != 0)
                {
                    error = "HRESULT " + hr1;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool IsVirtualName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            foreach (string marker in VirtualMarkers)
            {
                if (name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceName, IntPtr format);

        [PreserveSig] int GetDeviceFormat(string deviceName, bool isDefault, IntPtr format);

        [PreserveSig] int ResetDeviceFormat(string deviceName);

        [PreserveSig] int SetDeviceFormat(string deviceName, IntPtr endpoint, IntPtr mix);

        [PreserveSig] int GetProcessingPeriod(string deviceName, bool isDefault, IntPtr def, IntPtr min);

        [PreserveSig] int SetProcessingPeriod(string deviceName, IntPtr period);

        [PreserveSig] int GetShareMode(string deviceName, IntPtr mode);

        [PreserveSig] int SetShareMode(string deviceName, IntPtr mode);

        [PreserveSig] int GetPropertyValue(string deviceName, bool fxStore, IntPtr key);

        [PreserveSig] int SetPropertyValue(string deviceName, bool fxStore, IntPtr key);

        [PreserveSig] int SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            int role);
    }
}
