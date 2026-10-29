using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SinkLite.Platform;

namespace SinkLite.Core
{
    /// <summary>
    /// Wi-Fi 模式的 UDP 接收、抖动缓冲与播放调度。
    ///
    /// 协议实现要点（docs/02-protocol.md v1.1）：
    ///   - PC 绑定 0.0.0.0:45678（控制）与 0.0.0.0:45679（音频）；
    ///   - AUDIO 5 ms / 240 帧 / 960 字节，seq 200 包每秒；
    ///   - 按 d=(uint)(seq-expectedSeq) 判定正常 / 缺口 / 重复 / 迟到 / 重置；
    ///   - D = 已入队未播放时长，硬上限 200 ms；500 ms 评估，-5/+10 ms 调整；
    ///   - 缺口补静音；d>5 记断流并 5 ms 淡入；d>10 清队重缓冲；
    ///   - 欠载补静音并重缓冲到 lastTarget；STOP 排空 ≤500 ms；5 秒无 AUDIO 回 Armed。
    /// </summary>
    internal sealed class WifiReceiver : IDisposable
    {
        private const int TimeoutMs = 5000;
        private const int DrainTimeoutMs = 500;
        private const int LockWindowMs = 10000;
        private const int StatsIntervalMs = 5000;
        private const int AggregateIntervalMs = 1000;
        private const int JitterWindowMs = 2000;
        private const int PingIntervalMs = 2000;
        private const int MaxGapPackets = 10;

        private sealed class AudioFrame
        {
            public uint Seq;
            public byte[] Data;
            public bool Silence;
        }

        private struct JitterSample
        {
            public long Ticks;
            public double DeltaUs;
        }

        private struct LossSample
        {
            public long Ticks;
            public int Received;
            public int Missing;
        }

        private readonly object sync = new object();
        private readonly Log log;
        private readonly int controlPort;
        private readonly int audioPort;
        private readonly int baseMs;
        private readonly int maxMs;
        private readonly string deviceName;
        private readonly Stopwatch clock = Stopwatch.StartNew();

        private readonly List<AudioFrame> queue = new List<AudioFrame>(64);
        private readonly Queue<JitterSample> jitterSamples = new Queue<JitterSample>(512);
        private readonly Queue<LossSample> lossWindow = new Queue<LossSample>(512);

        private volatile bool running;
        private Socket controlSocket;
        private Socket audioSocket;
        private Thread controlThread;
        private Thread audioThread;
        private Thread playoutThread;
        private AudioOutput output;
        private AudioOutput pendingRelease;

        private SinkState state = SinkState.Armed;
        private string detail = "等待手机连接";
        private IPEndPoint remote;
        private IPEndPoint sessionEndpoint;
        private string remoteName;

        private bool expectedValid;
        private uint expectedSeq;
        private bool firstAudio;
        private bool playing;
        private bool draining;
        private bool rebuffering;
        private bool fadeInPending;
        private uint sessionStartUs;
        private bool hasSessionStart;

        private long stopTicks;
        private long lastAudioTicks;
        private long lastAnyTicks;
        private long prevArrivalTicks;
        private uint lastTimestampUs;
        private long nextDueTicks;
        private long lastAdaptTicks;
        private long lastStatsLogTicks;
        private long lastAggregateLogTicks;
        private int targetMs;

        private double jitterUs;
        private bool prevAudioValid;
        private uint prevAudioSeq;
        private uint prevAudioTimestampUs;

        private double rttMs = double.NaN;
        private int clockOffsetUs;
        private bool offsetValid;
        private long lastPingTicks;
        private uint lastPingSendUs;
        private long lastPongTicks;

        private long received;
        private long missing;
        private long duplicates;
        private long late;
        private long foreign;
        private long malformed;
        private long unknown;
        private long resets;
        private long discontinuities;
        private long underruns;
        private long dropped;
        private long stale;

        private int gapPending;
        private uint gapLastSeq;
        private int malformedPending;
        private string malformedReason;
        private int underrunPending;
        private int rebufferPending;
        private int lastRebufferMs;

        public WifiReceiver(Log log, int controlPort, int audioPort, int bufferBaseMs, int bufferMaxMs, string deviceName)
        {
            this.log = log;
            this.controlPort = controlPort;
            this.audioPort = audioPort;
            baseMs = Math.Clamp(bufferBaseMs, 10, 800);
            maxMs = Math.Clamp(bufferMaxMs, baseMs, 1000);
            this.deviceName = string.IsNullOrWhiteSpace(deviceName) ? "BlueAudioSink" : deviceName;
            targetMs = baseMs;
        }

        public bool Start()
        {
            lock (sync)
            {
                if (running)
                {
                    return true;
                }

                Socket control = null;
                Socket audio = null;
                try
                {
                    control = BindSocket(controlPort, true);
                    audio = BindSocket(audioPort, false);
                    controlSocket = control;
                    audioSocket = audio;
                    running = true;

                    controlThread = new Thread(() => ReceiveLoop(control, true));
                    controlThread.IsBackground = true;
                    controlThread.Name = "WifiControl";
                    audioThread = new Thread(() => ReceiveLoop(audio, false));
                    audioThread.IsBackground = true;
                    audioThread.Name = "WifiAudio";
                    playoutThread = new Thread(PlayoutLoop);
                    playoutThread.IsBackground = true;
                    playoutThread.Name = "WifiPlayout";

                    controlThread.Start();
                    audioThread.Start();
                    playoutThread.Start();
                    state = SinkState.Armed;
                    detail = "等待手机连接";
                    return true;
                }
                catch (Exception ex)
                {
                    running = false;
                    controlSocket = null;
                    audioSocket = null;
                    CloseSocket(control);
                    CloseSocket(audio);
                    state = SinkState.Faulted;
                    detail = "端口 " + controlPort + "/" + audioPort + " 绑定失败";
                    log.Error(
                        "Wifi.BindFailed",
                        ("controlPort", controlPort),
                        ("audioPort", audioPort),
                        ("error", ex.Message));
                    return false;
                }
            }
        }

        public void Stop()
        {
            if (!running && controlSocket == null && audioSocket == null)
            {
                return;
            }

            running = false;
            CloseSocket(controlSocket);
            CloseSocket(audioSocket);
            JoinThread(controlThread, 1000);
            JoinThread(audioThread, 1000);
            JoinThread(playoutThread, 1500);

            AudioOutput release = null;
            AudioOutput pending = null;
            lock (sync)
            {
                ClearQueueLocked();
                release = output;
                output = null;
                pending = pendingRelease;
                pendingRelease = null;
                state = SinkState.Armed;
                detail = "Wi-Fi 已停止";
                playing = false;
                draining = false;
                rebuffering = false;
                expectedValid = false;
                sessionEndpoint = null;
                hasSessionStart = false;
                offsetValid = false;
                rttMs = double.NaN;
                lastPingTicks = 0;
                lastAudioTicks = 0;
            }

            if (release != null)
            {
                release.Dispose();
            }

            if (pending != null)
            {
                pending.Dispose();
            }

            controlSocket = null;
            audioSocket = null;
            controlThread = null;
            audioThread = null;
            playoutThread = null;
        }

        /// <summary>从控制 socket（源端口 45678）广播 OFFER。</summary>
        public bool SendOffer()
        {
            Socket socket = controlSocket;
            if (socket == null)
            {
                return false;
            }

            try
            {
                byte[] offer = WifiTransport.BuildOffer(deviceName, audioPort);
                socket.SendTo(offer, new IPEndPoint(IPAddress.Broadcast, controlPort));
                return true;
            }
            catch (Exception ex)
            {
                if (DateTime.Now - lastSendErrorLog > TimeSpan.FromSeconds(30))
                {
                    lastSendErrorLog = DateTime.Now;
                    log.Warn("Wifi.Fault", ("code", "OfferFailed"), ("detail", ex.Message));
                }
                return false;
            }
        }

        private DateTime lastSendErrorLog = DateTime.MinValue;

        private static Socket BindSocket(int port, bool broadcast)
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.ReceiveBufferSize = 512 * 1024;
            socket.ReceiveTimeout = 250;
            socket.EnableBroadcast = broadcast;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }

        private void ReceiveLoop(Socket socket, bool control)
        {
            byte[] buffer = new byte[2048];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);

            while (running)
            {
                try
                {
                    int length = socket.ReceiveFrom(buffer, ref any);
                    long now = clock.ElapsedTicks;
                    IPEndPoint from = any as IPEndPoint;
                    if (from != null)
                    {
                        HandlePacket(buffer, length, from, now, control);
                    }
                    any = new IPEndPoint(IPAddress.Any, 0);
                }
                catch (SocketException ex)
                {
                    if (!running)
                    {
                        break;
                    }

                    if (ex.SocketErrorCode == SocketError.TimedOut ||
                        ex.SocketErrorCode == SocketError.ConnectionReset ||
                        ex.SocketErrorCode == SocketError.MessageSize ||
                        ex.SocketErrorCode == SocketError.Interrupted)
                    {
                        continue;
                    }

                    FailNetworkLocked("ReceiveError", ex.SocketErrorCode + ": " + ex.Message);
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!running)
                    {
                        break;
                    }
                    FailNetworkLocked("ReceiveError", ex.Message);
                    break;
                }
            }
        }

        private void FailNetworkLocked(string code, string text)
        {
            lock (sync)
            {
                state = SinkState.Faulted;
                detail = "网络接收失败：" + text;
                DetachOutputLocked();
                playing = false;
            }
            log.Error("Wifi.Fault", ("code", code), ("detail", text));
        }

        // ===== 包解析与会话 =====

        private void HandlePacket(byte[] data, int length, IPEndPoint from, long now, bool control)
        {
            if (length < WifiTransport.HeaderSize)
            {
                AddMalformedLocked("short");
                return;
            }

            WifiTransport.WifiHeader header;
            if (!WifiTransport.TryReadHeader(data, length, out header))
            {
                AddMalformedLocked("magic");
                return;
            }

            switch (header.Type)
            {
                case WifiTransport.TypeAudio:
                    HandleAudio(data, length, header, from, now);
                    break;
                case WifiTransport.TypeStart:
                    HandleStart(header, from, now);
                    break;
                case WifiTransport.TypeStop:
                    HandleStop(header, from, now);
                    break;
                case WifiTransport.TypePing:
                    HandlePing(header, from, now);
                    break;
                case WifiTransport.TypePong:
                    HandlePong(data, length, header, from, now);
                    break;
                case WifiTransport.TypeHello:
                    HandleHello(data, length, from, now);
                    break;
                case WifiTransport.TypeOffer:
                    // 自己广播回来的 OFFER，忽略。
                    break;
                default:
                    lock (sync)
                    {
                        unknown++;
                        lastAnyTicks = now;
                    }
                    break;
            }
        }

        private void HandleAudio(byte[] data, int length, WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {
            if (length != WifiTransport.PacketBytes)
            {
                AddMalformedLocked("audio_length");
                return;
            }

            lock (sync)
            {
                lastAnyTicks = now;

                if (state == SinkState.Faulted)
                {
                    return;
                }

                if (draining)
                {
                    late++;
                    return;
                }

                if (sessionEndpoint == null)
                {
                    // START 丢失：Armed 下首个 AUDIO 自动建会话，基线取该包 seq。
                    sessionEndpoint = from;
                    remote = from;
                    BeginSessionLocked(now, false, 0);
                    log.Info("Wifi.Start", ("endpoint", from.ToString()), ("ts", header.TimestampUs), ("auto", true));
                }
                else if (!SameEndpoint(sessionEndpoint, from))
                {
                    foreign++;
                    return;
                }

                remote = from;

                if (hasSessionStart)
                {
                    int diff = unchecked((int)(header.TimestampUs - sessionStartUs));
                    if (diff < -100000)
                    {
                        stale++;
                        return;
                    }
                }

                int gap = AcceptSequenceLocked(header.Seq, now);
                if (gap < 0)
                {
                    return;
                }

                lastAudioTicks = now;
                lastTimestampUs = header.TimestampUs;

                byte[] rented = ArrayPool<byte>.Shared.Rent(WifiTransport.PayloadBytes);
                Buffer.BlockCopy(data, WifiTransport.HeaderSize, rented, 0, WifiTransport.PayloadBytes);
                AudioFrame frame = new AudioFrame();
                frame.Seq = header.Seq;
                frame.Data = rented;
                frame.Silence = false;
                queue.Add(frame);
                received++;
                AddLossSampleLocked(1, 0);

                UpdateJitterLocked(now, header);
                if (!firstAudio)
                {
                    firstAudio = true;
                    log.Info("Wifi.FirstAudio", ("seq", header.Seq), ("ts", header.TimestampUs));
                }

                if ((header.Flags & WifiTransport.FlagDiscontinuity) != 0 && !fadeInPending)
                {
                    fadeInPending = true;
                    discontinuities++;
                }

                if (state != SinkState.Flowing)
                {
                    state = SinkState.LinkReady;
                    detail = "预缓冲中";
                }

                EnforceHardCapLocked();
            }
        }

        /// <summary>
        /// 按协议 §7.1 处理 seq。返回 0 表示正常接受，-1 表示丢弃。
        /// 缺口时先把静音帧追加到队尾，再追加当前包。
        /// </summary>
        private int AcceptSequenceLocked(uint seq, long now)
        {
            if (!expectedValid)
            {
                expectedValid = true;
                expectedSeq = seq + 1;
                return 0;
            }

            uint d = unchecked(seq - expectedSeq);
            if (d == 0)
            {
                expectedSeq = seq + 1;
                return 0;
            }

            if (d <= MaxGapPackets)
            {
                for (uint i = 0; i < d; i++)
                {
                    AudioFrame silence = new AudioFrame();
                    silence.Seq = unchecked(expectedSeq + i);
                    silence.Silence = true;
                    queue.Add(silence);
                }

                missing += d;
                AddLossSampleLocked(0, (int)d);
                gapPending += (int)d;
                gapLastSeq = seq;
                fadeInPending = true;
                if (d > 5)
                {
                    discontinuities++;
                }

                expectedValid = true;
                expectedSeq = seq + 1;
                return (int)d;
            }

            if (d == uint.MaxValue)
            {
                duplicates++;
                return -1;
            }

            if (d >= uint.MaxValue - 9)
            {
                late++;
                return -1;
            }

            // 前向跳变 > 10：视为时间轴重置，不补静音。
            uint oldSeq = expectedSeq - 1;
            expectedValid = true;
            expectedSeq = seq + 1;
            ResetForSeqJumpLocked(seq, now);
            resets++;
            discontinuities++;
            log.Warn("Wifi.Reset", ("oldSeq", oldSeq), ("newSeq", seq), ("queuedMs", 0));
            return 0;
        }

        private void HandleStart(WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {
            lock (sync)
            {
                lastAnyTicks = now;
                sessionEndpoint = from;
                remote = from;
                draining = false;
                rebuffering = false;
                BeginSessionLocked(now, true, header.TimestampUs);
                state = SinkState.LinkReady;
                detail = "预缓冲中";
                lastAudioTicks = now;
            }

            log.Info(
                "Wifi.Start",
                ("endpoint", from == null ? "?" : from.ToString()),
                ("ts", header.TimestampUs),
                ("auto", false));
        }

        private void HandleStop(WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {
            bool finish = false;
            lock (sync)
            {
                lastAnyTicks = now;
                if (state == SinkState.Faulted || state == SinkState.Armed)
                {
                    return;
                }

                if (sessionEndpoint != null && from != null && !SameEndpoint(sessionEndpoint, from))
                {
                    foreign++;
                    return;
                }

                if (draining)
                {
                    return;
                }

                draining = true;
                rebuffering = false;
                stopTicks = now;
                state = SinkState.LinkReady;
                detail = "排空缓冲中";
                if (!playing && queue.Count == 0)
                {
                    finish = true;
                }
            }

            log.Info("Wifi.Stop", ("ts", header.TimestampUs));

            if (finish)
            {
                lock (sync)
                {
                    DrainDoneLocked(clock.ElapsedTicks);
                }
            }
        }

        private void HandlePing(WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {
            lock (sync)
            {
                lastAnyTicks = now;
                if (state == SinkState.Armed && remote == null)
                {
                    remote = from;
                }
            }

            // v1.1 正常由 PC 发 PING；兼容手机主动 PING：payload 回填其 T1，头填 T2。
            SendControlPacket(WifiTransport.BuildPong(header.TimestampUs, ToMicroseconds(now)), from);
        }

        private void HandlePong(byte[] data, int length, WifiTransport.WifiHeader header, IPEndPoint from, long now)
        {
            if (length != WifiTransport.HeaderSize + 4)
            {
                AddMalformedLocked("pong_length");
                return;
            }

            uint echoedUs = WifiTransport.ReadUInt32(data, WifiTransport.HeaderSize);
            lock (sync)
            {
                if (!IsCurrentEndpointLocked(from))
                {
                    return;
                }

                lastAnyTicks = now;
                if (lastPingTicks == 0 || echoedUs != lastPingSendUs)
                {
                    return;
                }

                uint nowUs = ToMicroseconds(now);
                uint rttUs = unchecked(nowUs - lastPingSendUs);
                if (rttUs == 0 || rttUs > 2000000)
                {
                    return;
                }

                uint offset = unchecked(header.TimestampUs - lastPingSendUs - rttUs / 2);
                clockOffsetUs = unchecked((int)offset);
                offsetValid = true;
                lastPongTicks = now;
                rttMs = rttUs / 1000.0;
                log.Info(
                    "Pong.Recv",
                    ("rttMs", Math.Round(rttMs, 1)),
                    ("offsetMs", Math.Round(clockOffsetUs / 1000.0, 1)));
            }
        }

        private void HandleHello(byte[] data, int length, IPEndPoint from, long now)
        {
            int payloadLength = length - WifiTransport.HeaderSize;
            if (payloadLength < 1 || payloadLength > 64)
            {
                AddMalformedLocked("hello_length");
                return;
            }

            string name = WifiTransport.ReadUtf8Z(data, WifiTransport.HeaderSize, payloadLength);
            bool changed = false;
            lock (sync)
            {
                lastAnyTicks = now;
                if (state != SinkState.Armed && sessionEndpoint != null && from != null &&
                    !SameEndpoint(sessionEndpoint, from))
                {
                    foreign++;
                    return;
                }

                if (state == SinkState.Armed && from != null)
                {
                    // 待机时 HELLO 更新 PING 候选与显示端点；不创建会话。
                    remote = from;
                }

                if (!string.IsNullOrEmpty(name) && name != remoteName)
                {
                    remoteName = name;
                    changed = true;
                }

                if (state == SinkState.Armed)
                {
                    detail = string.IsNullOrEmpty(remoteName) ? "已发现手机" : "已发现 " + remoteName;
                }
            }

            if (changed)
            {
                log.Info("Wifi.Hello", ("name", name), ("endpoint", from == null ? "?" : from.ToString()));
            }
        }

        private void AddMalformedLocked(string reason)
        {
            lock (sync)
            {
                malformed++;
                malformedPending++;
                malformedReason = reason;
            }
        }

        // ===== 播放线程 =====

        private void PlayoutLoop()
        {
            while (running)
            {
                long now = clock.ElapsedTicks;
                try
                {
                    bool needOpen = false;
                    AudioOutput release = null;
                    lock (sync)
                    {
                        needOpen = ProcessLocked(now);
                        release = pendingRelease;
                        pendingRelease = null;
                    }

                    // 释放 WasapiOut 可能阻塞数百毫秒甚至更久；在接收锁外执行，
                    // 避免 UDP 接收线程被卡住导致整段音频丢在 socket 缓冲区里。
                    if (release != null)
                    {
                        release.Dispose();
                    }

                    if (needOpen && running)
                    {
                        OpenOutput();
                    }

                    Housekeeping(now);
                }
                catch (Exception ex)
                {
                    if (!running)
                    {
                        break;
                    }
                    log.Error("Wifi.Fault", ("code", "PlayoutFailed"), ("detail", ex.Message));
                    Thread.Sleep(100);
                }

                Thread.Sleep(5);
            }
        }

        private void OpenOutput()
        {
            AudioOutput created = new AudioOutput(log);
            bool ok = created.EnsureInitialized();

            lock (sync)
            {
                if (ok && output == null && state != SinkState.Faulted && state != SinkState.Armed)
                {
                    output = created;
                    created = null;
                }
                else if (!ok)
                {
                    state = SinkState.Faulted;
                    detail = "音频输出不可用";
                }
            }

            if (created != null)
            {
                created.Dispose();
            }
        }

        private bool ProcessLocked(long now)
        {
            if (state == SinkState.Faulted)
            {
                DetachOutputLocked();
                playing = false;
                return false;
            }

            if (draining && stopTicks != 0 && ToMilliseconds(now - stopTicks) > DrainTimeoutMs)
            {
                DrainDoneLocked(now);
                return false;
            }

            if (state != SinkState.Armed && lastAudioTicks != 0 &&
                ToMilliseconds(now - lastAudioTicks) > TimeoutMs)
            {
                TimeoutLocked(now);
                return false;
            }

            if (state == SinkState.Armed)
            {
                DetachOutputLocked();
                playing = false;
                return false;
            }

            if (now - lastAdaptTicks >= TicksFromMs(500))
            {
                lastAdaptTicks = now;
                AdaptLocked(now);
            }

            if (!playing)
            {
                int depth = DepthLocked();
                bool ready = depth >= targetMs || (draining && depth > 0);
                if (!ready)
                {
                    if (draining && depth == 0)
                    {
                        DrainDoneLocked(now);
                    }
                    return false;
                }

                if (output == null)
                {
                    return true;
                }

                int prefill = draining ? queue.Count : Math.Min(queue.Count, Math.Max(1, targetMs / WifiTransport.FrameMs));
                MoveFramesToOutputLocked(prefill);
                output.Play();
                playing = true;
                nextDueTicks = now + TicksFromMs(WifiTransport.FrameMs);
                if (draining)
                {
                    state = SinkState.LinkReady;
                    detail = "排空缓冲中";
                }
                else
                {
                    state = SinkState.Flowing;
                    detail = "接收中";
                }
                log.Info("Wifi.Playing", ("bufferMs", DepthLocked()));
                return false;
            }

            if (rebuffering)
            {
                if (DepthLocked() >= targetMs)
                {
                    rebuffering = false;
                    state = SinkState.Flowing;
                    detail = "接收中";
                    nextDueTicks = now;
                    fadeInPending = true;
                    rebufferPending++;
                    lastRebufferMs = DepthLocked();
                }
                else
                {
                    return false;
                }
            }

            if (now >= nextDueTicks)
            {
                int guard = 0;
                while (now >= nextDueTicks && guard < 5)
                {
                    PumpFrameLocked(now);
                    nextDueTicks += TicksFromMs(WifiTransport.FrameMs);
                    guard++;
                }

                if (now - nextDueTicks > TicksFromMs(200))
                {
                    nextDueTicks = now;
                }
            }

            if (draining && queue.Count == 0 && (output == null || output.BufferedMs <= 5))
            {
                DrainDoneLocked(now);
            }

            return false;
        }

        private void PumpFrameLocked(long now)
        {
            if (queue.Count == 0)
            {
                if (draining)
                {
                    return;
                }

                if (!rebuffering)
                {
                    rebuffering = true;
                    underruns++;
                    underrunPending++;
                    state = SinkState.LinkReady;
                    detail = "欠载，重缓冲中";
                    fadeInPending = true;
                }
                return;
            }

            AudioFrame frame = queue[0];
            queue.RemoveAt(0);

            if (frame.Silence)
            {
                if (output != null)
                {
                    output.AddSilence();
                }
            }
            else if (output != null)
            {
                if (fadeInPending)
                {
                    fadeInPending = false;
                    output.AddFadeIn(frame.Data, 0, WifiTransport.PayloadBytes);
                }
                else
                {
                    output.Add(frame.Data, 0, WifiTransport.PayloadBytes);
                }
            }

            ReturnFrame(frame);
            if (!draining && state == SinkState.LinkReady && !rebuffering)
            {
                state = SinkState.Flowing;
                detail = "接收中";
            }
        }

        private void MoveFramesToOutputLocked(int count)
        {
            for (int i = 0; i < count && queue.Count > 0; i++)
            {
                AudioFrame frame = queue[0];
                queue.RemoveAt(0);
                if (output != null)
                {
                    if (frame.Silence)
                    {
                        output.AddSilence();
                    }
                    else
                    {
                        output.Add(frame.Data, 0, WifiTransport.PayloadBytes);
                    }
                }
                ReturnFrame(frame);
            }
        }

        private void AdaptLocked(long now)
        {
            PruneJitterLocked(now);
            PruneLossLocked(now);

            double sum = 0;
            foreach (JitterSample sample in jitterSamples)
            {
                sum += sample.DeltaUs;
            }
            jitterUs = jitterSamples.Count > 0 ? sum / jitterSamples.Count : 0;

            int targetUs = (int)Math.Round(40000 + 4.0 * jitterUs);
            // 下限 60ms：40ms 在实测中仍会偶发欠载（underrun），
            // 按「优先稳定」原则抬高下限，代价约 +20ms 延迟。
            targetUs = Math.Clamp(targetUs, (int)Math.Max(60000L, baseMs * 1000L / 2), maxMs * 1000);
            targetMs = (int)Math.Round(targetUs / 1000.0);

            // 播放中按“当前深度 D”调整；预缓冲/重缓冲期间只更新 lastTarget。
            if (playing && !rebuffering && output != null)
            {
                int depth = DepthLocked();
                int delta = Math.Clamp(targetMs - depth, -5, 10);
                if (delta > 0)
                {
                    output.AddSilenceMs(delta);
                    fadeInPending = true;
                }
                else if (delta < 0)
                {
                    TrimDepthLocked(depth + delta);
                }
                EnforceHardCapLocked();
            }
        }

        private void UpdateJitterLocked(long now, WifiTransport.WifiHeader header)
        {
            if (prevAudioValid && header.Seq == prevAudioSeq + 1)
            {
                uint arrivalDiffUs = unchecked(ToMicroseconds(now) - ToMicroseconds(prevArrivalTicks == 0 ? now : prevArrivalTicks));
                uint sampleDiffUs = unchecked(header.TimestampUs - prevAudioTimestampUs);
                if (sampleDiffUs > 0 && sampleDiffUs <= 100000)
                {
                    JitterSample sample = new JitterSample();
                    sample.Ticks = now;
                    sample.DeltaUs = Math.Abs((double)arrivalDiffUs - sampleDiffUs);
                    jitterSamples.Enqueue(sample);
                }
            }

            prevAudioValid = true;
            prevAudioSeq = header.Seq;
            prevAudioTimestampUs = header.TimestampUs;
            prevArrivalTicks = now;
            PruneJitterLocked(now);
        }

        private void PruneJitterLocked(long now)
        {
            long cutoff = now - TicksFromMs(JitterWindowMs);
            while (jitterSamples.Count > 0 && jitterSamples.Peek().Ticks < cutoff)
            {
                jitterSamples.Dequeue();
            }
        }

        private void AddLossSampleLocked(int receivedCount, int missingCount)
        {
            LossSample sample = new LossSample();
            sample.Ticks = clock.ElapsedTicks;
            sample.Received = receivedCount;
            sample.Missing = missingCount;
            lossWindow.Enqueue(sample);
            PruneLossLocked(clock.ElapsedTicks);
        }

        private void PruneLossLocked(long now)
        {
            long cutoff = now - TicksFromMs(LockWindowMs);
            while (lossWindow.Count > 0 && lossWindow.Peek().Ticks < cutoff)
            {
                lossWindow.Dequeue();
            }
        }

        private double LossRateLocked(long now)
        {
            PruneLossLocked(now);
            long recv = 0;
            long miss = 0;
            foreach (LossSample sample in lossWindow)
            {
                recv += sample.Received;
                miss += sample.Missing;
            }
            if (recv + miss == 0)
            {
                return double.NaN;
            }
            return (double)miss / (recv + miss) * 100.0;
        }

        private void EnforceHardCapLocked()
        {
            int depth = DepthLocked();
            if (depth <= maxMs)
            {
                return;
            }
            TrimDepthLocked(maxMs);
        }

        private void TrimDepthLocked(int keepMs)
        {
            if (output != null)
            {
                int provider = output.BufferedMs;
                if (provider > keepMs)
                {
                    output.DiscardOldest(keepMs);
                    dropped += provider - keepMs;
                }
            }

            int queueKeep = Math.Max(0, keepMs - (output == null ? 0 : output.BufferedMs));
            while (queue.Count > 0 && queue.Count * WifiTransport.FrameMs > queueKeep)
            {
                AudioFrame frame = queue[0];
                queue.RemoveAt(0);
                ReturnFrame(frame);
                dropped++;
            }
        }

        private int DepthLocked()
        {
            int provider = output == null ? 0 : output.BufferedMs;
            return provider + queue.Count * WifiTransport.FrameMs;
        }

        // ===== 状态与生命周期 =====

        private void BeginSessionLocked(long now, bool fromStart, uint startUs)
        {
            ClearQueueLocked();
            if (output != null)
            {
                output.Clear();
            }

            expectedValid = false;
            expectedSeq = 0;
            firstAudio = false;
            playing = false;
            draining = false;
            rebuffering = false;
            fadeInPending = false;
            hasSessionStart = fromStart;
            sessionStartUs = startUs;
            lastAudioTicks = now;
            lastTimestampUs = 0;
            prevAudioValid = false;
            prevArrivalTicks = 0;
            nextDueTicks = 0;
            lastAdaptTicks = now;
            lastStatsLogTicks = now;
            lastAggregateLogTicks = now;
            targetMs = baseMs;
            jitterUs = 0;
            jitterSamples.Clear();
            lossWindow.Clear();
            stopTicks = 0;
            received = 0;
            missing = 0;
            duplicates = 0;
            late = 0;
            resets = 0;
            underruns = 0;
            discontinuities = 0;
            dropped = 0;
            stale = 0;
            gapPending = 0;
            malformedPending = 0;
            underrunPending = 0;
            rebufferPending = 0;
            offsetValid = false;
            rttMs = double.NaN;
            lastPingTicks = 0;
            lastPongTicks = 0;
            state = SinkState.LinkReady;
            detail = "预缓冲中";
        }

        private void ResetForSeqJumpLocked(uint newSeq, long now)
        {
            ClearQueueLocked();
            if (output != null)
            {
                output.Clear();
            }

            expectedValid = true;
            expectedSeq = newSeq + 1;
            playing = false;
            draining = false;
            rebuffering = false;
            fadeInPending = true;
            prevAudioValid = false;
            prevArrivalTicks = 0;
            jitterSamples.Clear();
            jitterUs = 0;
            lastAdaptTicks = now;
            targetMs = Math.Max(baseMs, targetMs);
            state = SinkState.LinkReady;
            detail = "序号重置，重缓冲中";
        }

        private void TimeoutLocked(long now)
        {
            long silentMs = lastAudioTicks == 0 ? 0 : (long)Math.Round(ToMilliseconds(now - lastAudioTicks));
            GoArmedLocked("等待手机发送音频");
            log.Info("Wifi.Timeout", ("silentMs", silentMs));
        }

        private void DrainDoneLocked(long now)
        {
            long drainedMs = stopTicks == 0 ? 0 : (long)Math.Round(ToMilliseconds(now - stopTicks));
            GoArmedLocked("等待手机连接");
            log.Info("Wifi.DrainDone", ("drainedMs", drainedMs));
        }

        private void GoArmedLocked(string text)
        {
            ClearQueueLocked();
            DetachOutputLocked();
            playing = false;
            draining = false;
            rebuffering = false;
            fadeInPending = false;
            expectedValid = false;
            firstAudio = false;
            hasSessionStart = false;
            sessionEndpoint = null;
            stopTicks = 0;
            lastAudioTicks = 0;
            lastTimestampUs = 0;
            prevAudioValid = false;
            prevArrivalTicks = 0;
            targetMs = baseMs;
            jitterSamples.Clear();
            jitterUs = 0;
            offsetValid = false;
            rttMs = double.NaN;
            lastPingTicks = 0;
            lastPongTicks = 0;
            state = SinkState.Armed;
            detail = text;
        }

        private void DetachOutputLocked()
        {
            if (output != null)
            {
                if (pendingRelease != null)
                {
                    pendingRelease.Dispose();
                }
                pendingRelease = output;
                output = null;
            }
        }

        private void ClearQueueLocked()
        {
            for (int i = 0; i < queue.Count; i++)
            {
                ReturnFrame(queue[i]);
            }
            queue.Clear();
        }

        private static void ReturnFrame(AudioFrame frame)
        {
            if (frame != null && frame.Data != null)
            {
                ArrayPool<byte>.Shared.Return(frame.Data);
                frame.Data = null;
            }
        }

        private bool IsCurrentEndpointLocked(IPEndPoint from)
        {
            IPEndPoint endpoint = sessionEndpoint ?? remote;
            return endpoint == null || SameEndpoint(endpoint, from);
        }

        private static bool SameEndpoint(IPEndPoint a, IPEndPoint b)
        {
            return a != null && b != null && a.Port == b.Port && a.Address.Equals(b.Address);
        }

        // ===== 定时任务 =====

        private void Housekeeping(long now)
        {
            IPEndPoint pingTarget = null;
            uint pingUs = 0;

            lock (sync)
            {
                FlushAggregatesLocked(now);
                LogStatsLocked(now);

                if (state != SinkState.Faulted && remote != null && lastAnyTicks != 0 &&
                    now - lastAnyTicks <= TicksFromMs(LockWindowMs) &&
                    (lastPingTicks == 0 || now - lastPingTicks >= TicksFromMs(PingIntervalMs)))
                {
                    lastPingTicks = now;
                    lastPingSendUs = ToMicroseconds(now);
                    pingUs = lastPingSendUs;
                    pingTarget = sessionEndpoint ?? remote;
                }
            }

            if (pingTarget != null)
            {
                SendControlPacket(
                    WifiTransport.BuildPacket(WifiTransport.TypePing, 0, 0, pingUs, null),
                    pingTarget);
            }
        }

        private void FlushAggregatesLocked(long now)
        {
            if (now - lastAggregateLogTicks < TicksFromMs(AggregateIntervalMs))
            {
                return;
            }
            lastAggregateLogTicks = now;

            if (gapPending > 0)
            {
                log.Warn("Audio.Gap", ("missing", gapPending), ("seq", gapLastSeq));
                gapPending = 0;
            }

            if (malformedPending > 0)
            {
                log.Warn("Packet.Malformed", ("count", malformedPending), ("reason", malformedReason));
                malformedPending = 0;
            }

            if (underrunPending > 0)
            {
                log.Warn("Audio.Underrun", ("count", underrunPending));
                underrunPending = 0;
            }

            if (rebufferPending > 0)
            {
                log.Info("Audio.Rebuffer", ("count", rebufferPending), ("bufferMs", lastRebufferMs));
                rebufferPending = 0;
            }
        }

        private void LogStatsLocked(long now)
        {
            if (now - lastStatsLogTicks < TicksFromMs(StatsIntervalMs))
            {
                return;
            }

            if (state == SinkState.Armed && (lastAnyTicks == 0 || now - lastAnyTicks > TicksFromMs(LockWindowMs)))
            {
                return;
            }
            lastStatsLogTicks = now;

            double loss = LossRateLocked(now);
            long recv = 0;
            long miss = 0;
            foreach (LossSample sample in lossWindow)
            {
                recv += sample.Received;
                miss += sample.Missing;
            }

            log.Info(
                "Wifi.Stats",
                ("state", state.ToString()),
                ("rttMs", double.IsNaN(rttMs) ? (object)null : Math.Round(rttMs, 1)),
                ("offsetMs", offsetValid ? Math.Round(clockOffsetUs / 1000.0, 1) : (object)null),
                ("bufferMs", DepthLocked()),
                ("targetMs", targetMs),
                ("lossPct", double.IsNaN(loss) ? (object)null : Math.Round(loss, 2)),
                ("received", recv),
                ("missing", miss),
                ("dup", duplicates),
                ("late", late),
                ("resets", resets),
                ("underruns", underruns),
                ("discontinuities", discontinuities),
                ("malformed", malformed),
                ("foreign", foreign),
                ("stale", stale),
                ("unknown", unknown));
        }

        private void SendControlPacket(byte[] packet, IPEndPoint target)
        {
            Socket socket = controlSocket;
            if (socket == null || target == null || packet == null)
            {
                return;
            }

            try
            {
                socket.SendTo(packet, target);
            }
            catch (Exception ex)
            {
                if (DateTime.Now - lastSendErrorLog > TimeSpan.FromSeconds(30))
                {
                    lastSendErrorLog = DateTime.Now;
                    log.Warn("Wifi.Fault", ("code", "SendFailed"), ("detail", ex.Message));
                }
            }
        }

        public WifiSnapshot GetSnapshot()
        {
            lock (sync)
            {
                long now = clock.ElapsedTicks;
                PruneLossLocked(now);
                int bufferMs = DepthLocked();
                double loss = LossRateLocked(now);
                double latencyMs = double.NaN;

                if (playing && !rebuffering && offsetValid && lastTimestampUs != 0 &&
                    lastPongTicks != 0 && now - lastPongTicks <= TicksFromMs(LockWindowMs))
                {
                    uint nowUs = ToMicroseconds(now);
                    int ageUs = unchecked((int)(nowUs - lastTimestampUs)) + clockOffsetUs;
                    if (ageUs >= 0 && ageUs <= 2000000)
                    {
                        latencyMs = ageUs / 1000.0 + 50.0;
                    }
                }

                WifiSnapshot snapshot = new WifiSnapshot();
                snapshot.Enabled = true;
                snapshot.State = state;
                snapshot.Detail = detail;
                snapshot.Remote = remote == null ? null : remote.ToString();
                snapshot.DeviceName = remoteName;
                snapshot.RttMs = rttMs;
                snapshot.BufferMs = bufferMs;
                snapshot.TargetMs = targetMs;
                snapshot.LatencyMs = latencyMs;
                snapshot.LossPercent = loss;
                snapshot.Received = received;
                snapshot.Missing = missing;
                snapshot.Duplicates = duplicates;
                snapshot.Late = late;
                snapshot.Resets = resets;
                snapshot.Underruns = underruns;
                snapshot.Discontinuities = discontinuities;
                snapshot.Malformed = malformed;
                snapshot.Foreign = foreign;
                snapshot.Stale = stale;
                snapshot.Unknown = unknown;
                snapshot.Dropped = dropped;
                snapshot.Port = audioPort;
                return snapshot;
            }
        }

        private static void JoinThread(Thread thread, int milliseconds)
        {
            if (thread == null)
            {
                return;
            }

            try
            {
                thread.Join(milliseconds);
            }
            catch
            {
            }
        }

        private static void CloseSocket(Socket socket)
        {
            if (socket == null)
            {
                return;
            }

            try
            {
                socket.Close();
            }
            catch
            {
            }
        }

        private static uint ToMicroseconds(long ticks)
        {
            return unchecked((uint)(ticks * 1000000L / Stopwatch.Frequency));
        }

        private static double ToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static long TicksFromMs(int milliseconds)
        {
            return Stopwatch.Frequency * milliseconds / 1000L;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
