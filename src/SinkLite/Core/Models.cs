using System;

namespace SinkLite.Core
{
    /// <summary>
    /// 四段状态。这是本项目的核心修正：原方案只有"已连接/未连接"两态，
    /// 把控制层成功当成了音频成功，因此界面必然误导用户。
    /// </summary>
    public enum SinkState
    {
        /// <summary>未做任何事。</summary>
        Idle = 0,

        /// <summary>正在武装 / 打开。</summary>
        Busy = 1,

        /// <summary>控制层就绪：StartAsync + OpenAsync 成功，等待手机发送音频。</summary>
        Armed = 2,

        /// <summary>链路层就绪：手机 A2DP 音频端点已在系统中出现。</summary>
        LinkReady = 3,

        /// <summary>音频层确认：默认渲染端点峰值大于 0，声音确实到达。</summary>
        Flowing = 4,

        /// <summary>失败或需要人工处理，附带归因文案。</summary>
        Faulted = 5,

        /// <summary>退避重试中。</summary>
        Backoff = 6
    }

    internal sealed class SinkDevice
    {
        public SinkDevice(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }

        public string Name { get; }
    }

    internal sealed class SinkSnapshot
    {
        public SinkState State { get; set; } = SinkState.Idle;

        public string DeviceId { get; set; }

        public string DeviceName { get; set; }

        /// <summary>面向用户的归因说明，区分"已就绪"与"传输中"。</summary>
        public string Detail { get; set; } = "未连接";

        /// <summary>可操作建议，给托盘气泡使用。</summary>
        public string Hint { get; set; }

        public int Attempt { get; set; }

        public string LastErrorCode { get; set; }

        public float Peak { get; set; }

        public string RenderEndpoint { get; set; }

        public DateTime ChangedAt { get; set; } = DateTime.Now;

        public SinkSnapshot Clone()
        {
            return (SinkSnapshot)MemberwiseClone();
        }
    }

    /// <summary>音频端点观察记录。</summary>
    internal sealed class EndpointInfo
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public bool IsActive { get; set; }

        public bool IsRender { get; set; }

        /// <summary>是否为手机等蓝牙 A2DP 端点。</summary>
        public bool IsBluetoothA2dp { get; set; }

        /// <summary>是否为虚拟声卡（VB-CABLE / Steam / 虚拟音频设备等）。</summary>
        public bool IsVirtual { get; set; }
    }
}
