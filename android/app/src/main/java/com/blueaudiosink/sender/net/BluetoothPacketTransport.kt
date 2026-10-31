package com.blueaudiosink.sender.net

import android.annotation.SuppressLint
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.Context
import java.io.DataInputStream
import java.io.IOException
import java.util.UUID
import java.util.concurrent.LinkedBlockingDeque
import java.util.concurrent.TimeUnit

/** Paired secure RFCOMM. Never opens a network socket. Control packets take priority. */
class BluetoothPacketTransport(private val context: Context, private val address: String) : PacketTransport {
    private val control = LinkedBlockingDeque<ByteArray>(32)
    private val audio = LinkedBlockingDeque<ByteArray>(24)
    private val encoder = BluetoothAudioCodec.Encoder()
    @Volatile private var active = false
    @Volatile private var socket: BluetoothSocket? = null
    private var writer: Thread? = null
    @SuppressLint("MissingPermission")
    override fun open(onPacket: (ByteArray) -> Unit, onFailure: (String) -> Unit): Boolean {
        if (active) return true
        active = true
        Thread({
            try {
                val adapter = context.getSystemService(BluetoothManager::class.java)?.adapter
                    ?: throw IOException("设备不支持蓝牙")
                if (!adapter.isEnabled) throw IOException("请先开启蓝牙")
                val peer = adapter.getRemoteDevice(address)
                val channel = peer.createRfcommSocketToServiceRecord(SERVICE_ID)
                synchronized(this) {
                    if (!active) { channel.close(); return@Thread }
                    socket = channel
                }
                // Bluetooth connect may block indefinitely on some stacks; close aborts it.
                val timeout = Thread({
                    try { Thread.sleep(15000); if (active && !channel.isConnected) channel.close() }
                    catch (_: InterruptedException) { }
                }, "bluetooth-connect-timeout").apply { isDaemon = true; start() }
                try { channel.connect() } finally { timeout.interrupt() }
                val output = channel.outputStream
                writer = Thread({
                    try {
                        while (active) {
                            val frame = control.pollFirst() ?: audio.pollFirst(10, TimeUnit.MILLISECONDS) ?: continue
                            output.write(frame)
                        }
                    } catch (e: Exception) { fail(e, onFailure) }
                }, "bluetooth-send").apply { isDaemon = true; start() }
                val input = DataInputStream(channel.inputStream)
                while (active) {
                    val n = input.readUnsignedByte() or (input.readUnsignedByte() shl 8)
                    val codec = input.readUnsignedByte(); val reserved = input.readUnsignedByte()
                    if (n !in 16..2048 || codec != 0 || reserved != 0) throw IOException("蓝牙协议版本不兼容")
                    val bytes = ByteArray(n); input.readFully(bytes)
                    if (!PacketCodec.isValid(bytes, bytes.size)) throw IOException("蓝牙控制帧无效")
                    onPacket(bytes)
                }
            } catch (e: Exception) { fail(e, onFailure) }
        }, "bluetooth-connect").apply { isDaemon = true; start() }
        return true
    }
    private fun fail(e: Exception, callback: (String) -> Unit) {
        val report: Boolean
        synchronized(this) { report = active; active = false; try { socket?.close() } catch (_: Exception) { } }
        if (report) callback("蓝牙连接断开：${e.message ?: e.javaClass.simpleName}；请检查配对及电脑蓝牙模式后重新连接")
    }
    override fun send(packet: ByteArray, audio: Boolean) {
        if (!active) return
        val bytes = if (audio) encoder.encode(packet) else packet
        val frame = ByteArray(bytes.size+4)
        frame[0] = bytes.size.toByte(); frame[1] = (bytes.size shr 8).toByte(); frame[2] = if (audio) 1 else 0
        bytes.copyInto(frame,4)
        if (audio) {
            if (!this.audio.offerLast(frame)) { this.audio.pollFirst(); this.audio.offerLast(frame) }
        } else if (!control.offerLast(frame)) {
            // Replace an older pending control; session requests are idempotent/retried.
            control.pollFirst(); control.offerLast(frame)
        }
    }
    override fun close() {
        synchronized(this) { active = false; try { socket?.close() } catch (_: Exception) { }; socket = null }
        writer?.interrupt(); control.clear(); audio.clear()
    }
    companion object { val SERVICE_ID: UUID = UUID.fromString("c774d750-0c0b-4cd9-a4af-a060d0b14925") }
}
