using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SinkLite.Platform
{
    // Native Winsock RFCOMM works in the unpackaged desktop application.
    internal sealed class RfcommListener : IDisposable
    {
        public static readonly Guid ServiceId=new Guid("c774d750-0c0b-4cd9-a4af-a060d0b14925");
        private Socket socket;
        private Query registration;
        private IntPtr serviceId, address, addresses;
        private bool registered;
        public void Start()
        {
            try {
                socket=new Socket((AddressFamily)32,SocketType.Stream,(ProtocolType)3);
                // Require an authenticated/paired connection (SO_BTH_AUTHENTICATE).
                socket.SetSocketOption((SocketOptionLevel)3,unchecked((SocketOptionName)(int)0x80000001),1);
                socket.Bind(new BluetoothEndpoint(-1)); socket.Listen(1);
                var local=socket.LocalEndPoint.Serialize(); var bytes=new byte[30];
                for(int i=0;i<30;i++) bytes[i]=local[i];
                serviceId=Marshal.AllocHGlobal(16); Marshal.StructureToPtr(ServiceId,serviceId,false);
                address=Marshal.AllocHGlobal(30); Marshal.Copy(bytes,0,address,30);
                var csa=new CsAddress { LocalAddress=new NativeSocketAddress { Address=address,Length=30 },SocketType=1,Protocol=3 };
                addresses=Marshal.AllocHGlobal(Marshal.SizeOf<CsAddress>()); Marshal.StructureToPtr(csa,addresses,false);
                registration=new Query { Size=Marshal.SizeOf<Query>(),Name="BlueAudioSink Microphone",ClassId=serviceId,Namespace=16,AddressCount=1,Addresses=addresses };
                if(WSASetService(ref registration,0,0)!=0) throw new SocketException(Marshal.GetLastWin32Error());
                registered=true;
            } catch { Dispose(); throw; }
        }
        public Socket Accept() => socket.Accept();
        public void Dispose()
        {
            if(registered) { WSASetService(ref registration,2,0); registered=false; }
            try { socket?.Dispose(); } catch { } socket=null;
            if(addresses!=IntPtr.Zero) Marshal.FreeHGlobal(addresses);
            if(address!=IntPtr.Zero) Marshal.FreeHGlobal(address);
            if(serviceId!=IntPtr.Zero) Marshal.FreeHGlobal(serviceId);
            addresses=address=serviceId=IntPtr.Zero;
        }
        // SOCKET_ADDRESS includes trailing pointer-alignment padding on x64.
        // Preserve the native nesting: flattening moves SocketType/Protocol by four
        // bytes and causes WSASetService to reject the SDP record with WSAEINVAL.
        [StructLayout(LayoutKind.Sequential)] private struct NativeSocketAddress {
            public IntPtr Address; public int Length;
        }
        [StructLayout(LayoutKind.Sequential)] private struct CsAddress {
            public NativeSocketAddress LocalAddress,RemoteAddress; public int SocketType,Protocol;
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Query {
            public int Size; [MarshalAs(UnmanagedType.LPWStr)] public string Name;
            public IntPtr ClassId,Version,Comment; public uint Namespace;
            public IntPtr Provider,Context; public uint ProtocolCount; public IntPtr Protocols,QueryString;
            public uint AddressCount; public IntPtr Addresses; public uint Flags; public IntPtr Blob;
        }
        [DllImport("ws2_32.dll",EntryPoint="WSASetServiceW",CharSet=CharSet.Unicode,SetLastError=true)]
        private static extern int WSASetService(ref Query query,int operation,uint flags);
        private sealed class BluetoothEndpoint : EndPoint {
            private readonly byte[] bytes;
            public BluetoothEndpoint(int port) { bytes=new byte[30]; bytes[0]=32; BitConverter.GetBytes(port).CopyTo(bytes,26); }
            private BluetoothEndpoint(SocketAddress a) { bytes=new byte[30]; for(int i=0;i<30;i++) bytes[i]=a[i]; }
            public override AddressFamily AddressFamily => (AddressFamily)32;
            public override SocketAddress Serialize() { var a=new SocketAddress(AddressFamily,30); for(int i=0;i<30;i++) a[i]=bytes[i]; return a; }
            public override EndPoint Create(SocketAddress a) { if(a.Family!=AddressFamily || a.Size<30) throw new ArgumentException("RFCOMM endpoint"); return new BluetoothEndpoint(a); }
        }
    }
}
