using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SinkLite.Platform;

namespace SinkLite.Core
{
    /// <summary>
    /// 播放输出：WasapiOut（共享模式，50 ms）+ PlayoutBuffer（48 kHz / 16-bit / 立体声）。
    ///
    /// 唯一缓冲由 WASAPI Read 消费，实际缺数在 Read 层计数并重缓冲。
    /// 所有方法都由接收器的播放线程串行调用，内部再加锁以兼容 UI 线程读取统计。
    /// </summary>
    internal sealed class AudioOutput : IDisposable
    {
        private const int PlaybackLatencyMs = 50;
        private const int BufferSeconds = 1;

        private readonly object sync = new object();
        private readonly Log log;
        private readonly PlayoutBuffer buffer;
        private readonly byte[] silence;
        private readonly byte[] fade;
        private WasapiOut output;
        private bool disposed;

        public AudioOutput(Log log)
        {
            this.log = log;
            buffer = new PlayoutBuffer { UseMmcss = true };
            silence = new byte[WifiTransport.PayloadBytes];
            fade = new byte[WifiTransport.PayloadBytes];
        }

        public int BufferedMs
        {
            get
            {
                lock (sync)
                {
                    return buffer.BufferedMs;
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
                    created.PlaybackStopped += (_, e) =>
                    {
                        buffer.ReleaseAudioThread();
                        if (e.Exception != null)
                            log.Error("Player.Fault", ("code", "PlaybackStopped"), ("detail", e.Exception.Message));
                    };
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

            buffer.Add(data, offset, count);
        }

        public long PaddingFrames { get { return buffer.PaddingFrames; } }
        public long ActualUnderruns { get { return buffer.Underruns; } }
        public long Overflows { get { return buffer.Overflows; } }
        public bool MmcssActive { get { return buffer.MmcssActive; } }
        public double MaxReadGapMs { get { return buffer.MaxReadGapMs; } }
        public long InputFrames { get { return buffer.InputFrames; } }
        public long MaxInputZeroRunFrames { get { return buffer.MaxInputZeroRunFrames; } }
        public bool Recovering { get { return buffer.Recovering; } }
        public int TargetMs { set { buffer.TargetMs = value; } }
        public void EndInput() { buffer.EndInput(); }

        /// <summary>补一帧 5 ms 静音，用于单包缺失时避免爆音。</summary>
        public void AddSilence()
        {
            buffer.Add(silence, 0, silence.Length);
        }

        /// <summary>
        /// 断流恢复时对首个数据帧做 5 ms 线性淡入（0 → 1），避免从静音切回音乐时爆音。
        /// 一帧正好 5 ms；协议要求 PC 不置 flags.bit0，只记录断流并做淡入。
        /// </summary>
        public void AddFadeIn(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0) return;
            lock (sync)
            {
                int bytes = Math.Min(count, fade.Length);
                int frames = bytes / WifiTransport.BytesPerFrame;
                for (int frame = 0; frame < frames; frame++)
                {
                    for (int channel = 0; channel < 2; channel++)
                    {
                        int i = frame * 4 + channel * 2;
                        short sample = (short)(data[offset + i] | data[offset + i + 1] << 8);
                        short scaled = (short)(sample * (frame + 1) / frames);
                        fade[i] = (byte)scaled;
                        fade[i + 1] = (byte)(scaled >> 8);
                    }
                }
                buffer.Add(fade, 0, bytes);
                if (count > bytes) buffer.Add(data, offset + bytes, count - bytes);
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                try
                {
                    buffer.Clear();
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
                log.Info("Player.ReadStats", ("paddingFrames", buffer.PaddingFrames),
                    ("underruns", buffer.Underruns), ("overflows", buffer.Overflows));
                try
                {
                    buffer.Clear();
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
