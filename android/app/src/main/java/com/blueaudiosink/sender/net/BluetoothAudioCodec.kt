package com.blueaudiosink.sender.net

/** Independently decodable IMA ADPCM blocks. Mic: 16 kHz mono; media copy: 24 kHz stereo. */
object BluetoothAudioCodec {
    private val steps = intArrayOf(7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767)
    private val indices = intArrayOf(-1,-1,-1,-1,2,4,6,8)
    class Encoder {
        private val predictors = Array(2) { IntArray(2) }
        private val indexes = Array(2) { IntArray(2) { 40 } }
        fun encode(packet: ByteArray): ByteArray {
            require(packet.size == DualStreamPacketCodec.PACKET_SIZE)
            val stream = packet[4].toInt(); require(stream in 0..1)
            val channels = if (stream == 1) 1 else 2
            val stride = if (stream == 1) 3 else 2
            val count = 240 / stride * channels
            val offset = 24 + channels * 4
            val out = ByteArray(offset + count / 2)
            packet.copyInto(out, 0, 0, 20)
            out[20] = channels.toByte(); out[21] = stride.toByte()
            val p = predictors[stream]; val ix = indexes[stream]
            for (ch in 0 until channels) {
                val at = 24 + ch * 4
                out[at] = p[ch].toByte(); out[at+1] = (p[ch] shr 8).toByte(); out[at+2] = ix[ch].toByte()
            }
            for (i in 0 until count) {
                val ch = i % channels
                val frame = i / channels * stride
                var sample = 0
                for (j in 0 until stride) {
                    val at = 20 + (frame+j)*4 + ch*2
                    sample += ((packet[at].toInt() and 255) or (packet[at+1].toInt() shl 8)).toShort().toInt()
                }
                sample /= stride
                val step = steps[ix[ch]]
                val delta = sample-p[ch]
                var rem = kotlin.math.abs(delta)
                var code = if (delta < 0) 8 else 0
                var change = step shr 3
                for (bit in 2 downTo 0) {
                    val value = step shr (2-bit)
                    if (rem >= value) { code = code or (1 shl bit); rem -= value; change += value }
                }
                p[ch] = (p[ch] + if (code and 8 != 0) -change else change).coerceIn(-32768,32767)
                ix[ch] = (ix[ch]+indices[code and 7]).coerceIn(0,88)
                val pos = offset+i/2
                out[pos] = (out[pos].toInt() or (code shl ((i%2)*4))).toByte()
            }
            return out
        }
    }
}
