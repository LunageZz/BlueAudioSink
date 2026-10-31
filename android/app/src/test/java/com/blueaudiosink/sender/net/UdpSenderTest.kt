package com.blueaudiosink.sender.net

import android.content.ContextWrapper
import org.junit.Assert.assertEquals
import org.junit.Test
import java.net.DatagramPacket
import java.net.InetAddress

class UdpSenderTest {
    @Test
    fun heartbeatMustComeFromCurrentComputerControlPort() {
        // No socket or Android service is started; only packet dispatch is exercised.
        val sender = UdpSender(ContextWrapper(null))
        val seen = mutableListOf<Pair<String, Int>>()
        sender.listener = object : UdpSender.Listener {
            override fun onOffer(device: DiscoveredDevice) = Unit
            override fun onError(message: String) = error(message)
            override fun onPing(targetHost: String, targetPort: Int, t1Us: Int, receivedAtUs: Int) {
                seen += targetHost to targetPort
            }
        }
        val handle = UdpSender::class.java.getDeclaredMethod(
            "handlePing", DatagramPacket::class.java, ByteArray::class.java,
        ).apply { isAccessible = true }
        fun ping(ip: String, port: Int = PacketCodec.CONTROL_PORT) {
            val data = PacketCodec.build(PacketCodec.TYPE_PING, 0, 123, null)
            handle.invoke(sender, DatagramPacket(data, data.size, InetAddress.getByName(ip), port), data)
        }
        try {
            ping("127.0.0.1") // No selected target.
            sender.setTarget("127.0.0.1", 50001)
            ping("127.0.0.2")
            ping("127.0.0.1", 50001) // Audio port is not the PC control port.
            assertEquals(0, seen.size)
            ping("127.0.0.1")
            sender.setTarget("127.0.0.2", 50002)
            ping("127.0.0.1") // Previous computer no longer counts.
            ping("127.0.0.2")
            sender.clearTarget()
            ping("127.0.0.2")
            assertEquals(listOf("127.0.0.1" to 50001, "127.0.0.2" to 50002), seen)
        } finally {
            sender.close()
        }
    }
}
