package com.blueaudiosink.sender.capture
import com.blueaudiosink.sender.net.PacketCodec
import org.junit.Assert.*
import org.junit.Test
class MicrophonePcmTest {
    @Test fun monoSamplesAreCopiedToBothChannelsWithoutChangingSignOrGain() {
        val mono=ByteArray(PacketCodec.AUDIO_PAYLOAD_SIZE/2)
        val stereo=ByteArray(PacketCodec.AUDIO_PAYLOAD_SIZE)
        val samples=intArrayOf(-32768,-1,0,1,32767)
        for(frame in 0 until PacketCodec.FRAMES_PER_PACKET) {
            val value=samples[frame%samples.size]
            mono[frame*2]=value.toByte(); mono[frame*2+1]=(value shr 8).toByte()
        }
        PcmRecorder.monoToStereo(mono,stereo)
        for(frame in 0 until PacketCodec.FRAMES_PER_PACKET) for(channel in 0..1) {
            val offset=frame*4+channel*2
            val actual=((stereo[offset].toInt() and 255) or (stereo[offset+1].toInt() shl 8)).toShort().toInt()
            assertEquals(samples[frame%samples.size],actual)
        }
    }
}
