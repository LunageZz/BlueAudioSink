package com.blueaudiosink.sender.net

import android.content.ContextWrapper
import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.CountDownLatch

class BluetoothSenderTest {
    private class Transport : PacketTransport {
        lateinit var incoming: (ByteArray) -> Unit
        val sent=LinkedBlockingQueue<Pair<ByteArray,Boolean>>()
        var closed=false
        override fun open(onPacket: (ByteArray)->Unit,onFailure:(String)->Unit):Boolean { incoming=onPacket; return true }
        override fun send(packet:ByteArray,audio:Boolean) { sent.offer(packet.copyOf() to audio) }
        override fun close() { closed=true }
    }
    private fun field(sender:UdpSender,name:String)=UdpSender::class.java.getDeclaredField(name).apply { isAccessible=true }
    @Test fun bluetoothMediaOnlyNeverFallsBackToUnframedLegacyAudio() {
        val transport=Transport(); val sender=UdpSender(ContextWrapper(null),transport)
        try {
            sender.setTarget("01:23:45:67:89:AB",45679)
            field(sender,"session").setInt(sender,123)
            field(sender,"dualFlags").setInt(sender,1)
            field(sender,"startPending").setBoolean(sender,true)
            field(sender,"negotiationAt").setLong(sender,-5000)
            field(sender,"lastRetryMs").setLong(sender,-1000)
            UdpSender::class.java.getDeclaredMethod("retryPending").apply { isAccessible=true }.invoke(sender)
            assertEquals(10,transport.sent.poll(2,TimeUnit.SECONDS)!!.first[4].toInt())
            assertEquals(123,field(sender,"session").getInt(sender))
            assertFalse(field(sender,"audioEnabled").getBoolean(sender))
        } finally { sender.close() }
    }
    @Test fun bluetoothNegotiatesControlsAndBothAudioStreamsWithoutUdp() {
        val transport=Transport(); val sender=UdpSender(ContextWrapper(null),transport)
        val states=mutableListOf<MicrophoneStatus>()
        sender.listener=object:UdpSender.Listener {
            override fun onOffer(device:DiscoveredDevice) { fail("Bluetooth must not discover Wi-Fi devices") }
            override fun onPing(targetHost:String,targetPort:Int,t1Us:Int,receivedAtUs:Int) {}
            override fun onError(message:String) { fail(message) }
            override fun onMicrophoneState(status:MicrophoneStatus) { states+=status }
        }
        try {
            assertTrue(sender.setTarget("01:23:45:67:89:AB",45679)); assertTrue(sender.open())
            val started=CountDownLatch(1); sender.sendStart(3) { started.countDown() }
            assertTrue(started.await(2,TimeUnit.SECONDS))
            assertNull(field(sender,"socket").get(sender))
            val session=field(sender,"session").getInt(sender)
            field(sender,"lastRetryMs").setLong(sender,-1000)
            UdpSender::class.java.getDeclaredMethod("retryPending").apply { isAccessible=true }.invoke(sender)
            val start=transport.sent.poll(2,TimeUnit.SECONDS)!!.first
            assertEquals(10,start[4].toInt()); assertEquals(session,PacketCodec.getIntLE(start,8))
            fun state(revision:Int,ack:Int=0,muted:Int=0,volume:Int=100,share:Int=0) {
                val payload=ByteArray(24)
                listOf(session,ack,muted,volume,3,share).forEachIndexed { i,v -> PacketCodec.putIntLE(payload,i*4,v) }
                transport.incoming(PacketCodec.build(11,revision,0,payload,24))
            }
            val pcm=ByteArray(960)
            sender.sendAudio(pcm,960,0,false,1)
            assertFalse(transport.sent.any { it.second }) // Wait for PC ACK.
            state(1)
            assertEquals("01:23:45:67:89:AB",states.last().host)
            transport.sent.clear()
            sender.sendAudio(pcm,960,0,false,0)
            assertFalse(transport.sent.any { it.second }) // Native A2DP handles headphones.
            sender.sendAudio(pcm,960,0,false,1)
            assertEquals(1,transport.sent.poll(2,TimeUnit.SECONDS)!!.first[4].toInt())
            state(2,share=1)
            sender.sendAudio(pcm,960,5000,false,0)
            assertEquals(0,transport.sent.poll(2,TimeUnit.SECONDS)!!.first[4].toInt())
            assertTrue(sender.requestMicrophone(true,35))
            val request=transport.sent.poll(2,TimeUnit.SECONDS)!!.first
            assertEquals(12,request[4].toInt())
            state(3,PacketCodec.getIntLE(request,8),1,35,1)
            assertTrue(states.last().muted); assertEquals(35,states.last().volume)
            assertNull(field(sender,"pendingControl").get(sender))
        } finally { sender.close() }
    }
}
