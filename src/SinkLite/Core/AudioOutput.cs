using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SinkLite.Platform;

namespace SinkLite.Core
{
    /// <summary>
    /// 播放输出：WasapiOut（共享模式，50 ms）+ BufferedWaveProvider（48 kHz / 16-bit / 立体声）。
    ///
    /// 缓冲容量固定 1 秒，ReadFully=true 保证 WASAPI 拉不到数据时自动补静音。
    /// 所有方法都由接收器的播放线程串行调用，内部再加锁以兼容 UI 线程读取统计。
    /// </summary>
    internal sealed class AudioOutput : IDisposable
    {
        private const int PlaybackLatencyMs = 50;
        private const int BufferSeconds = 1;

        private readonly object sync = new object();
        private readonly Log log;
        private readonly BufferedWaveProvider buffer;
        private readonly byte[] silence;
        private readonly byte[] silencePad;
        private readonly byte[] fade;
        private readonly byte[] discard;
        private WasapiOut output;
        private bool disposed;

        public AudioOutput(Log log)
        {
            this.log = log;
            WaveFormat format = new WaveFormat(WifiTransport.SampleRate, WifiTransport.BitsPerSample, WifiTransport.Channels);
            buffer = new BufferedWaveProvider(format);
            buffer.BufferDuration = TimeSpan.FromSeconds(BufferSeconds);
            buffer.ReadFully = true;
            buffer.DiscardOnBufferOverflow = true;

            silence = new byte[WifiTransport.PayloadBytes];
            silencePad = new byte[WifiTransport.PayloadBytes * 4];
            fade = new byte[WifiTransport.PayloadBytes];
            discard = new byte[WifiTransport.PayloadBytes * 8];
        }

        public int BufferedMs
        {
            get
            {
                lock (sync)
                {
                    return (int)Math.Round(buffer.BufferedDuration.TotalMilliseconds);
                }
            }
        }

        public bool IsInitialized
        {
            get
            {
                lock (sync)
                {
                    return output != null;
                }
            }
        }

        /// <summary>
        /// 打开默认输出端点并完成 WasapiOut.Init。失败返回 false，由调用方记录并退避重试。
        /// 刻意不在 Init 之后立刻 Play：调用方要先预填缓冲，再开始播放。
        /// </summary>
        public bool EnsureInitialized()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return false;
                }

                if (output != null)
                {
                    return true;
                }

                WasapiOut created = null;
                try
                {
                    created = new WasapiOut(AudioClientShareMode.Shared, PlaybackLatencyMs);
                    created.Init(buffer);
                    output = created;
                    log.Info(
                        "Player.Created",
                        ("format", "48000/16/2"),
                        ("latencyMs", PlaybackLatencyMs),
                        ("bufferMs", BufferSeconds * 1000));
                    return true;
                }
                catch (Exception ex)
                {
                    log.Error("Player.Fault", ("code", "CreateFailed"), ("detail", ex.Message));
                    try
                    {
                        if (created != null)
                        {
                            created.Dispose();
                        }
                    }
                    catch
                    {
                    }
                    output = null;
                    return false;
                }
            }
        }

        /// <summary>开始消费缓冲。调用前应已预填至少一个目标缓冲。</summary>
        public void Play()
        {
            lock (sync)
            {
                if (output == null)
                {
                    return;
                }

                try
                {
                    if (output.PlaybackState != PlaybackState.Playing)
                    {
                        output.Play();
                    }
                }
                catch (Exception ex)
                {
                    // 播放失败不抛出到接收线程，交给上层按故障处理。
                    log.Error("Player.Fault", ("code", "PlayFailed"), ("detail", ex.Message));
                }
            }
        }

        public void Add(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0)
            {
                return;
            }

            lock (sync)
            {
                try
                {
                    buffer.AddSamples(data, offset, count);
                }
                catch
                {
                    // DiscardOnBufferOverflow=true 时正常不会抛；保留防御。
                }
            }
        }

        /// <summary>补一帧 5 ms 静音，用于单包缺失时避免爆音。</summary>
        public void AddSilence()
        {
            lock (sync)
            {
                try
                {
                    buffer.AddSamples(silence, 0, silence.Length);
                }
                catch
                {
                }
            }
        }

        /// <summary>向队尾追加指定毫秒的静音（§6.3 的 Δ&gt;0 调整，1~10 ms）。</summary>
        public void AddSilenceMs(int milliseconds)
        {
            if (milliseconds <= 0)
            {
                return;
            }

            int bytes = Math.Min(silencePad.Length, milliseconds * (WifiTransport.SampleRate * WifiTransport.BytesPerFrame) / 1000);
            bytes -= bytes % WifiTransport.BytesPerFrame;
            if (bytes <= 0)
            {
                return;
            }

            lock (sync)
            {
                try
                {
                    buffer.AddSamples(silencePad, 0, bytes);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// 断流恢复时对首个数据帧做 5 ms 线性淡入（0 → 1），避免从静音切回音乐时爆音。
        /// 一帧正好 5 ms；协议要求 PC 不置 flags.bit0，只记录断流并做淡入。
        /// </summary>
        public void AddFadeIn(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0)
            {
                return;
            }

            lock (sync)
            {
                try
                {
                    int frames = Math.Min(count, fade.Length);
                    int samples = frames / 2;
                    if (samples <= 0)
                    {
                        buffer.AddSamples(data, offset, count);
                        return;
                    }

                    for (int i = 0; i < samples; i++)
                    {
                        int index = i * 2;
                        short value = (short)(data[offset + index] | (data[offset + index + 1] << 8));
                        double gain = (double)(i + 1) / samples;
                        short scaled = (short)(value * gain);
                        fade[index] = (byte)(scaled & 0xFF);
                        fade[index + 1] = (byte)((scaled >> 8) & 0xFF);
                    }

                    buffer.AddSamples(fade, 0, frames);
                    if (count > frames)
                    {
                        buffer.AddSamples(data, offset + frames, count - frames);
                    }
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// 丢弃最旧的数据，把缓冲压到 keepMs 左右（按 4 字节帧对齐）。
        /// BufferedWaveProvider 没有"丢最旧"的 API，这里用 Read 消费掉队首数据实现。
        /// </summary>
        public void DiscardOldest(int keepMs)
        {
            if (keepMs < 0)
            {
                keepMs = 0;
            }

            lock (sync)
            {
                try
                {
                    int keepBytes = keepMs * (WifiTransport.SampleRate * WifiTransport.BytesPerFrame) / 1000;
                    int excess = buffer.BufferedBytes - keepBytes;
                    if (excess <= 0)
                    {
                        return;
                    }

                    excess -= excess % WifiTransport.BytesPerFrame;
                    while (excess > 0)
                    {
                        int available = buffer.BufferedBytes;
                        if (available <= 0)
                        {
                            break;
                        }

                        int chunk = Math.Min(excess, Math.Min(discard.Length, available));
                        int read = buffer.Read(discard, 0, chunk);
                        if (read <= 0)
                        {
                            break;
                        }
                        excess -= read;
                    }
                }
                catch
                {
                }
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                try
                {
                    buffer.ClearBuffer();
                }
                catch
                {
                }
            }
        }

        /// <summary>停止并释放输出端点；缓冲一并清空。</summary>
        public void Stop()
        {
            WasapiOut current = null;
            lock (sync)
            {
                current = output;
                output = null;
                try
                {
                    buffer.ClearBuffer();
                }
                catch
                {
                }
            }

            if (current == null)
            {
                return;
            }

            log.Info("Player.Released");
            try
            {
                current.Stop();
            }
            catch
            {
            }

            try
            {
                current.Dispose();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
            }

            Stop();
        }
    }
}
