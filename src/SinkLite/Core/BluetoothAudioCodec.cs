using System;
using System.IO;

namespace SinkLite.Core
{
    // Wire framing: u16 LE length, u8 codec (0=BAS1 control, 1=ADPCM BAS2), u8 reserved=0.
    internal static class BluetoothAudioCodec
    {
        private static readonly int[] Steps={7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767};
        private static readonly int[] Indices={-1,-1,-1,-1,2,4,6,8};
        public static byte[] Read(Stream stream)
        {
            byte[] header=new byte[4]; stream.ReadExactly(header);
            int n=header[0]|header[1]<<8;
            if(n<16 || n>2048 || header[3]!=0 || header[2]>1) throw new InvalidDataException("蓝牙帧头无效");
            byte[] data=new byte[n]; stream.ReadExactly(data);
            if(header[2]==0) {
                if(!WifiTransport.TryReadHeader(data,data.Length,out var h) || h.Type==WifiTransport.TypeAudio) throw new InvalidDataException("蓝牙控制帧无效");
                return data;
            }
            return Decode(data);
        }
        public static byte[] Frame(byte[] data)
        {
            if(data.Length<16 || data.Length>2048) throw new ArgumentException("Frame size");
            var frame=new byte[data.Length+4]; frame[0]=(byte)data.Length; frame[1]=(byte)(data.Length>>8);
            Buffer.BlockCopy(data,0,frame,4,data.Length); return frame;
        }
        public static byte[] Decode(byte[] data)
        {
            if(data.Length<28 || data[0]!=0x42 || data[1]!=0x41 || data[2]!=0x53 || data[3]!=0x32 || data[4]>1 || data[22]!=0 || data[23]!=0) throw new InvalidDataException("ADPCM header");
            int channels=data[4]==1?1:2, stride=data[4]==1?3:2, offset=24+channels*4, count=240/stride*channels;
            if(data[20]!=channels || data[21]!=stride || data.Length!=offset+count/2) throw new InvalidDataException("ADPCM size/format");
            int[] p=new int[channels], ix=new int[channels];
            for(int ch=0;ch<channels;ch++) {
                int at=24+ch*4; p[ch]=(short)(data[at]|data[at+1]<<8); ix[ch]=data[at+2];
                if(ix[ch]>88 || data[at+3]!=0) throw new InvalidDataException("ADPCM state");
            }
            byte[] pcm=new byte[DualStreamTransport.PacketBytes]; Buffer.BlockCopy(data,0,pcm,0,20);
            for(int i=0;i<count;i++) {
                int ch=i%channels, code=(data[offset+i/2]>>((i%2)*4))&15, step=Steps[ix[ch]];
                int change=(step>>3)+((code&4)!=0?step:0)+((code&2)!=0?step>>1:0)+((code&1)!=0?step>>2:0);
                p[ch]=Math.Clamp(p[ch]+((code&8)!=0?-change:change),short.MinValue,short.MaxValue);
                ix[ch]=Math.Clamp(ix[ch]+Indices[code&7],0,88);
                int frame=i/channels*stride;
                for(int j=0;j<stride;j++) for(int c=0;c<(channels==1?2:1);c++) {
                    int at=20+(frame+j)*4+(channels==1?c:ch)*2; pcm[at]=(byte)p[ch]; pcm[at+1]=(byte)(p[ch]>>8);
                }
            }
            if(!DualStreamTransport.TryRead(pcm,pcm.Length,out _)) throw new InvalidDataException("BAS2 header");
            return pcm;
        }
    }
}
