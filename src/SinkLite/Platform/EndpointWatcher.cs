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

        // 端点列表缓存：注册表枚举虽便宜，但每秒做两次仍会产生可观的对象分配。
        private IReadOnlyList<EndpointInfo> cachedEndpoints;
        private DateTime cachedEndpointsAt = DateTime.MinValue;

        // 默认端点 ID 也缓存：NAudio 查询需要激活 COM 对象（约 14 ms）。
        private string cachedDefaultId;
        private DateTime cachedDefaultIdAt = DateTime.MinValue;

        private static readonly TimeSpan EndpointCacheLifetime = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DefaultIdCacheLifetime = TimeSpan.FromSeconds(3);

        /// <summary>强制枚举端点并刷新缓存（注册表实现，零 COM 泄漏）。</summary>
        public IReadOnlyList<EndpointInfo> Sample()
        {
            cachedEndpoints = EndpointRegistry.EnumerateAll();
            cachedEndpointsAt = DateTime.Now;
            return cachedEndpoints;
        }

        /// <summary>取端点列表，命中缓存则不枚举。</summary>
        private IReadOnlyList<EndpointInfo> GetEndpointsCached()
        {
            if (cachedEndpoints == null ||
                DateTime.Now - cachedEndpointsAt > EndpointCacheLifetime)
            {
                cachedEndpoints = EndpointRegistry.EnumerateAll();
                cachedEndpointsAt = DateTime.Now;
            }
            return cachedEndpoints;
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

        /// <summary>
        /// 读取默认渲染端点信息。
        /// 关键优化：端点列表与默认端点 ID 都走缓存，
        /// 避免每秒触发两次注册表全量枚举（每次约创建 48 个对象）。
        /// </summary>
        public EndpointInfo GetDefaultRender()
        {
            string id = GetDefaultIdCached();
            if (id == null)
            {
                return null;
            }

            try
            {
                foreach (EndpointInfo e in GetEndpointsCached())
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

        private string GetDefaultIdCached()
        {
            if (cachedDefaultId != null &&
                DateTime.Now - cachedDefaultIdAt <= DefaultIdCacheLifetime)
            {
                return cachedDefaultId;
            }

            try
            {
                MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device == null)
                {
                    return null;
                }

                cachedDefaultId = device.ID;
                cachedDefaultIdAt = DateTime.Now;
                device.Dispose();
                return cachedDefaultId;
            }
            catch
            {
                return null;
            }
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
