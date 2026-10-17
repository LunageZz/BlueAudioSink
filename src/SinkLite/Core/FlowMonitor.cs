using System;
using System.Collections.Generic;
using System.Linq;
using SinkLite.Platform;

namespace SinkLite.Core
{
    /// <summary>三层归因结果：控制层 / 链路层 / 音频层。</summary>
    internal sealed class FlowFinding
    {
        public bool HasBluetoothEndpoint { get; set; }

        public string BluetoothEndpointName { get; set; }

        public float Peak { get; set; }

        public string RenderEndpointName { get; set; }

        public string RenderEndpointId { get; set; }

        public bool RenderIsVirtual { get; set; }

        public bool AudioFlowing { get; set; }
    }

    /// <summary>
    /// 音频流向归因。依据实测结论：
    ///   - 控制层成功（OpenAsync=Success）不代表音频已开始传输；
    ///   - 手机 A2DP 端点出现才代表链路层就绪；
    ///   - 默认渲染端点峰值大于阈值才代表音频层确认。
    /// </summary>
    internal sealed class FlowMonitor : IDisposable
    {
        private readonly EndpointWatcher watcher = new EndpointWatcher();
        private readonly Policy policy;

        // 端点枚举涉及 COM 接口激活，成本远高于缓存后的峰值读表。
        // 实测结论：枚举保留在每 5 个采样周期执行一次即可保证状态及时。
        // 注册表枚举成本低（约 1~3 ms），但仍不必每秒做，10 个采样周期一次足够。
        private const int EnumerateEverySamples = 10;
        private int sampleCounter;
        private System.Collections.Generic.IReadOnlyList<EndpointInfo> cachedEndpoints =
            new System.Collections.Generic.List<EndpointInfo>();

        public FlowMonitor(Policy policy)
        {
            this.policy = policy;
        }

        public FlowFinding Sample()
        {
            FlowFinding finding = new FlowFinding();
            bool enumerate = (sampleCounter++ % EnumerateEverySamples) == 0;

            if (enumerate)
            {
                cachedEndpoints = watcher.Sample();
            }

            string bluetoothName;
            if (EndpointWatcher.HasBluetoothEndpoint(cachedEndpoints, out bluetoothName))
            {
                finding.HasBluetoothEndpoint = true;
                finding.BluetoothEndpointName = bluetoothName;
            }

            EndpointInfo render = watcher.GetDefaultRender();
            if (render != null)
            {
                finding.RenderEndpointName = render.Name;
                finding.RenderEndpointId = render.Id;
                finding.RenderIsVirtual = render.IsVirtual;
            }

            float peak = watcher.GetDefaultPeak();
            finding.Peak = peak < 0f ? 0f : peak;
            finding.AudioFlowing = finding.Peak > policy.PeakThreshold;
            return finding;
        }

        public void Dispose()
        {
            watcher.Dispose();
        }
    }
}
