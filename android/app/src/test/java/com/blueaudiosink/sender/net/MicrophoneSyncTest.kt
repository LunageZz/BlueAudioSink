package com.blueaudiosink.sender.net

import android.content.ContextWrapper
import java.net.DatagramPacket
import java.net.InetAddress
import org.junit.Assert.*
import org.junit.Test

class MicrophoneSyncTest {
    private fun set(sender: UdpSender, key: String, value: Any) {
        UdpSender::class.java.getDeclaredField(key).apply { isAccessible=true }.set(sender,value)
    }
    private fun get(sender: UdpSender, key: String): Any? =
        UdpSender::class.java.getDeclaredField(key).apply { isAccessible=true }.get(sender)
    @Test fun controlRequestCarriesSessionRevisionMuteAndVolume() {
        val sender=UdpSender(ContextWrapper(null))
        try {
            set(sender,"session",123); set(sender,"revision",10); set(sender,"dualFlags",3)
            assertTrue(sender.requestMicrophone(true,40))
            val packet=get(sender,"pendingControl") as ByteArray
            assertEquals(32,packet.size)
            assertEquals(12,PacketCodec.typeOf(packet,packet.size))
            assertEquals(3,packet[5].toInt())
            assertEquals(1,PacketCodec.getIntLE(packet,8))
            assertEquals(123,PacketCodec.getIntLE(packet,16))
            assertEquals(10,PacketCodec.getIntLE(packet,20))
            assertEquals(1,PacketCodec.getIntLE(packet,24))
            assertEquals(40,PacketCodec.getIntLE(packet,28))
        } finally { sender.close() }
    }
    @Test fun missingControlAckIsRetriedOverUdpAndAcknowledgedRequestStopsRetrying() {
        val sender=UdpSender(ContextWrapper(null))
        val peer=java.net.DatagramSocket(java.net.InetSocketAddress("127.0.0.1",0))
        val socket=java.net.DatagramSocket(java.net.InetSocketAddress("127.0.0.1",0))
        val retry=UdpSender::class.java.getDeclaredMethod("retryPending").apply { isAccessible=true }
        try {
            peer.soTimeout=1500
            set(sender,"socket",socket); sender.setTarget("127.0.0.1",peer.localPort)
            set(sender,"session",123); set(sender,"revision",10); set(sender,"dualFlags",3)
            assertTrue(sender.requestMicrophone(true,40))
            fun receive():ByteArray {
                val packet=DatagramPacket(ByteArray(100),100); peer.receive(packet)
                return packet.data.copyOf(packet.length)
            }
            val first=receive()
            // SDK unit-test clocks return zero: seed an older retry deadline explicitly.
            set(sender,"lastRetryMs",-1000L); retry.invoke(sender)
            assertArrayEquals(first,receive())
            val payload=ByteArray(24)
            listOf(123,1,1,40,3,0).forEachIndexed { i,v -> PacketCodec.putIntLE(payload,i*4,v) }
            val ack=PacketCodec.build(11,11,0,payload,payload.size)
            UdpSender::class.java.getDeclaredMethod("handleMicrophoneState",DatagramPacket::class.java,ByteArray::class.java)
                .apply { isAccessible=true }.invoke(sender,DatagramPacket(ack,ack.size,InetAddress.getByName("127.0.0.1"),45678),ack)
            assertNull(get(sender,"pendingControl"))
            set(sender,"lastRetryMs",-1000L); retry.invoke(sender)
            peer.soTimeout=200
            try { receive(); fail("Acknowledged command was retransmitted") }
            catch (_: java.net.SocketTimeoutException) { }
        } finally { sender.close(); peer.close() }
    }
    @Test fun stateAckFiltersSessionSourceAndOldRevisionThenClearsPendingRequest() {
        val sender=UdpSender(ContextWrapper(null)); val seen=mutableListOf<MicrophoneStatus>()
        sender.listener=object:UdpSender.Listener {
            override fun onOffer(device:DiscoveredDevice) {}
            override fun onPing(targetHost:String,targetPort:Int,t1Us:Int,receivedAtUs:Int) {}
            override fun onError(message:String) { error(message) }
            override fun onMicrophoneState(status:MicrophoneStatus) { seen+=status }
        }
        val handle=UdpSender::class.java.getDeclaredMethod("handleMicrophoneState",DatagramPacket::class.java,ByteArray::class.java).apply { isAccessible=true }
        fun state(session:Int=123,revision:Int=11,ack:Int=1,ip:String="127.0.0.1",muted:Int=1,volume:Int=40) {
            val payload=ByteArray(24)
            listOf(session,ack,muted,volume,3,1).forEachIndexed { index,value -> PacketCodec.putIntLE(payload,index*4,value) }
            val packet=PacketCodec.build(11,revision,0,payload,payload.size)
            handle.invoke(sender,DatagramPacket(packet,packet.size,InetAddress.getByName(ip),45678),packet)
        }
        try {
            sender.setTarget("127.0.0.1",45679)
            set(sender,"session",123); set(sender,"revision",10); set(sender,"dualFlags",3)
            sender.requestMicrophone(true,40)
            state(ip="127.0.0.2"); state(session=124); state(revision=9)
            assertTrue(seen.isEmpty()); assertNotNull(get(sender,"pendingControl"))
            state(ack=0) // Lost request: do not overwrite the phone's pending desired values.
            assertTrue(seen.isEmpty()); assertNotNull(get(sender,"pendingControl"))
            state()
            assertEquals(1,seen.size); assertTrue(seen.single().muted)
            assertEquals(40,seen.single().volume); assertTrue(seen.single().deviceReady)
            assertNull(get(sender,"pendingControl"))
            state(revision=10,muted=0,volume=100)
            assertEquals(1,seen.size)
            state(revision=12,muted=0,volume=65)
            assertEquals(65,seen.last().volume); assertFalse(seen.last().muted)
        } finally { sender.close() }
    }
}
