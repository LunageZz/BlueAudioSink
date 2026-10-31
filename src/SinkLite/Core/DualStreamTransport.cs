using System;
using System.Buffers.Binary;

namespace SinkLite.Core
{
    // A distinct magic prevents legacy receivers from playing microphone PCM as media.
    internal static class DualStreamTransport
    {
        public const uint Magic = 0x32534142; // BAS2
        public const int HeaderSize = 20;
        public const int PacketBytes = HeaderSize + WifiTransport.PayloadBytes;
        public const byte Media = 0;
        public const byte Microphone = 1;

        public struct Header
        {
            public byte Stream;
            public byte Flags;
            public uint Session;
            public uint Sequence;
            public uint TimestampUs;
        }

        public static bool TryRead(byte[] packet, int length, out Header header)
        {
            header = default;
            if (packet == null || length != PacketBytes || length > packet.Length ||
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(0, 4)) != Magic ||
                packet[4] > Microphone || (packet[5] & ~WifiTransport.FlagDiscontinuity) != 0 ||
                packet[6] != 0 || packet[7] != 0) return false;
            header.Stream = packet[4];
            header.Flags = packet[5];
            header.Session = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4));
            header.Sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4));
            header.TimestampUs = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16, 4));
            return header.Session != 0;
        }

        public static byte[] Build(byte stream, uint session, uint sequence, uint timestampUs,
            byte[] pcm, bool discontinuity = false)
        {
            if (stream > Microphone || session == 0 || pcm == null || pcm.Length != WifiTransport.PayloadBytes)
                throw new ArgumentException("Invalid dual-stream audio frame");
            var packet = new byte[PacketBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), Magic);
            packet[4] = stream;
            packet[5] = discontinuity ? WifiTransport.FlagDiscontinuity : (byte)0;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), session);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16, 4), timestampUs);
            pcm.CopyTo(packet, HeaderSize);
            return packet;
        }
    }
}
