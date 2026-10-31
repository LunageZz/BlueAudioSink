package com.blueaudiosink.sender.net

/** BAS2 is deliberately distinct from BAS1 so legacy PCs cannot play microphone packets. */
object DualStreamPacketCodec {
    const val HEADER_SIZE = 20
    const val MEDIA = 0
    const val MICROPHONE = 1
    const val PACKET_SIZE = HEADER_SIZE + PacketCodec.AUDIO_PAYLOAD_SIZE

    fun build(stream: Int, session: Int, sequence: Int, timestampUs: Int,
              pcm: ByteArray, discontinuity: Boolean = false): ByteArray {
        require(stream in MEDIA..MICROPHONE && session != 0)
        require(pcm.size == PacketCodec.AUDIO_PAYLOAD_SIZE)
        return ByteArray(PACKET_SIZE).also {
            it[0] = 0x42; it[1] = 0x41; it[2] = 0x53; it[3] = 0x32
            it[4] = stream.toByte()
            it[5] = if (discontinuity) 1 else 0
            PacketCodec.putIntLE(it, 8, session)
            PacketCodec.putIntLE(it, 12, sequence)
            PacketCodec.putIntLE(it, 16, timestampUs)
            pcm.copyInto(it, HEADER_SIZE)
        }
    }
}
