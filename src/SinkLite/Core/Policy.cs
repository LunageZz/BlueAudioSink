using System;

namespace SinkLite.Core
{
    /// <summary>
    /// 全部可调参数集中在此，便于按实测数据调参而不散落在业务代码里。
    /// </summary>
    internal sealed class Policy
    {
        public static Policy Default { get; } = new Policy();

        /// <summary>StartAsync（武装）超时。</summary>
        public TimeSpan ArmTimeout { get; set; } = TimeSpan.FromSeconds(8);

        /// <summary>OpenAsync 超时。</summary>
        public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>OpenAsync 成功后的稳定观察时间，用于过滤"秒断"。</summary>
        public TimeSpan OpenStabilityDelay { get; set; } = TimeSpan.FromMilliseconds(400);

        /// <summary>失败后的退避序列。</summary>
        public TimeSpan[] BackoffSteps { get; set; } = new[]
        {
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(30)
        };

        /// <summary>单次连接流程内最多自动重试次数（超过则转待机，把发起权交还手机）。</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>已就绪但长时间没有手机端点出现 -> 提示用户检查手机侧。</summary>
        public TimeSpan LinkWaitHintDelay { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>有端点但长时间没有声音 -> 提示可能播到别的输出设备。</summary>
        public TimeSpan FlowWaitHintDelay { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>主循环采样间隔（低频，避免无谓 CPU 占用）。</summary>
        public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>峰值判定阈值，低于此值认为无声。</summary>
        public float PeakThreshold { get; set; } = 0.0005f;

        /// <summary>断开时等待释放的超时。</summary>
        public TimeSpan ReleaseTimeout { get; set; } = TimeSpan.FromSeconds(5);
    }
}
