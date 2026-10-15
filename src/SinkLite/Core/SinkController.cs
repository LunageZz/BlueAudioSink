using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SinkLite.Platform;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace SinkLite.Core
{
    /// <summary>
    /// 四段状态机。核心设计针对原实现的两个已观测故障：
    ///   1) 连接失败：连接对象持久化，绝不为重试而销毁重建；失败按可重试性分类并退避。
    ///   2) 连上没声音：显式区分控制层 / 链路层 / 音频层，界面不再把控制层成功当成功。
    /// 所有状态变更只在主循环线程发生，外部命令经队列串行化。
    /// </summary>
    internal sealed class SinkController : IDisposable
    {
        private sealed class Command
        {
            public string Kind;
            public string DeviceId;
        }

        private readonly Policy policy;
        private readonly Log log;
        private readonly FlowMonitor flow;

        private readonly Channel<Command> commands =
            Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });

        private readonly object stateSync = new object();

        private SinkSnapshot current = new SinkSnapshot();
        private IReadOnlyList<SinkDevice> devices = new List<SinkDevice>();
        private CancellationTokenSource loopCancellation;
        private Task loopTask;

        // 仅在主循环线程访问
        private AudioPlaybackConnection connection;
        private string armedDeviceId;
        private bool wantConnected;
        private int retryCount;
        private DateTime? retryAt;
        private DateTime? armedAt;
        private DateTime? linkReadyAt;
        private bool linkHinted;
        private bool flowHinted;
        private bool faultReported;
        private bool devicesEnumerated;
        private DateTime lastDeviceScan;
        private string lastEndpointName;
        private string lastRenderName;

        public SinkController(Policy policy, Log log)
        {
            this.policy = policy;
            this.log = log;
            flow = new FlowMonitor(policy);
        }

        public event EventHandler<SinkSnapshot> SnapshotChanged;

        public SinkSnapshot Current
        {
            get
            {
                lock (stateSync)
                {
                    return current.Clone();
                }
            }
        }

        public IReadOnlyList<SinkDevice> Devices
        {
            get
            {
                lock (stateSync)
                {
                    return devices;
                }
            }
        }

        public void Start()
        {
            loopCancellation = new CancellationTokenSource();
            loopTask = Task.Run(() => RunLoopAsync(loopCancellation.Token));
            log.Info("App.Start", ("version", "0.1.0"));
        }

        public void RequestConnect(string deviceId)
        {
            commands.Writer.TryWrite(new Command { Kind = "connect", DeviceId = deviceId });
        }

        public void RequestDisconnect()
        {
            commands.Writer.TryWrite(new Command { Kind = "disconnect" });
        }

        public void RequestRefreshDevices()
        {
            commands.Writer.TryWrite(new Command { Kind = "refresh" });
        }

        public void RequestShutdown()
        {
            commands.Writer.TryWrite(new Command { Kind = "shutdown" });
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            try
            {
                InitializeWinRt();
                await RefreshDevicesAsync();
            }
            catch (Exception ex)
            {
                log.Error("Loop.InitFailed", ("error", ex.Message));
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await DrainCommandsAsync();

                    if (DateTime.Now - lastDeviceScan > TimeSpan.FromSeconds(30))
                    {
                        lastDeviceScan = DateTime.Now;
                        await RefreshDevicesAsync();
                    }

                    await StepAsync();
                }
                catch (Exception ex)
                {
                    log.Error("Loop.IterationFailed", ("error", ex.Message));
                }

                try
                {
                    await Task.Delay(policy.SampleInterval, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Cleanup();
        }

        private async Task DrainCommandsAsync()
        {
            while (commands.Reader.TryRead(out Command command))
            {
                switch (command.Kind)
                {
                    case "connect":
                        await HandleConnectAsync(command.DeviceId);
                        break;
                    case "disconnect":
                        await HandleDisconnectAsync();
                        break;
                    case "refresh":
                        await RefreshDevicesAsync();
                        break;
                    case "shutdown":
                        HandleDisconnectAsync().GetAwaiter().GetResult();
                        return;
                }
            }
        }

        private void InitializeWinRt()
        {
            log.Info("WinRt.Ready");
        }

        private async Task RefreshDevicesAsync()
        {
            try
            {
                string selector = AudioPlaybackConnection.GetDeviceSelector();
                DeviceInformationCollection found = await DeviceInformation.FindAllAsync(selector);
                List<SinkDevice> list = new List<SinkDevice>();
                foreach (DeviceInformation info in found)
                {
                    list.Add(new SinkDevice(info.Id, info.Name));
                }

                lock (stateSync)
                {
                    devices = list;
                }

                devicesEnumerated = list.Count > 0;
                log.Info("Devices.Enumerated", ("count", list.Count));

                if (list.Count > 0 && string.IsNullOrEmpty(SnapshotDeviceId))
                {
                    SetSnapshot(s =>
                    {
                        s.DeviceId = list[0].Id;
                        s.DeviceName = list[0].Name;
                        s.Detail = "已发现设备，可连接";
                    });
                }
            }
            catch (Exception ex)
            {
                log.Error("Devices.EnumerateFailed", ("error", ex.Message));
            }
        }

        private string SnapshotDeviceId
        {
            get
            {
                lock (stateSync)
                {
                    return current.DeviceId;
                }
            }
        }

        private async Task HandleConnectAsync(string deviceId)
        {
            SinkDevice target = null;
            if (!string.IsNullOrEmpty(deviceId))
            {
                target = Devices.FirstOrDefault(d => d.Id == deviceId);
            }
            if (target == null)
            {
                target = Devices.FirstOrDefault();
            }
            if (target == null)
            {
                await RefreshDevicesAsync();
                target = Devices.FirstOrDefault();
            }

            if (target == null)
            {
                SetSnapshot(s =>
                {
                    s.State = SinkState.Faulted;
                    s.Detail = "没有可用的音频接收设备";
                    s.Hint = "请先在 Windows 蓝牙设置中配对手机。";
                });
                log.Warn("Connect.NoDevice");
                return;
            }

            wantConnected = true;
            retryCount = 0;
            retryAt = null;
            faultReported = false;
            SetTarget(target);
            log.Info("Connect.Requested", ("device", target.Name));
            await ConnectInternalAsync(target);
        }

        private void SetTarget(SinkDevice target)
        {
            SetSnapshot(s =>
            {
                s.DeviceId = target.Id;
                s.DeviceName = target.Name;
            });
        }

        private async Task HandleDisconnectAsync()
        {
            wantConnected = false;
            retryAt = null;
            retryCount = 0;
            await ReleaseAsync("user disconnect");
            SetSnapshot(s =>
            {
                s.State = SinkState.Idle;
                s.Detail = "未连接";
                s.Hint = null;
                s.Peak = 0f;
            });
        }

        private async Task ConnectInternalAsync(SinkDevice target)
        {
            SetSnapshot(s =>
            {
                s.State = SinkState.Busy;
                s.Attempt = retryCount + 1;
                s.Detail = "正在连接…";
                s.Hint = null;
                s.LastErrorCode = null;
            });

            // 1) 获取或复用连接对象：整个进程生命周期内每个设备只创建一个。
            AudioPlaybackConnection conn = connection;
            bool freshlyCreated = false;
            if (conn == null || armedDeviceId != target.Id)
            {
                try
                {
                    conn = AudioPlaybackConnection.TryCreateFromId(target.Id);
                }
                catch (Exception ex)
                {
                    Fail("CreateException", ex.Message);
                    return;
                }

                if (conn == null)
                {
                    Fail("NotSupported", "设备不支持蓝牙音频接收");
                    return;
                }

                connection = conn;
                armedDeviceId = target.Id;
                freshlyCreated = true;
            }

            // 2) 仅在首次创建时武装（StartAsync）。重试不重复武装，避免与手机端协商互相打断。
            if (freshlyCreated)
            {
                try
                {
                    Task armTask = conn.StartAsync().AsTask();
                    Task completed = await Task.WhenAny(armTask, Task.Delay(policy.ArmTimeout));
                    if (completed != armTask)
                    {
                        Fail("ArmTimeout", "武装接收通道超时");
                        return;
                    }
                    await armTask;
                    log.Info("Connect.Armed", ("device", target.Name));
                }
                catch (Exception ex)
                {
                    Fail("ArmException", ex.Message);
                    return;
                }
            }

            // 3) 打开连接。失败按可重试性分类。
            AudioPlaybackConnectionOpenResultStatus status;
            try
            {
                var openOperation = conn.OpenAsync();
                Task<AudioPlaybackConnectionOpenResult> openTask = openOperation.AsTask();
                Task done = await Task.WhenAny(openTask, Task.Delay(policy.OpenTimeout));
                if (done != openTask)
                {
                    try
                    {
                        openOperation.Cancel();
                    }
                    catch
                    {
                    }
                    Fail("OpenTimeout", "打开连接超时");
                    return;
                }

                status = (await openTask).Status;
            }
            catch (Exception ex)
            {
                Fail("OpenException", ex.Message);
                return;
            }

            if (status != AudioPlaybackConnectionOpenResultStatus.Success)
            {
                Fail("OpenStatus." + status, "打开连接失败：" + status);
                return;
            }

            // 4) 稳定观察：过滤"秒断"。
            await Task.Delay(policy.OpenStabilityDelay);
            if (conn.State != AudioPlaybackConnectionState.Opened)
            {
                Fail("NotOpened", "连接未能保持打开状态");
                return;
            }

            retryCount = 0;
            retryAt = null;
            armedAt = DateTime.Now;
            linkReadyAt = null;
            linkHinted = false;
            flowHinted = false;
            lastEndpointName = null;
            lastRenderName = null;

            bool virtualWarning = false;
            EndpointInfo render = new EndpointWatcher().GetDefaultRender();
            if (render != null && render.IsVirtual)
            {
                virtualWarning = true;
            }

            SetSnapshot(s =>
            {
                s.State = SinkState.Armed;
                s.Detail = "已就绪，等待手机发送音频";
                s.Peak = 0f;
                s.Attempt = 0;
                s.LastErrorCode = null;
                s.Hint = virtualWarning
                    ? "注意：当前默认输出是虚拟声卡（" + render.Name + "），手机声音可能听不到。"
                    : "请在手机上播放音乐以开始传输。";
            });
            log.Info("Connect.Armed", ("device", target.Name), ("fresh", freshlyCreated));
        }

        /// <summary>每个采样周期执行一次：端点归因、峰值判定、退避推进。</summary>
        private async Task StepAsync()
        {
            if (!wantConnected)
            {
                return;
            }

            if (connection == null)
            {
                return;
            }

            if (retryAt.HasValue && DateTime.Now >= retryAt.Value)
            {
                retryAt = null;
                SinkDevice target = Devices.FirstOrDefault(d => d.Id == armedDeviceId);
                if (target != null)
                {
                    await ConnectInternalAsync(target);
                    return;
                }
            }

            FlowFinding finding = flow.Sample();

            if (!string.IsNullOrEmpty(finding.RenderEndpointName) &&
                finding.RenderEndpointName != lastRenderName)
            {
                lastRenderName = finding.RenderEndpointName;
                log.Info("Audio.DefaultRender", ("name", finding.RenderEndpointName), ("virtual", finding.RenderIsVirtual));
            }

            bool hasLink = finding.HasBluetoothEndpoint;
            bool flowing = finding.AudioFlowing;

            if (hasLink && lastEndpointName != finding.BluetoothEndpointName)
            {
                lastEndpointName = finding.BluetoothEndpointName;
                log.Info("Audio.LinkEndpointAppeared", ("name", finding.BluetoothEndpointName));
            }
            else if (!hasLink && !string.IsNullOrEmpty(lastEndpointName))
            {
                log.Info("Audio.LinkEndpointGone", ("name", lastEndpointName));
                lastEndpointName = null;
            }

            if (flowing)
            {
                if (Current.State != SinkState.Flowing)
                {
                    linkReadyAt = linkReadyAt ?? DateTime.Now;
                    SetSnapshot(s =>
                    {
                        s.State = SinkState.Flowing;
                        s.Detail = "音频传输中";
                        s.Hint = null;
                        s.Peak = finding.Peak;
                    });
                    log.Info("Audio.Flowing", ("peak", finding.Peak), ("render", finding.RenderEndpointName));
                }
                else
                {
                    SetSnapshot(s => s.Peak = finding.Peak);
                }
                flowHinted = false;
                return;
            }

            SetSnapshot(s => s.Peak = 0f);

            if (hasLink)
            {
                linkReadyAt = linkReadyAt ?? DateTime.Now;
                if (Current.State != SinkState.LinkReady && Current.State != SinkState.Flowing)
                {
                    SetSnapshot(s =>
                    {
                        s.State = SinkState.LinkReady;
                        s.Detail = "已建立音频链路，等待播放";
                        s.Hint = "若手机正在播放却听不到，请检查是否被其他输出设备抢占。";
                    });
                    log.Info("Link.Ready", ("endpoint", finding.BluetoothEndpointName));
                }

                if (!flowHinted &&
                    linkReadyAt.HasValue &&
                    DateTime.Now - linkReadyAt.Value > policy.FlowWaitHintDelay &&
                    finding.RenderIsVirtual)
                {
                    flowHinted = true;
                    SetSnapshot(s =>
                    {
                        s.State = SinkState.LinkReady;
                        s.Detail = "音频链路已建立，但输出指向虚拟声卡";
                        s.Hint = "当前默认输出是 " + finding.RenderEndpointName + "，请切换到真实扬声器或耳机。";
                    });
                    log.Warn("Link.VirtualRender", ("render", finding.RenderEndpointName));
                }
                return;
            }

            // 已就绪但没有端点：这正是原方案显示"已连接"却无声的真实状态。
            if (!linkHinted &&
                armedAt.HasValue &&
                DateTime.Now - armedAt.Value > policy.LinkWaitHintDelay)
            {
                linkHinted = true;
                SetSnapshot(s =>
                {
                    s.State = SinkState.Armed;
                    s.Detail = "控制层已就绪，手机尚未发送音频";
                    s.Hint = "请确认：① 手机正在播放；② 手机蓝牙输出指向本机；③ Windows 蓝牙设备属性允许该设备连接用于音频。";
                });
                log.Warn("Link.WaitingForPhone", ("armedFor", (DateTime.Now - armedAt.Value).TotalSeconds));
            }
        }

        private void Fail(string code, string message)
        {
            retryCount++;
            log.Warn("Connect.Failed", ("code", code), ("message", message), ("attempt", retryCount));

            // 致命错误：不再重试，避免无意义拆连把手机端也拖乱。
            bool fatal = code == "NotSupported" || code == "CreateException" || code.StartsWith("OpenStatus.");
            int maxRetries = fatal ? Math.Min(policy.MaxRetries, 1) : policy.MaxRetries;

            if (retryCount <= maxRetries)
            {
                int index = Math.Min(retryCount - 1, policy.BackoffSteps.Length - 1);
                TimeSpan delay = policy.BackoffSteps[index];
                retryAt = DateTime.Now + delay;
                SetSnapshot(s =>
                {
                    s.State = SinkState.Backoff;
                    s.Attempt = retryCount;
                    s.LastErrorCode = code;
                    s.Detail = "连接失败，正在重试（第 " + retryCount + " 次）";
                    s.Hint = message;
                });
                return;
            }

            // 重试耗尽：转为待机，把发起权交还手机与 Windows 自动连接，而不是死循环重连。
            wantConnected = false;
            retryAt = null;
            SetSnapshot(s =>
            {
                s.State = SinkState.Faulted;
                s.Attempt = retryCount;
                s.LastErrorCode = code;
                s.Detail = "连接失败，已转入待机";
                s.Hint = BuildHint(code, message);
            });
            if (!faultReported)
            {
                faultReported = true;
                log.Error("Connect.GaveUp", ("code", code), ("attempts", retryCount));
            }
        }

        private static string BuildHint(string code, string message)
        {
            switch (code)
            {
                case "OpenTimeout":
                    return "手机可能未响应。请把手机靠近电脑、唤醒屏幕后重试。";
                case "ArmTimeout":
                    return "蓝牙栈无响应。可尝试重新开关电脑蓝牙后重试。";
                case "NotSupported":
                    return "该设备不支持蓝牙音频接收，请换用已配对的手机。";
                default:
                    return "请确认手机在附近且蓝牙已开启；若反复失败，请重启电脑蓝牙适配器。错误：" + code;
            }
        }

        private async Task ReleaseAsync(string reason)
        {
            AudioPlaybackConnection conn = connection;
            connection = null;
            armedDeviceId = null;
            armedAt = null;
            linkReadyAt = null;
            linkHinted = false;
            flowHinted = false;

            if (conn == null)
            {
                return;
            }

            try
            {
                Task disposeTask = Task.Run(() =>
                {
                    try
                    {
                        conn.Dispose();
                    }
                    catch
                    {
                    }
                });
                Task done = await Task.WhenAny(disposeTask, Task.Delay(policy.ReleaseTimeout));
                log.Info("Connect.Released", ("reason", reason), ("clean", done == disposeTask));
            }
            catch (Exception ex)
            {
                log.Warn("Connect.ReleaseFailed", ("reason", reason), ("error", ex.Message));
            }
        }

        private void Cleanup()
        {
            try
            {
                if (connection != null)
                {
                    connection.Dispose();
                    connection = null;
                }
            }
            catch
            {
            }

            try
            {
                flow.Dispose();
            }
            catch
            {
            }
        }

        private void SetSnapshot(Action<SinkSnapshot> mutate)
        {
            SinkSnapshot updated;
            lock (stateSync)
            {
                SinkSnapshot next = current.Clone();
                mutate(next);
                next.ChangedAt = DateTime.Now;
                current = next;
                updated = next.Clone();
            }

            try
            {
                SnapshotChanged?.Invoke(this, updated);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            try
            {
                loopCancellation?.Cancel();
            }
            catch
            {
            }

            try
            {
                loopTask?.Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }

            try
            {
                commands.Writer.TryComplete();
            }
            catch
            {
            }

            try
            {
                loopCancellation?.Dispose();
            }
            catch
            {
            }
        }
    }
}
