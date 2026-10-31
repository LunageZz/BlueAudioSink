using System;
using NAudio.Wave;

namespace SinkLite.Core
{
    /// <summary>
    /// PCM fan-out after each stream's packet reorder stage. Two independent consumers:
    /// headphones receive media only; the virtual capture device receives mic + optional media.
    /// No hardware devices are opened here. All PCM is 48 kHz / signed 16-bit / stereo.
    /// </summary>
    internal sealed class PhoneAudioRouter
    {
        private readonly object sync = new object();
        private readonly PlayoutBuffer headphones = new PlayoutBuffer();
        private readonly PlayoutBuffer microphone = new PlayoutBuffer();
        private readonly PlayoutBuffer sharedMedia = new PlayoutBuffer();
        private bool shareMedia;
        private bool microphoneMuted;
        public IWaveProvider Headphones => headphones;
        public IWaveProvider VirtualMicrophone { get; }
        public PhoneAudioRouter() { VirtualMicrophone = new MixOutput(this); }

        public bool ShareMedia
        {
            get { lock (sync) return shareMedia; }
            set
            {
                lock (sync)
                {
                    if (shareMedia == value) return;
                    // Never leak pre-toggle music, or replay stale music on re-enable.
                    sharedMedia.Clear();
                    shareMedia = value;
                }
            }
        }
        public bool MicrophoneMuted
        {
            get { lock (sync) return microphoneMuted; }
            set { lock (sync) { microphoneMuted = value; microphone.Clear(); } }
        }
        public void AddMedia(byte[] pcm, int offset, int count)
        {
            Validate(pcm, offset, count);
            lock (sync)
            {
                headphones.Add(pcm, offset, count);
                if (shareMedia) sharedMedia.Add(pcm, offset, count);
            }
        }
        public void AddMicrophone(byte[] pcm, int offset, int count)
        {
            Validate(pcm, offset, count);
            lock (sync) if (!microphoneMuted) microphone.Add(pcm, offset, count);
        }
        public void Clear()
        {
            lock (sync) { headphones.Clear(); microphone.Clear(); sharedMedia.Clear(); }
        }
        private static void Validate(byte[] pcm, int offset, int count)
        {
            if (pcm == null || offset < 0 || count < 0 || count % 4 != 0 || offset > pcm.Length - count)
                throw new ArgumentException("Expected complete stereo PCM frames");
        }
        private sealed class MixOutput : IWaveProvider
        {
            private readonly PhoneAudioRouter owner;
            private byte[] music = Array.Empty<byte>();
            public MixOutput(PhoneAudioRouter owner) { this.owner = owner; }
            public WaveFormat WaveFormat => owner.microphone.WaveFormat;
            public int Read(byte[] data, int offset, int count)
            {
                Validate(data, offset, count);
                lock (owner.sync)
                {
                    owner.microphone.Read(data, offset, count);
                    if (!owner.shareMedia) return count;
                    if (music.Length < count) music = new byte[count];
                    owner.sharedMedia.Read(music, 0, count);
                    for (int i = 0; i < count; i += 2)
                    {
                        int mic = (short)(data[offset + i] | data[offset + i + 1] << 8);
                        int media = (short)(music[i] | music[i + 1] << 8);
                        // Saturate instead of wrapping from a positive peak to a negative peak.
                        short mixed = (short)Math.Clamp(mic + media, short.MinValue, short.MaxValue);
                        data[offset + i] = (byte)mixed;
                        data[offset + i + 1] = (byte)(mixed >> 8);
                    }
                    return count;
                }
            }
        }
    }
}
