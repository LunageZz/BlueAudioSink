using System.Reflection;
using System.Net;
using System.Text.Json;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using SinkLite.Core;
using SinkLite.Platform;
using SinkLite.UI;

class Program
{
    static readonly BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Set(object o, string name, object value) => o.GetType().GetField(name, F).SetValue(o, value);
    static object Get(object o, string name) => o.GetType().GetField(name, F).GetValue(o);
    static object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, F).Invoke(o, args);
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    static byte[] Signal(int ms, int startFrame = 0)
    {
        byte[] b = new byte[ms * 192];
        for (int frame = 0; frame < b.Length / 4; frame++)
        {
            short v = (short)(11000 * Math.Sin(2 * Math.PI * 997 * (startFrame + frame) / 48000.0));
            for (int ch = 0; ch < 2; ch++) { int i = frame * 4 + ch * 2; b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); }
        }
        return b;
    }
    static long Ticks(int ms) => System.Diagnostics.Stopwatch.Frequency * ms / 1000;
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--decode")
        {
            using var reader = new MediaFoundationReader(args[1]);
            using var resampler = new MediaFoundationResampler(reader, new WaveFormat(48000, 16, 2));
            resampler.ResamplerQuality = 60;
            WaveFileWriter.CreateWaveFile(args[2], resampler);
            return;
        }
        if (args.Length > 0 && args[0] == "--record") { Record(int.Parse(args[1]), args[2]); return; }
        using var log = new Log(Path.Combine(AppContext.BaseDirectory, "logs"));
        int passed = 0;
        void Test(string name, Action test) { test(); Console.WriteLine("PASS " + name); passed++; }
        Test("Healthy PCM remains bit exact across changing targets", () => {
            var b = new PlayoutBuffer(); var source = Signal(300); b.Add(source, 0, source.Length);
            byte[] actual = new byte[source.Length];
            for (int i = 0; i < 6; i++) { b.TargetMs = 80 + i * 10; b.Read(actual, i * 50 * 192, 50 * 192); }
            Assert(source.SequenceEqual(actual), "Changed healthy audio");
            Assert(b.PaddingFrames == 0 && b.Underruns == 0, "Unexpected padding");
        });
        Test("Actual shortage counted, recovery holds all PCM until threshold", () => {
            var b = new PlayoutBuffer(); var x = Signal(20); b.Add(x, 0, x.Length);
            byte[] read = new byte[50 * 192]; b.Read(read, 0, read.Length);
            Assert(b.PaddingFrames == 30 * 48 && b.Underruns == 1 && b.Recovering, "Shortage counter");
            x = Signal(40); b.Add(x, 0, x.Length); b.Read(read, 0, read.Length);
            Assert(b.BufferedMs == 40 && b.PaddingFrames == 80 * 48 && b.Underruns == 1, "Recovery consumed early");
            b.Add(x, 0, x.Length); b.Read(read, 0, read.Length);
            Assert(!b.Recovering && b.BufferedMs == 30, "Recovery failed to prefill");
        });
        Test("Normal drain does not count starvation", () => {
            var b = new PlayoutBuffer(); var x = Signal(20); b.Add(x, 0, x.Length); b.EndInput();
            Assert(b.Read(new byte[50 * 192], 0, 50 * 192) == x.Length, "Drain length");
            Assert(b.Underruns == 0 && b.PaddingFrames == 0, "Drain is not a fault");
        });
        Test("Target adaptation never inserts or deletes PCM; 80ms stays minimum", () => {
            using var r = new WifiReceiver(log, 45678, 45679, 80, 200, "test");
            var o = new AudioOutput(log); var x = Signal(80); o.Add(x, 0, x.Length);
            Set(r, "output", o); Set(r, "playing", true); Set(r, "state", SinkState.Flowing);
            Call(r, "AdaptLocked", 0L);
            // This is a PCM-only test, not a Windows render-thread scheduling test.
            var b = (PlayoutBuffer)Get(o, "buffer"); b.UseMmcss = false;
            byte[] y = new byte[x.Length]; b.Read(y, 0, y.Length);
            Assert(x.SequenceEqual(y) && r.GetSnapshot().TargetMs == 80, "Adaptation changed audio");
        });
        Test("Empty staging queue does not starve a healthy output buffer", () => {
            using var r = new WifiReceiver(log, 45678, 45679, 80, 200, "test");
            var o = new AudioOutput(log); var x = Signal(80); o.Add(x, 0, x.Length);
            Set(r, "output", o); Set(r, "playing", true); Set(r, "state", SinkState.Flowing);
            Call(r, "ProcessLocked", Ticks(1));
            Assert(r.GetSnapshot().State == SinkState.Flowing && r.GetSnapshot().Underruns == 0
                && o.BufferedMs == 80, "False starvation or consumed audio");
        });
        Test("New START detaches the old consumer before prebuffering", () => {
            using var r = new WifiReceiver(log, 45678, 45679, 80, 200, "test");
            var o = new AudioOutput(log); var x = Signal(80); o.Add(x, 0, x.Length);
            Set(r, "output", o); Set(r, "playing", true); Set(r, "state", SinkState.Flowing);
            Call(r, "BeginSessionLocked", Ticks(1), true, 0U);
            Assert(Get(r, "output") == null && ReferenceEquals(Get(r, "pendingRelease"), o)
                && !(bool)Get(r, "hasSessionStart"), "Old consumer or zero timestamp baseline retained");
            o.Dispose();
        });
        void Packet(WifiReceiver r, uint seq, long now)
        {
            byte[] data = WifiTransport.BuildPacket(WifiTransport.TypeAudio, 0, seq, seq * 5000, Signal(5));
            Call(r, "HandlePacket", data, data.Length, new IPEndPoint(IPAddress.Loopback, 45678), now, false);
        }
        Test("Long sessions cross signed timestamp boundary and full wrap without stale loss", () => {
            using var r = new WifiReceiver(log, 0, 0, 80, 200, "test");
            var from = new IPEndPoint(IPAddress.Loopback, 45678);
            const uint start = 123456;
            var begin = WifiTransport.BuildPacket(WifiTransport.TypeStart, 0, 0, start, null);
            Call(r, "HandlePacket", begin, begin.Length, from, Ticks(1), false);
            uint seq = 0;
            // Advance one simulated second at a time; no wall-clock wait or real output.
            // Clear only staged PCM so the test remains bounded over 75 simulated minutes.
            for (long second = 0; second <= 4500; second++) {
                uint ts = unchecked(start + (uint)(second * 1000000));
                var audio = WifiTransport.BuildPacket(WifiTransport.TypeAudio, 0, seq++, ts, Signal(5));
                Call(r, "HandlePacket", audio, audio.Length, from, Ticks(1) + second * Ticks(1000), false);
                Call(r, "FlushReorderLocked", Ticks(1) + second * Ticks(1000));
                Call(r, "ClearQueueLocked");
            }
            Assert(r.GetSnapshot().Received == 4501 && r.GetSnapshot().Stale == 0, "Long stream rejected after timestamp boundary");
        });
        Test("Stale startup audio is rejected even when START crosses uint wrap", () => {
            using var r = new WifiReceiver(log, 0, 0, 80, 200, "test");
            var from = new IPEndPoint(IPAddress.Loopback, 45678);
            const uint start = uint.MaxValue - 2000;
            var begin = WifiTransport.BuildPacket(WifiTransport.TypeStart, 0, 0, start, null);
            Call(r, "HandlePacket", begin, begin.Length, from, Ticks(1), false);
            var old = WifiTransport.BuildPacket(WifiTransport.TypeAudio, 0, 0, start - 200000, Signal(5));
            Call(r, "HandlePacket", old, old.Length, from, Ticks(2), false);
            var fresh = WifiTransport.BuildPacket(WifiTransport.TypeAudio, 0, 0, unchecked(start + 5000), Signal(5));
            Call(r, "HandlePacket", fresh, fresh.Length, from, Ticks(3), false);
            Assert(r.GetSnapshot().Stale == 1 && r.GetSnapshot().Received == 1, "Startup stale guard or wrap handling lost");
        });
        Test("Player startup exceptions fault the receiver and detach the failed output", () => {
            using var r = new WifiReceiver(log, 0, 0, 80, 200, "test");
            var o = new AudioOutput(log);
            var player = new FailingPlayer();
            Set(o, "output", player);
            o.Add(Signal(80), 0, 80 * 192);
            Set(r, "output", o); Set(r, "state", SinkState.LinkReady);
            Call(r, "ProcessLocked", Ticks(1));
            Assert(player.PlayCalls == 1 && !(bool)Get(r, "playing"), "Failed player marked playing");
            Assert(r.GetSnapshot().State == SinkState.Faulted && Get(r, "output") == null
                && ReferenceEquals(Get(r, "pendingRelease"), o), "Failed output retained or fault hidden");
            Call(r, "ProcessLocked", Ticks(2));
            Assert(player.PlayCalls == 1, "Faulted player unexpectedly retried");
            o.Dispose();
        });
        Test("0,2,1 is reordered without concealment", () => {
            using var r = new WifiReceiver(log, 45678, 45679, 80, 200, "test");
            Packet(r, 0, Ticks(1)); Call(r, "FlushReorderLocked", Ticks(1));
            Packet(r, 2, Ticks(2)); Call(r, "FlushReorderLocked", Ticks(10));
            Packet(r, 1, Ticks(11)); Call(r, "FlushReorderLocked", Ticks(11));
            var q = (System.Collections.IList)Get(r, "queue");
            Assert(q.Count == 3 && r.GetSnapshot().Missing == 0 && r.GetSnapshot().Late == 0, "Reorder loss");
            for (int i = 0; i < q.Count; i++) Assert((uint)q[i].GetType().GetField("Seq").GetValue(q[i]) == i, "Order");
        });
        Test("True gap concealed only after deadline; old packets never reset", () => {
            using var r = new WifiReceiver(log, 45678, 45679, 80, 200, "test");
            Packet(r, 0, Ticks(1)); Packet(r, 2, Ticks(2));
            Call(r, "FlushReorderLocked", Ticks(21)); Assert(r.GetSnapshot().Missing == 0, "Early loss");
            Call(r, "FlushReorderLocked", Ticks(22)); Assert(r.GetSnapshot().Missing == 1, "Gap not concealed");
            Packet(r, unchecked((uint)-100), Ticks(30)); Assert(r.GetSnapshot().Resets == 0, "Old packet reset");
        });
        Test("Sequence wrap remains continuous", () => {
            using var r = new WifiReceiver(log, 45678, 45679, 80, 200, "test");
            Packet(r, uint.MaxValue, Ticks(1)); Packet(r, 0, Ticks(2)); Call(r, "FlushReorderLocked", Ticks(2));
            Assert(r.GetSnapshot().Missing == 0 && ((System.Collections.IList)Get(r, "queue")).Count == 2, "Wrap");
        });
        Test("90 seconds independent arrival and 50ms device reads stay bit exact", () => {
            var b = new PlayoutBuffer(); using var expected = new MemoryStream(); using var actual = new MemoryStream();
            var prefill = Signal(100); b.Add(prefill, 0, prefill.Length); expected.Write(prefill);
            for (int ms = 0; ms < 90000; ms += 5)
            {
                var x = Signal(5, (100 + ms) * 48); b.Add(x, 0, x.Length); expected.Write(x);
                if (ms % 50 == 45) { var y = new byte[50 * 192]; b.Read(y, 0, y.Length); actual.Write(y); }
            }
            Assert(actual.ToArray().SequenceEqual(expected.ToArray().Take((int)actual.Length)), "PCM mismatch");
            Assert(b.Underruns == 0 && b.PaddingFrames == 0 && b.Overflows == 0, "Counter mismatch");
        });
        Test("Silent PCM is connected and quiet, not audible playback", () => {
            var snapshot = new WifiSnapshot { Enabled = true, State = SinkState.Flowing, Remote = "192.168.0.2:45678",
                LastAudioAgeMs = 10, OutputActive = true, HasAudibleSignal = false };
            var view = ConnectionPresentation.Wifi(snapshot);
            Assert(view.Title.Contains("静音") && view.PhoneReady && view.StreamReady && view.OutputReady, "Silent stream label");
            snapshot.HasAudibleSignal = true;
            Assert(ConnectionPresentation.Wifi(snapshot).Title.Contains("正在播放"), "Active audio label");
        });
        Test("A remembered phone endpoint alone is not a live connection", () => {
            var snapshot = new WifiSnapshot { Enabled = true, State = SinkState.Armed, Remote = "192.168.0.2:45678", ControlAgeMs = 7000 };
            var view = ConnectionPresentation.Wifi(snapshot);
            Assert(!view.PhoneReady && view.Title == "等待手机连接", "Old endpoint counted live");
            snapshot.ControlAgeMs = 20;
            Assert(ConnectionPresentation.Wifi(snapshot).Title == "手机已连接", "Control link not recognized");
        });
        Test("Stale audio cannot keep showing active playback", () => {
            var view = ConnectionPresentation.Wifi(new WifiSnapshot { Enabled = true, State = SinkState.Flowing,
                Remote = "192.168.0.2:45678", LastAudioAgeMs = 1500, HasAudibleSignal = true, OutputActive = true });
            Assert(view.Title == "音频暂时中断" && !view.StreamReady && !view.OutputReady, "Stale playing label");
        });
        Test("Recovering, draining and actual output failure remain distinct", () => {
            var snapshot = new WifiSnapshot { Enabled = true, State = SinkState.Flowing, LastAudioAgeMs = 10, Recovering = true };
            Assert(ConnectionPresentation.Wifi(snapshot).Title == "正在恢复播放", "Recovery label");
            snapshot.Draining = true;
            Assert(ConnectionPresentation.Wifi(snapshot).Title == "正在结束播放", "Drain label");
            snapshot.Draining = false; snapshot.Recovering = false;
            Assert(ConnectionPresentation.Wifi(snapshot).Title == "播放设备尚未就绪", "Output failure hidden");
        });
        Test("A fault stays actionable even if Wi-Fi failed to enable", () => {
            var view = ConnectionPresentation.Wifi(new WifiSnapshot { Enabled = false, State = SinkState.Faulted, Detail = "端口绑定失败" });
            Assert(view.Tone == ConnectionTone.Error && view.Description == "端口绑定失败", "Fault lost");
            Assert(ConnectionPresentation.Wifi(new WifiSnapshot { Enabled = true, State = SinkState.Busy }).Title == "正在准备连接", "Preparing label lost");
        });
        void From(WifiReceiver r, IPEndPoint source, byte type, long now, string name = null, uint seq = 0)
        {
            byte[] payload = type == WifiTransport.TypeHello ? System.Text.Encoding.UTF8.GetBytes(name) : type == WifiTransport.TypeAudio ? Signal(5) : null;
            byte[] data = WifiTransport.BuildPacket(type, 0, seq, (uint)(now / 1000), payload);
            Call(r, "HandlePacket", data, data.Length, source, now, false);
        }
        Test("Wi-Fi discovers a second phone without changing active name or stream", () => {
            using var r = new WifiReceiver(log, 0, 0, 80, 200, "test");
            var a = new IPEndPoint(IPAddress.Loopback, 31001); var b = new IPEndPoint(IPAddress.Loopback, 31002);
            From(r, a, WifiTransport.TypeHello, Ticks(1), "Phone A");
            From(r, a, WifiTransport.TypeStart, Ticks(2));
            From(r, b, WifiTransport.TypeHello, Ticks(3), "Phone B");
            From(r, b, WifiTransport.TypeStart, Ticks(4));
            Assert(r.ListPeers().Count == 2 && r.GetSnapshot().DeviceName == "Phone A" && r.GetSnapshot().Remote == a.ToString(), "Foreign phone hijacked stream/name");
            Assert(r.ListPeers().Single(p => p.Active).Id == a.ToString() && r.ListPeers().All(p => !p.Selected), "Automatic source confused with explicit selection");
        });
        Test("Selecting another Wi-Fi phone releases PCM and rejects every old source message", () => {
            using var r = new WifiReceiver(log, 0, 0, 80, 200, "test");
            var a = new IPEndPoint(IPAddress.Loopback, 31001); var b = new IPEndPoint(IPAddress.Loopback, 31002);
            From(r, a, WifiTransport.TypeHello, Ticks(1), "Phone A");
            From(r, a, WifiTransport.TypeAudio, Ticks(2));
            From(r, b, WifiTransport.TypeHello, Ticks(3), "Phone B");
            Assert(!r.SelectPeer("missing") && r.GetSnapshot().Remote == a.ToString(), "Invalid choice disturbed stream");
            Assert(r.SelectPeer(b.ToString()), "Selection failed");
            var selected = r.GetSnapshot();
            Assert(selected.Remote == b.ToString() && selected.Received == 0 && selected.BufferMs == 0 && selected.State == SinkState.Armed && double.IsNaN(selected.LastAudioAgeMs) && double.IsNaN(selected.ControlAgeMs), "Stale audio/control retained on switch");
            From(r, a, WifiTransport.TypeHello, Ticks(4), "Wrong name");
            From(r, a, WifiTransport.TypeStart, Ticks(5));
            From(r, a, WifiTransport.TypeAudio, Ticks(6));
            From(r, a, WifiTransport.TypeStop, Ticks(7));
            Assert(r.GetSnapshot().State == SinkState.Armed && r.GetSnapshot().DeviceName == "Phone B" && (long)Get(r, "lastAnyTicks") == 0, "Old source altered state");
            From(r, b, WifiTransport.TypeAudio, Ticks(8));
            Assert(r.GetSnapshot().Received == 1 && r.GetSnapshot().Remote == b.ToString(), "Selected source did not resume without START");
            Call(r, "GoArmedLocked", "timeout");
            From(r, a, WifiTransport.TypeStart, Ticks(9));
            Assert(r.GetSnapshot().State == SinkState.Armed && r.ListPeers().Single(p => p.Selected).Id == b.ToString(), "Timeout unlocked selected source");
        });
        Test("Wi-Fi discovery is bounded and retained candidates are not claimed online", () => {
            using var r = new WifiReceiver(log, 0, 0, 80, 200, "test");
            for (int i = 0; i < 40; i++) From(r, new IPEndPoint(IPAddress.Loopback, 31000 + i), WifiTransport.TypeHello, -Ticks(11000), "Phone " + i);
            Assert(r.ListPeers().Count == 32 && r.ListPeers().All(p => !p.Recent), "Unbounded or stale presence");
        });
        Test("Two real UDP senders switch through receiver without mixed audio or takeover", () => {
            using var r = new WifiReceiver(log, 0, 0, 80, 200, "test");
            Assert(r.Start(), "Cannot bind test receiver");
            var rx = (System.Net.Sockets.Socket)Get(r, "audioSocket");
            var target = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)rx.LocalEndPoint).Port);
            using var a = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var b = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            void Send(System.Net.Sockets.UdpClient sender, byte type, string name = null, uint seq = 0) {
                var payload = type == WifiTransport.TypeHello ? System.Text.Encoding.UTF8.GetBytes(name) : type == WifiTransport.TypeAudio ? Signal(5) : null;
                var packet = WifiTransport.BuildPacket(type, 0, seq, 0, payload); sender.Send(packet, packet.Length, target);
            }
            void Wait(Func<bool> check, string message) => Assert(SpinWait.SpinUntil(check, 2000), message);
            Send(a, WifiTransport.TypeHello, "UDP Phone A"); Send(b, WifiTransport.TypeHello, "UDP Phone B");
            Wait(() => r.ListPeers().Count == 2, "Discovery over UDP failed");
            string aId = a.Client.LocalEndPoint.ToString(), bId = b.Client.LocalEndPoint.ToString();
            Assert(r.SelectPeer(aId), "Select A"); Send(a, WifiTransport.TypeAudio);
            Wait(() => r.GetSnapshot().Received == 1, "A audio not received");
            Assert(r.SelectPeer(bId) && r.GetSnapshot().BufferMs == 0, "Switch did not clear A PCM");
            long foreign = r.GetSnapshot().Foreign;
            Send(a, WifiTransport.TypeStart); Send(a, WifiTransport.TypeAudio, seq: 1); Send(a, WifiTransport.TypeStop);
            Wait(() => r.GetSnapshot().Foreign >= foreign + 3, "Old source was not rejected");
            Assert(r.GetSnapshot().Remote == bId && r.GetSnapshot().State == SinkState.Armed, "Old source changed selected session");
            Send(b, WifiTransport.TypeAudio);
            Wait(() => r.GetSnapshot().Remote == bId && r.GetSnapshot().Received == 1 && r.GetSnapshot().BufferMs == 5, "B audio not independently buffered");
        });
        Test("BAS2 microphone golden header is isolated from legacy playback", () => {
            var pcm = Signal(5);
            var packet = DualStreamTransport.Build(1, 0x12345678, uint.MaxValue, 0x80000001, pcm, true);
            byte[] expected = { 0x42,0x41,0x53,0x32,1,1,0,0,0x78,0x56,0x34,0x12,255,255,255,255,1,0,0,128 };
            Assert(packet.Take(20).SequenceEqual(expected), "Wire format mismatch");
            Assert(DualStreamTransport.TryRead(packet, packet.Length, out var h) && h.Stream == 1 && h.Sequence == uint.MaxValue, "Decode failed");
            Assert(!WifiTransport.TryReadHeader(packet, packet.Length, out _), "Legacy receiver accepts microphone");
            packet[6] = 1;
            Assert(!DualStreamTransport.TryRead(packet, packet.Length, out _), "Reserved field accepted");
        });
        byte[] Constant(short value) {
            var data = new byte[20 * 192];
            for (int i = 0; i < data.Length; i += 2) { data[i] = (byte)value; data[i + 1] = (byte)(value >> 8); }
            return data;
        }
        Test("Media and mic remain isolated; sharing never changes headphones", () => {
            var router = new PhoneAudioRouter(); var media = Constant(2000); var mic = Constant(3000);
            router.AddMedia(media, 0, media.Length); router.AddMicrophone(mic, 0, mic.Length);
            var a = new byte[media.Length]; var b = new byte[mic.Length];
            router.Headphones.Read(a, 0, a.Length); router.VirtualMicrophone.Read(b, 0, b.Length);
            Assert(a.SequenceEqual(media) && b.SequenceEqual(mic), "Streams leaked across outputs");
            router.ShareMedia = true;
            router.AddMedia(media, 0, media.Length); router.AddMicrophone(mic, 0, mic.Length);
            router.Headphones.Read(a, 0, a.Length); router.VirtualMicrophone.Read(b, 0, b.Length);
            Assert(a.SequenceEqual(media) && b.SequenceEqual(Constant(5000)), "Sharing interrupted media or lost mix");
        });
        Test("Share toggle clears queued media without discarding headphone audio", () => {
            var router = new PhoneAudioRouter { ShareMedia = true }; var media = Constant(2000);
            router.AddMedia(media, 0, media.Length); router.ShareMedia = false; router.ShareMedia = true;
            var a = new byte[media.Length]; var b = new byte[media.Length];
            router.Headphones.Read(a, 0, a.Length); router.VirtualMicrophone.Read(b, 0, b.Length);
            Assert(a.SequenceEqual(media) && b.All(v => v == 0), "Toggle leaked stale music or cleared headphones");
        });
        Test("Virtual microphone mix saturates instead of integer wrap", () => {
            var router = new PhoneAudioRouter { ShareMedia = true }; var loud = Constant(30000);
            router.AddMedia(loud, 0, loud.Length); router.AddMicrophone(loud, 0, loud.Length);
            var actual = new byte[loud.Length]; router.VirtualMicrophone.Read(actual, 0, actual.Length);
            Assert(actual.SequenceEqual(Constant(short.MaxValue)), "Clipping wrapped PCM sign");
        });
        Test("Microphone mute and volume change only the virtual mic, not headphones or shared music", () => {
            var router=new PhoneAudioRouter { ShareMedia=true, MicrophoneVolume=50 };
            var media=Constant(2000); var mic=Constant(6000); var a=new byte[media.Length]; var b=new byte[mic.Length];
            router.AddMedia(media,0,media.Length); router.AddMicrophone(mic,0,mic.Length);
            router.Headphones.Read(a,0,a.Length); router.VirtualMicrophone.Read(b,0,b.Length);
            Assert(a.SequenceEqual(media)&&b.SequenceEqual(Constant(5000)),"Gain applied to wrong source or more than once");
            router.MicrophoneMuted=true;
            router.AddMedia(media,0,media.Length); router.AddMicrophone(mic,0,mic.Length);
            router.Headphones.Read(a,0,a.Length); router.VirtualMicrophone.Read(b,0,b.Length);
            Assert(a.SequenceEqual(media)&&b.SequenceEqual(media),"Mute removed shared media or leaked microphone");
            router.MicrophoneMuted=false; router.ShareMedia=false; router.Clear();
            router.AddMicrophone(mic,0,mic.Length); router.VirtualMicrophone.Read(b,0,b.Length);
            Assert(b.SequenceEqual(Constant(3000)),"Unmute failed or gain applied twice");
        });
        Test("PC authoritative microphone revisions reject duplicate and conflicting phone requests", () => {
            var state=new MicrophoneState(); var first=state.Current;
            Assert(state.ApplyRemote(1,first.Revision,true,40),"Phone request rejected");
            var applied=state.Current;
            Assert(applied.Muted&&applied.Volume==40&&applied.Acknowledged==1,"Phone state lost");
            Assert(!state.ApplyRemote(1,applied.Revision,false,100),"Duplicate request replayed");
            state.SetLocal(false,70,true); var pc=state.Current;
            Assert(!state.ApplyRemote(2,applied.Revision,false,5),"Stale phone request overwrote new PC state");
            Assert(state.Current.Volume==70&&state.Current.Acknowledged==2,"Conflict ACK or PC state lost");
            Assert(state.ApplyRemote(3,applied.Revision,true,5),"A new mute request was lost to a volume conflict");
            Assert(state.Current.Muted&&state.Current.Volume==70,"Mute conflict changed the newer PC volume");
            Assert(state.ApplyRemote(4,state.Current.Revision,true,25),"Current request rejected");
            Assert(state.Current.Volume==25&&state.Current.Share,"Remote control changed media sharing");
            Assert(state.ApplyRemote(5,state.Current.Revision,false,55,2),"Volume-only update rejected");
            Assert(state.Current.Muted&&state.Current.Volume==55,"Volume update unexpectedly unmuted mic");
            Assert(state.ApplyRemote(6,state.Current.Revision,false,100,1),"Mute-only update rejected");
            Assert(!state.Current.Muted&&state.Current.Volume==55,"Mute update overwrote volume");
            state.BeginSession(); Assert(state.Current.Acknowledged==0&&state.Current.Volume==55,"Reconnect lost saved controls");
        });
        Test("Dual microphone packets cannot enter another source or session", () => {
            using var receiver=new WifiReceiver(log,0,0,80,200,"test");
            var from=new IPEndPoint(IPAddress.Loopback,31001); var other=new IPEndPoint(IPAddress.Loopback,31002);
            var flags=new byte[]{3,0,0,0};
            var start=WifiTransport.BuildPacket(10,0,123,0,flags);
            Call(receiver,"HandlePacket",start,start.Length,from,Ticks(1),false);
            var valid=DualStreamTransport.Build(1,123,0,5000,Signal(5));
            Call(receiver,"HandlePacket",valid,valid.Length,other,Ticks(2),false);
            var stale=DualStreamTransport.Build(1,124,0,5000,Signal(5));
            Call(receiver,"HandlePacket",stale,stale.Length,from,Ticks(3),false);
            Assert(receiver.GetSnapshot().Received==0,"Foreign microphone accepted");
            Call(receiver,"HandlePacket",valid,valid.Length,from,Ticks(4),false);
            Assert(receiver.GetSnapshot().DualMode&&receiver.GetSnapshot().Received==1,"Selected microphone not enqueued");
            var stop=WifiTransport.BuildPacket(WifiTransport.TypeStop,0,124,0,null);
            Call(receiver,"HandlePacket",stop,stop.Length,from,Ticks(5),false);
            Assert(receiver.GetSnapshot().DualMode,"Old STOP terminated current microphone");
            stop=WifiTransport.BuildPacket(WifiTransport.TypeStop,0,123,0,null);
            Call(receiver,"HandlePacket",stop,stop.Length,from,Ticks(6),false);
            Assert(!receiver.GetSnapshot().DualMode,"Current STOP did not end microphone");
        });
        Test("Independent dual-stream reorder waits for missing packets and handles wrap", () => {
            var stream=new OrderedAudioStream(); var got=new List<byte[]>();
            var one=Constant(1); var two=Constant(2); var three=Constant(3);
            stream.Add(uint.MaxValue,one); stream.Drain(Ticks(1),got.Add);
            stream.Add(1,three); stream.Drain(Ticks(2),got.Add);
            Assert(got.Count==1,"Reorder deadline ignored");
            stream.Add(0,two); stream.Drain(Ticks(3),got.Add);
            Assert(got.Count==3&&got[0].SequenceEqual(one)&&got[1].SequenceEqual(two)&&got[2].SequenceEqual(three),"Wrapped stream reordered incorrectly");
        });
        Test("Virtual mic selection excludes physical and capture endpoints", () => {
            Assert(!EndpointRegistry.IsCableRender(new EndpointInfo { IsRender=true,IsActive=true,IsVirtual=true,Name="Nahimic speakers" }),"Physical DSP endpoint mistaken for microphone cable");
            Assert(!EndpointRegistry.IsCableRender(new EndpointInfo { IsRender=false,IsActive=true,Name="CABLE Output (VB-Audio Virtual Cable)" }),"Capture endpoint selected for rendering");
            Assert(EndpointRegistry.IsCableRender(new EndpointInfo { IsRender=true,IsActive=true,Name="CABLE Input (VB-Audio Virtual Cable)" }),"Official cable input not found");
        });
        Console.WriteLine($"{passed} tests passed");
    }
    sealed class FailingPlayer : IWavePlayer
    {
        public int PlayCalls;
        public PlaybackState PlaybackState => PlaybackState.Stopped;
        public WaveFormat OutputWaveFormat => new WaveFormat(48000, 16, 2);
        public float Volume { get; set; }
        public event EventHandler<StoppedEventArgs> PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider provider) { }
        public void Play() { PlayCalls++; throw new InvalidOperationException("Simulated device startup failure"); }
        public void Pause() { }
        public void Stop() { }
        public void Dispose() { }
    }
    static void Record(int seconds, string path)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var capture = new WasapiLoopbackCapture(device);
        using var writer = new WaveFileWriter(path, capture.WaveFormat);
        object gate = new object();
        capture.DataAvailable += (_, e) => { lock (gate) writer.Write(e.Buffer, 0, e.BytesRecorded); };
        using var stopped = new ManualResetEventSlim();
        Exception error = null;
        capture.RecordingStopped += (_, e) => { error = e.Exception; stopped.Set(); };
        capture.StartRecording(); Thread.Sleep(seconds * 1000); capture.StopRecording();
        Assert(stopped.Wait(5000), "Recording stop timeout"); if (error != null) throw error;
        writer.Flush();
        Console.WriteLine(JsonSerializer.Serialize(new { device = device.FriendlyName, rate = capture.WaveFormat.SampleRate,
            bits = capture.WaveFormat.BitsPerSample, channels = capture.WaveFormat.Channels, format = capture.WaveFormat.ToString(), seconds, bytes = writer.Length, path }));
    }
}
