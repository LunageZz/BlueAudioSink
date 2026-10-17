using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using SinkLite.Core;

namespace SinkLite.Platform
{
    /// <summary>
    /// 音频端点观察器。
    ///
    /// 分工（依据实测结论）：
    ///   - 端点枚举走注册表（EndpointRegistry），完全不产生 COM 对象，避免句柄泄漏；
    ///   - 默认渲染端点的峰值读表走 NAudio。只对同一个 MMDevice 反复读
    ///     MasterPeakValue 不泄漏（实测 100 次句柄不变），
    ///     但每次访问 AudioMeterInformation 属性都会泄漏一个 COM 引用，
    ///     所以这里长期缓存 MMDevice 与 AudioMeterInformation。
    /// </summary>
    internal sealed class EndpointWatcher : IDisposable
    {
        private readonly MMDeviceEnumerator enumerator = new MMDeviceEnumerator();

        private MMDevice defaultDevice;
        private AudioMeterInformation defaultMeter;
        private string defaultDeviceId;

        /// <summary>枚举端点（注册表实现，零 COM 泄漏）。</summary>
        public IReadOnlyList<EndpointInfo> Sample()
        {
            return EndpointRegistry.EnumerateAll();
        }

        /// <summary>仅从端点列表判断是否存在蓝牙端点，不做任何枚举。</summary>
        public static bool HasBluetoothEndpoint(IReadOnlyList<EndpointInfo> endpoints, out string name)
        {
            name = null;
            if (endpoints == null)
            {
                return false;
            }

            foreach (EndpointInfo e in endpoints)
            {
                if (e.IsBluetoothA2dp)
                {
                    name = e.Name;
                    return true;
                }
            }
            return false;
        }

        /// <summary>读取默认渲染端点的峰值。命中缓存后近乎零成本且不泄漏。</summary>
        public float GetDefaultPeak()
        {
            try
            {
                MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device == null)
                {
                    return -1f;
                }

                string id;
                try
                {
                    id = device.ID;
                }
                catch
                {
                    device.Dispose();
                    return -1f;
                }

                if (defaultMeter == null ||
                    !string.Equals(id, defaultDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        defaultMeter = null;
                        if (defaultDevice != null)
                        {
                            defaultDevice.Dispose();
                        }
                    }
                    catch
                    {
                    }

                    defaultDevice = device;
                    defaultDeviceId = id;
                    try
                    {
                        defaultMeter = device.AudioMeterInformation;
                    }
                    catch
                    {
                        defaultMeter = null;
                        return -1f;
                    }
                }
                else
                {
                    // 未变化：释放临时对象，避免每秒一次泄漏
                    device.Dispose();
                }

                return defaultMeter.MasterPeakValue;
            }
            catch
            {
                return -1f;
            }
        }

        /// <summary>读取默认渲染端点信息：ID 取自 Core Audio，名称取自注册表。</summary>
        public EndpointInfo GetDefaultRender()
        {
            string id;
            try
            {
                MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device == null)
                {
                    return null;
                }
                id = device.ID;
                device.Dispose();
            }
            catch
            {
                return null;
            }

            try
            {
                foreach (EndpointInfo e in EndpointRegistry.EnumerateActivePhysicalRenders())
                {
                    if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        return e;
                    }
                }

                // 默认端点可能是虚拟设备或非活动，回退到全量枚举
                foreach (EndpointInfo e in EndpointRegistry.EnumerateAll())
                {
                    if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        return e;
                    }
                }
            }
            catch
            {
            }

            return new EndpointInfo
            {
                Id = id,
                Name = id,
                IsActive = true,
                IsRender = true
            };
        }

        public void Dispose()
        {
            try
            {
                defaultMeter = null;
                if (defaultDevice != null)
                {
                    defaultDevice.Dispose();
                    defaultDevice = null;
                }
                enumerator.Dispose();
            }
            catch
            {
            }
        }
    }
}
