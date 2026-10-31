package com.blueaudiosink.sender.net

import org.junit.Assert.*
import org.junit.Test

class DualStreamPacketCodecTest {
    @Test fun microphoneWireFormatMatchesWindowsGoldenHeader() {
        val pcm = ByteArray(PacketCodec.AUDIO_PAYLOAD_SIZE) { (it % 127).toByte() }
        val packet = DualStreamPacketCodec.build(1, 0x12345678, -1, Int.MIN_VALUE + 1, pcm, true)
        val expected = byteArrayOf(0x42, 0x41, 0x53, 0x32, 1, 1, 0, 0,
            0x78, 0x56, 0x34, 0x12, -1, -1, -1, -1, 1, 0, 0, -128)
        assertArrayEquals(expected, packet.copyOfRange(0, 20))
        assertArrayEquals(pcm, packet.copyOfRange(20, packet.size))
        assertFalse(PacketCodec.isValid(packet, packet.size))
    }
    @Test fun invalidStreamAndSessionCannotProducePackets() {
        val pcm = ByteArray(PacketCodec.AUDIO_PAYLOAD_SIZE)
        for ((stream, session) in listOf(-1 to 1, 2 to 1, 0 to 0)) {
            try { DualStreamPacketCodec.build(stream, session, 0, 0, pcm); fail("Invalid packet accepted") }
            catch (_: IllegalArgumentException) { }
        }
    }
}
