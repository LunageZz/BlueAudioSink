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
            var b = (PlayoutBuffer)Get(o, "buffer"); byte[] y = new byte[x.Length]; b.Read(y, 0, y.Length);
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
        Console.WriteLine($"{passed} tests passed");
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
