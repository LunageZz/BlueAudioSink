using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using SinkLite.Core;

namespace SinkLite.Platform
{
    /// <summary>
    /// 音频端点观察器。依据实测：手机 A2DP 端点只在链路真正建立时才出现，
    /// 因此"端点是否出现"是判断链路层是否就绪的可靠信号。
    /// 这里采用低频轮询（2 秒）而非 COM 回调：实现简单、稳定，CPU 开销可忽略。
    /// </summary>
    internal sealed class EndpointWatcher : IDisposable
    {
        private static readonly string[] VirtualMarkers = new[]
        {
            "VB-Audio", "CABLE Input", "CABLE Output", "Steam Streaming",
            "虚拟音频", "Nahimic", "WO Mic", "网易"
        };

        private readonly MMDeviceEnumerator enumerator = new MMDeviceEnumerator();

        public IReadOnlyList<EndpointInfo> LastEndpoints { get; private set; } = new List<EndpointInfo>();

        public IReadOnlyList<EndpointInfo> Sample()
        {
            List<EndpointInfo> list = new List<EndpointInfo>(64);

            foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                MMDeviceCollection collection;
                try
                {
                    collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.All);
                }
                catch
                {
                    continue;
                }

                foreach (MMDevice device in collection)
                {
                    EndpointInfo info = Describe(device, flow);
                    if (info != null)
                    {
                        list.Add(info);
                    }
                }
            }

            LastEndpoints = list;
            return list;
        }

        private static EndpointInfo Describe(MMDevice device, DataFlow flow)
        {
            string id;
            try
            {
                id = device.ID;
            }
            catch
            {
                return null;
            }

            string name;
            try
            {
                name = device.FriendlyName;
            }
            catch
            {
                name = string.Empty;
            }

            bool active;
            try
            {
                active = device.State == DeviceState.Active;
            }
            catch
            {
                active = false;
            }

            return new EndpointInfo
            {
                Id = id,
                Name = name,
                IsActive = active,
                IsRender = flow == DataFlow.Render,
                IsBluetoothA2dp =
                    Contains(id, "BTHENUM") ||
                    Contains(name, "A2DP") ||
                    Contains(name, "A2dp"),
                IsVirtual = ContainsAny(name, VirtualMarkers) || ContainsAny(id, VirtualMarkers)
            };
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

        /// <summary>读取默认多媒体渲染端点；失败返回 null。</summary>
        public EndpointInfo GetDefaultRender()
        {
            try
            {
                MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return Describe(device, DataFlow.Render);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读取默认渲染端点的峰值电平；失败返回 -1。</summary>
        public float GetDefaultPeak()
        {
            try
            {
                MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return device.AudioMeterInformation.MasterPeakValue;
            }
            catch
            {
                return -1f;
            }
        }

        public void Dispose()
        {
            try
            {
                enumerator.Dispose();
            }
            catch
            {
            }
        }
    }
}
