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

        public FlowMonitor(Policy policy)
        {
            this.policy = policy;
        }

        public FlowFinding Sample()
        {
            FlowFinding finding = new FlowFinding();
            IReadOnlyList<EndpointInfo> endpoints = watcher.Sample();

            EndpointInfo bluetooth = endpoints.FirstOrDefault(e => e.IsBluetoothA2dp);
            if (bluetooth != null)
            {
                finding.HasBluetoothEndpoint = true;
                finding.BluetoothEndpointName = bluetooth.Name;
            }

            EndpointInfo render = watcher.GetDefaultRender();
            if (render != null)
            {
                finding.RenderEndpointName = render.Name;
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
