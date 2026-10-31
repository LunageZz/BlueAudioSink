using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using SinkLite.Platform;

namespace SinkLite.Core
{
    internal sealed class BluetoothMicrophoneReceiver : IDisposable
    {
        private readonly object sync=new object();
        private readonly Log log;
        private readonly MicrophoneState controls;
        private RfcommListener listener;
        private Socket client;
        private WifiReceiver receiver;
        private Thread worker;
        private volatile bool running;
        private volatile string status="蓝牙麦克风未开启";
        public string Status => status;
        public bool Connected { get { lock(sync) return client!=null; } }
        public void Disconnect() { lock(sync) { try { client?.Close(); } catch { } } }
        public WifiSnapshot Current { get { lock(sync) return receiver?.GetSnapshot() ?? new WifiSnapshot { MicrophoneStatus=status }; } }
        public BluetoothMicrophoneReceiver(Log log,MicrophoneState controls) { this.log=log; this.controls=controls; }
        public void Start()
        {
            lock(sync) {
                if(running) return;
                // Stop has a bounded wait. An old worker must finish all finally blocks
                // before another worker can publish listener/client/receiver fields.
                if(worker!=null && worker.IsAlive) { status="上次蓝牙麦克风仍在关闭，请稍后点击连接重试"; return; }
                running=true;
                worker=new Thread(Run) { IsBackground=true,Name="BluetoothMicrophone" }; worker.Start();
            }
        }
        private void Run()
        {
            try {
                var service=new RfcommListener();
                lock(sync) { if(!running) return; listener=service; service.Start(); }
                while(running) {
                    status="等待手机蓝牙麦克风连接";
                    Socket peer=service.Accept();
                    lock(sync) { if(!running) { peer.Dispose(); break; } client=peer; }
                    try {
                        peer.SendTimeout=2000;
                        using(var stream=new NetworkStream(peer,false))
                        using(var engine=new WifiReceiver(log,45678,45679,160,400,"Bluetooth",controls)) {
                            // Bounded reliable control queue: a slow peer cannot block the playout thread.
                            using(var outgoing=new System.Collections.Concurrent.BlockingCollection<byte[]>(32)) {
                                var writer=new Thread(()=> {
                                    try { foreach(var packet in outgoing.GetConsumingEnumerable()) stream.Write(BluetoothAudioCodec.Frame(packet)); }
                                    catch { try { peer.Close(); } catch { } }
                                }) { IsBackground=true,Name="BluetoothControl" };
                                writer.Start();
                                lock(sync) receiver=engine;
                                engine.StartLocalTransport(packet=> { if(!outgoing.TryAdd(packet)) { try { peer.Close(); } catch { } } });
                                status="蓝牙麦克风通道已连接";
                                try { while(running) engine.ReceiveTransportPacket(BluetoothAudioCodec.Read(stream)); }
                                finally { engine.Stop(); outgoing.CompleteAdding(); peer.Close(); writer.Join(2500); lock(sync) receiver=null; }
                            }
                        }
                    } catch(Exception ex) { if(running) log.Warn("Bluetooth.MicrophoneDisconnected",("detail",ex.Message)); }
                    finally { peer.Dispose(); lock(sync) client=null; }
                }
            } catch(Exception ex) { if(running) { status="蓝牙麦克风不可用："+ex.Message; log.Error("Bluetooth.MicrophoneFault",("detail",ex.Message)); } }
            finally { lock(sync) { listener?.Dispose(); listener=null; } running=false; }
        }
        public void Stop()
        {
            Thread thread;
            lock(sync) { running=false; client?.Dispose(); listener?.Dispose(); listener=null; thread=worker; }
            bool stopped=thread==null || thread.Join(5000);
            status=stopped ? "蓝牙麦克风未开启" : "上次蓝牙麦克风仍在关闭，请稍后点击连接重试";
        }
        public void Dispose() => Stop();
    }
}
