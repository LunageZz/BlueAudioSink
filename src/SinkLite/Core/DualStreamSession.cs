using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SinkLite.Platform;

namespace SinkLite.Core
{
    internal sealed class OrderedAudioStream
    {
        private readonly Dictionary<uint, byte[]> pending = new Dictionary<uint, byte[]>();
        private uint expected;
        private bool initialized;
        private long gapAt;
        public void Add(uint seq, byte[] pcm)
        {
            if (!initialized) { initialized=true; expected=seq; }
            int distance=unchecked((int)(seq-expected));
            if (distance<0 || pending.ContainsKey(seq)) return;
            if (distance>200) { pending.Clear(); expected=seq; gapAt=0; }
            pending[seq]=pcm;
        }
        public void Drain(long now, Action<byte[]> emit)
        {
            while(pending.Count>0) {
                if(pending.TryGetValue(expected,out var pcm)) {
                    pending.Remove(expected++); gapAt=0; emit(pcm); continue;
                }
                if(gapAt==0) gapAt=now;
                if(now-gapAt<Stopwatch.Frequency/50) return;
                uint nearest=pending.Keys.OrderBy(x=>unchecked((uint)(x-expected))).First();
                uint distance=unchecked(nearest-expected);
                if(distance>10) { expected=nearest; gapAt=0; continue; }
                emit(new byte[WifiTransport.PayloadBytes]); expected++;
            }
        }
        public void Clear() { pending.Clear(); initialized=false; gapAt=0; }
    }

    // Network thread only enqueues. Device creation and disposal occur on the playout thread.
    internal sealed class DualStreamSession : IDisposable
    {
        private readonly object sync=new object();
        private readonly OrderedAudioStream[] streams={new OrderedAudioStream(),new OrderedAudioStream()};
        private readonly PhoneAudioRouter router=new PhoneAudioRouter();
        private readonly Log log;
        private readonly MicrophoneState controls;
        private readonly bool mediaEnabled, micEnabled, routeMediaToHeadphones;
        private WasapiOut headphones, virtualMic;
        private MMDevice headphoneDevice, virtualDevice;
        private string headphoneId, virtualId;
        private Settings currentSettings;
        private long nextSettingsRead;
        private long nextOpen, lastMedia, lastMic;
        private bool closed;
        public readonly uint Id;
        private volatile bool virtualReady;
        public bool HasVirtualOutput { get => virtualReady; private set => virtualReady=value; }
        private volatile bool headphonesReady;
        public bool HasHeadphones { get => headphonesReady; private set => headphonesReady=value; }
        private volatile string status="正在准备音频设备";
        public string OutputStatus { get => status; private set => status=value; }
        public long Received { get; private set; }
        public DualStreamSession(uint id, bool media, bool mic, MicrophoneState controls, Log log, bool routeMediaToHeadphones = true)
        {
            this.routeMediaToHeadphones=routeMediaToHeadphones;
            if(!routeMediaToHeadphones) router.SetTargetLatency(160);
            Id=id; mediaEnabled=media; micEnabled=mic; this.controls=controls; this.log=log;
            router.EnableMmcss(OperatingSystem.IsWindows());
            router.EnableOutputs(false,false);
        }
        public bool Add(DualStreamTransport.Header h, byte[] data, long now)
        {
            lock(sync) {
                if(closed || h.Session!=Id || h.Stream>1 || data==null || data.Length<DualStreamTransport.PacketBytes || (h.Stream==0 ? !mediaEnabled : !micEnabled)) return false;
                var pcm=new byte[WifiTransport.PayloadBytes];
                Buffer.BlockCopy(data,DualStreamTransport.HeaderSize,pcm,0,pcm.Length);
                streams[h.Stream].Add(h.Sequence,pcm); Received++;
                if(h.Stream==0) lastMedia=now; else lastMic=now;
                return true;
            }
        }
        public void CloseInput() { lock(sync) { closed=true; foreach(var stream in streams) stream.Clear(); router.Clear(); } }
        public void Tick(long now)
        {
            lock(sync) { if(closed) return; }
            if(currentSettings==null || now>=nextSettingsRead) { currentSettings=Settings.Load(); nextSettingsRead=now+Stopwatch.Frequency; }
            var settings=currentSettings;
            if(settings.PreferredRenderId!=headphoneId) {
                ReleaseHeadphones(); router.ClearMedia(); headphoneId=settings.PreferredRenderId; nextOpen=0;
                router.EnableOutputs(false,HasVirtualOutput);
            }
            if(settings.VirtualRenderId!=virtualId) {
                ReleaseVirtual(); router.ClearVirtual(); virtualId=settings.VirtualRenderId; nextOpen=0;
                router.EnableOutputs(HasHeadphones,false);
            }
            if(now>=nextOpen && ((mediaEnabled && routeMediaToHeadphones && headphones==null)||virtualMic==null)) {
                nextOpen=now+Stopwatch.Frequency*3;
                OpenDevices(settings);
            }
            var state=controls.Current;
            router.ShareMedia=state.Share; router.MicrophoneVolume=state.Volume;
            // Muting clears only on an actual transition, not once per audio tick.
            if(router.MicrophoneMuted!=state.Muted) router.MicrophoneMuted=state.Muted;
            try {
                lock(sync) {
                    if(closed) return;
                    if(lastMedia!=0 && now-lastMedia>Stopwatch.Frequency) { streams[0].Clear(); lastMedia=0; router.ClearMedia(); }
                    if(lastMic!=0 && now-lastMic>Stopwatch.Frequency) { streams[1].Clear(); lastMic=0; router.ClearMicrophone(); }
                    streams[0].Drain(now,pcm=>router.AddMedia(pcm,0,pcm.Length));
                    streams[1].Drain(now,pcm=>router.AddMicrophone(pcm,0,pcm.Length));
                }
            } catch(Exception ex) {
                log.Error("Dual.BufferFault",("detail",ex.Message));
                ReleaseDevices(); router.Clear(); nextOpen=now+Stopwatch.Frequency;
            }
            if(headphones!=null && headphones.PlaybackState!=PlaybackState.Playing) { ReleaseHeadphones(); router.EnableOutputs(false,HasVirtualOutput); nextOpen=now+Stopwatch.Frequency; }
            if(virtualMic!=null && virtualMic.PlaybackState!=PlaybackState.Playing) { ReleaseVirtual(); router.EnableOutputs(HasHeadphones,false); nextOpen=now+Stopwatch.Frequency; }
        }
        private void OpenDevices(Settings settings)
        {
            var endpoints=EndpointRegistry.EnumerateAll();
            if(mediaEnabled && routeMediaToHeadphones && headphones==null) try {
                using(var devices=new MMDeviceEnumerator()) {
                    var chosen=endpoints.FirstOrDefault(e=>e.IsActive&&e.IsRender&&!e.IsVirtual&&e.Id==settings.PreferredRenderId);
                    if(chosen==null) {
                        try { using(var d=devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia)) chosen=endpoints.FirstOrDefault(e=>e.Id==d.ID&&!e.IsVirtual); } catch { }
                    }
                    chosen=chosen??endpoints.FirstOrDefault(e=>e.IsActive&&e.IsRender&&!e.IsVirtual);
                    if(chosen!=null) { headphoneDevice=devices.GetDevice(chosen.Id); headphones=new WasapiOut(headphoneDevice,AudioClientShareMode.Shared,false,50); headphones.Init(router.Headphones); headphones.PlaybackStopped += (_,__)=>router.ReleaseHeadphoneThread(); headphones.Play(); }
                }
            } catch(Exception ex) { log.Error("Dual.HeadphoneFault",("detail",ex.Message)); ReleaseHeadphones(); }
            if(virtualMic==null) try {
                using(var devices=new MMDeviceEnumerator()) {
                    var chosen=endpoints.FirstOrDefault(e=>EndpointRegistry.IsCableRender(e)&&e.Id==settings.VirtualRenderId)
                        ?? endpoints.FirstOrDefault(e=>EndpointRegistry.IsCableRender(e));
                    if(chosen!=null) { virtualDevice=devices.GetDevice(chosen.Id); virtualMic=new WasapiOut(virtualDevice,AudioClientShareMode.Shared,false,50); virtualMic.Init(router.VirtualMicrophone); virtualMic.PlaybackStopped += (_,__)=>router.ReleaseMicrophoneThread(); virtualMic.Play(); }
                }
            } catch(Exception ex) { log.Error("Dual.MicrophoneOutputFault",("detail",ex.Message)); ReleaseVirtual(); }
            HasHeadphones=headphones!=null; HasVirtualOutput=virtualMic!=null;
            router.EnableOutputs(HasHeadphones,HasVirtualOutput);
            OutputStatus=HasVirtualOutput ? (mediaEnabled&&routeMediaToHeadphones&&!HasHeadphones?"虚拟麦克风已就绪；耳机设备不可用":"虚拟麦克风已就绪") : "未找到虚拟麦克风，请安装 VB-CABLE 或选择设备";
        }
        private void ReleaseHeadphones()
        {
            try { headphones?.Stop(); } catch { } try { headphones?.Dispose(); } catch { }
            try { headphoneDevice?.Dispose(); } catch { }
            headphones=null; headphoneDevice=null; HasHeadphones=false;
        }
        private void ReleaseVirtual()
        {
            try { virtualMic?.Stop(); } catch { } try { virtualMic?.Dispose(); } catch { }
            try { virtualDevice?.Dispose(); } catch { }
            virtualMic=null; virtualDevice=null; HasVirtualOutput=false;
        }
        private void ReleaseDevices() { router.EnableOutputs(false,false); ReleaseHeadphones(); ReleaseVirtual(); }
        public void Dispose() { CloseInput(); ReleaseDevices(); }
    }
}
