package com.blueaudiosink.sender.net

interface PacketTransport {
    fun open(onPacket: (ByteArray) -> Unit, onFailure: (String) -> Unit): Boolean
    fun send(packet: ByteArray, audio: Boolean = false)
    fun close()
}
