package com.blueaudiosink.sender.net

import android.content.Context
import android.net.wifi.WifiManager
import android.os.SystemClock
import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/**
 * 手机侧唯一 UDP 通道：一个 socket 绑定 45678，包办发现/控制/音频。
 *
 * - 收：PC 广播的 OFFER（type=6）、PC 发起的 PING（type=4）
 * - 回：PONG（type=5，payload 原样回填 PING 的 T1，header.timestampUs = 手机 T2）
 * - 发：HELLO / START / STOP / AUDIO（发往目标电脑的音频端口，源端口固定 45678）
 *
 * 线程约定（重要）：Android 禁止主线程做 socket 收发。
 * 因此控制包（HELLO/START/STOP）全部投递到内部单线程 controlExecutor 执行，
 * AUDIO 由采集发送线程调用；START 发出后才打开 AUDIO 闸门，保证顺序。
 */
class UdpSender(context: Context) {

    interface Listener {
        fun onOffer(device: DiscoveredDevice)
        fun onPing(targetHost: String, targetPort: Int, t1Us: Int, receivedAtUs: Int)
        fun onMicrophoneState(status: MicrophoneStatus) {}
        fun onError(message: String)
    }

    @Volatile
    var listener: Listener? = null

    private val appContext = context.applicationContext
    private val sendLock = Any()

    private val controlExecutor: ExecutorService =
        Executors.newSingleThreadExecutor { runnable ->
            Thread(runnable, "udp-control").apply { isDaemon = true }
        }

    @Volatile
    private var socket: DatagramSocket? = null

    @Volatile
    private var targetHost: String? = null

    @Volatile
    private var targetPort: Int = PacketCodec.DEFAULT_AUDIO_PORT

    @Volatile
    private var cachedAddress: InetAddress? = null

    @Volatile
    private var running: Boolean = false

    /** START 已发出后才允许 AUDIO。 */
    @Volatile
    private var audioEnabled: Boolean = false

    @Volatile
    private var closed: Boolean = false

    private var receiveThread: Thread? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    /** AUDIO 包序号，从 0 开始，START 时归零。 */
    private var seq: Int = 0
    private var sendFailures = 0L
    private var dualFlags = 0
    private var session = 0
    private val streamSequence = IntArray(2)
    private var revision = 0
    private var request = 0
    private var pendingControl: ByteArray? = null
    private var startPending = false
    private var lastRetryMs = 0L
    private var lastStateMs = 0L
    private var negotiationAt = 0L
    private var negotiationWarned = false
    fun isCurrentSession(id: Int): Boolean = synchronized(sendLock) { session != 0 && session == id }
    fun isDualReady(): Boolean = synchronized(sendLock) { session!=0 && !startPending && SystemClock.elapsedRealtime()-lastStateMs<3000 }

    fun requestMicrophone(muted: Boolean, volume: Int, mask: Int = 3): Boolean = synchronized(sendLock) {
        if (mask !in 1..3 || closed || session == 0 || revision == 0 || dualFlags == 0) return@synchronized false
        request++
        val payload = ByteArray(16)
        PacketCodec.putIntLE(payload, 0, session)
        PacketCodec.putIntLE(payload, 4, revision)
        PacketCodec.putIntLE(payload, 8, if (muted) 1 else 0)
        PacketCodec.putIntLE(payload, 12, volume.coerceIn(0, 100))
        pendingControl = PacketCodec.build(12, request, 0, payload, payload.size).also { it[5] = mask.toByte() }
        lastRetryMs = 0
        val packet = pendingControl
        try {
            controlExecutor.execute {
                synchronized(sendLock) {
                    if (!closed && pendingControl === packet && packet != null) {
                        sendRawLocked(packet); lastRetryMs = SystemClock.elapsedRealtime()
                    }
                }
            }
        } catch (_: java.util.concurrent.RejectedExecutionException) { return@synchronized false }
        true
    }

    private fun retryPending() {
        synchronized(sendLock) {
            if (closed || dualFlags == 0 || session == 0) return
            val now = SystemClock.elapsedRealtime()
            if (now - lastRetryMs < 500) return
            lastRetryMs = now
            // Re-establish a PC session after restart; never send microphone PCM unacknowledged.
            if (startPending && dualFlags == 1 && now-negotiationAt>3000) {
                session=0; dualFlags=0; startPending=false
                sendControlLocked(PacketCodec.TYPE_START); audioEnabled=true
                return
            }
            if(startPending && dualFlags != 1 && now-negotiationAt>3000 && !negotiationWarned) {
                negotiationWarned=true
                listener?.onError("电脑未确认双路连接，请检查 Wi-Fi、端口及 Windows 接收端版本")
            }
            if (startPending || now - lastStateMs > 3000) {
                startPending = true; audioEnabled = false
                val payload = ByteArray(4); PacketCodec.putIntLE(payload, 0, dualFlags)
                sendRawLocked(PacketCodec.build(10, session, 0, payload, 4))
            } else pendingControl?.let { sendRawLocked(it) }
        }
    }
    private fun sendRawLocked(data: ByteArray) {
        val address = resolveTarget() ?: return
        try { socket?.send(DatagramPacket(data, data.size, address, targetPort)) }
        catch (ex: Exception) { listener?.onError("控制指令发送失败：" + ex.message) }
    }

    /** 复用缓冲：960 payload + 16 包头，避免每包分配。 */
    private val frameBuffer = ByteArray(PacketCodec.AUDIO_PACKET_SIZE)

    /** 绑定 45678 并开始收包。重复调用安全。 */
    fun open(): Boolean {
        if (socket != null) return true
        val sock = try {
            DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                soTimeout = 500
                bind(InetSocketAddress(PacketCodec.CONTROL_PORT))
            }
        } catch (e: Exception) {
            Log.w(TAG, "bind 45678 failed", e)
            listener?.onError("端口 45678 被占用")
            return false
        }
        socket = sock
        running = true
        acquireMulticastLock()
        receiveThread = Thread({ receiveLoop() }, "udp-recv").apply {
            isDaemon = true
            start()
        }
        return true
    }

    fun isOpen(): Boolean = socket != null

    /** 关闭 socket；先让已排队的 STOP 发完（同一 executor 保证顺序）。 */
    fun close() {
        if (closed) return
        closed = true
        running = false
        audioEnabled = false
        controlExecutor.execute {
            synchronized(sendLock) {
                try {
                    socket?.close()
                } catch (_: Exception) {
                }
                socket = null
            }
            releaseMulticastLock()
        }
        controlExecutor.shutdown()
    }

    /** 设定目标电脑（来自发现列表或手动输入）。不做 DNS，解析推迟到发送线程。 */
    fun setTarget(host: String, port: Int): Boolean = synchronized(sendLock) {
        val trimmed = host.trim()
        if (trimmed.isEmpty()) return@synchronized false
        targetHost = trimmed
        targetPort = if (port in 1..65_535) port else PacketCodec.DEFAULT_AUDIO_PORT
        cachedAddress = null
        true
    }

    fun clearTarget() = synchronized(sendLock) {
        targetHost = null
        targetPort = PacketCodec.DEFAULT_AUDIO_PORT
        cachedAddress = null
    }

    fun hasTarget(): Boolean = targetHost != null

    /** START：重置 seq 基线为 0，不重传；发送完成后才开启 AUDIO。 */
    fun sendStart(flags: Int = 0, onSent: (() -> Unit)? = null) {
        controlExecutor.execute {
            synchronized(sendLock) {
                seq = 0
                dualFlags = flags
                streamSequence.fill(0); request = 0; revision = 0; pendingControl = null
                if (flags != 0) {
                    do { session = java.security.SecureRandom().nextInt() } while (session == 0)
                    startPending = true; lastRetryMs = 0; lastStateMs = 0; negotiationAt = SystemClock.elapsedRealtime(); negotiationWarned = false
                } else { session = 0; sendControlLocked(PacketCodec.TYPE_START) }
            }
            audioEnabled = flags == 0
            if (flags != 0) retryPending()
            onSent?.invoke()
        }
    }

    fun sendStop() {
        audioEnabled = false
        controlExecutor.execute {
            synchronized(sendLock) {
                if (session != 0) sendRawLocked(PacketCodec.build(PacketCodec.TYPE_STOP, session, 0, null))
                else sendControlLocked(PacketCodec.TYPE_STOP)
                session = 0; dualFlags = 0; pendingControl = null; startPending = false
            }
        }
    }

    fun sendHello(deviceName: String) {
        val bytes = deviceName.toByteArray(Charsets.UTF_8)
        controlExecutor.execute {
            synchronized(sendLock) {
                sendLocked(PacketCodec.TYPE_HELLO, 0, bytes, bytes.size)
            }
        }
    }

    /** 发送一个 AUDIO 包（自动递增 seq）。payload 必须恰好 960 字节。 */
    fun sendAudio(payload: ByteArray, length: Int, timestampUs: Int, discontinuity: Boolean, stream: Int = 0) {
        if (!audioEnabled) return
        synchronized(sendLock) {
            if (!audioEnabled) return
            val sock = socket ?: return
            val address = resolveTarget() ?: return
            if (dualFlags != 0) {
                if (stream !in 0..1 || dualFlags and (1 shl stream) == 0) return
                val packet = DualStreamPacketCodec.build(stream, session, streamSequence[stream]++, timestampUs, payload, discontinuity)
                try { sock.send(DatagramPacket(packet, packet.size, address, targetPort)) }
                catch (ex: Exception) { listener?.onError("音频发送失败：" + ex.message) }
                return
            }
            if (stream != 0) return
            val flags = if (discontinuity) PacketCodec.FLAG_DISCONTINUITY else 0
            val packetSeq = seq++ // Consume source time even if send fails.
            PacketCodec.writeHeader(frameBuffer, PacketCodec.TYPE_AUDIO, flags, packetSeq, timestampUs)
            System.arraycopy(payload, 0, frameBuffer, PacketCodec.HEADER_SIZE, length)
            try {
                sock.send(DatagramPacket(frameBuffer, PacketCodec.HEADER_SIZE + length, address, targetPort))
            } catch (e: Exception) {
                sendFailures++
                Log.w(TAG, "send audio failed count=$sendFailures", e)
                listener?.onError("发送失败：" + (e.message ?: e.javaClass.simpleName))
                return
            }
        }
    }

    private fun resolveTarget(): InetAddress? {
        val cached = cachedAddress
        if (cached != null) return cached
        val host = targetHost ?: return null
        return try {
            InetAddress.getByName(host).also { cachedAddress = it }
        } catch (e: Exception) {
            Log.w(TAG, "resolve target failed: " + host, e)
            listener?.onError("无法解析地址 " + host + "：" + (e.message ?: e.javaClass.simpleName))
            null
        }
    }

    private fun receiveLoop() {
        val buffer = ByteArray(2048)
        while (running) {
            retryPending()
            val sock = socket ?: break
            val packet = DatagramPacket(buffer, buffer.size)
            try {
                sock.receive(packet)
            } catch (_: SocketTimeoutException) {
                continue
            } catch (e: Exception) {
                if (running) Log.w(TAG, "receive failed", e)
                break
            }
            val length = packet.length
            if (!PacketCodec.isValid(buffer, length)) continue
            when (PacketCodec.typeOf(buffer, length)) {
                PacketCodec.TYPE_OFFER -> handleOffer(packet, buffer)
                PacketCodec.TYPE_PING -> handlePing(packet, buffer)
                11 -> handleMicrophoneState(packet, buffer)
                else -> Unit
            }
        }
    }

    private fun handleOffer(packet: DatagramPacket, buffer: ByteArray) {
        val length = packet.length
        val nameStart = PacketCodec.HEADER_SIZE
        val zero = PacketCodec.findZero(buffer, nameStart, length)
        val portOffset = if (zero >= 0) zero + 1 else -1
        val port = if (portOffset > 0 && portOffset + 4 <= length) {
            PacketCodec.getIntLE(buffer, portOffset)
        } else {
            PacketCodec.DEFAULT_AUDIO_PORT
        }
        val nameEnd = if (zero >= 0) zero else length
        val name = String(buffer, nameStart, nameEnd - nameStart, Charsets.UTF_8).trim()
        val ip = packet.address?.hostAddress ?: return
        val device = DiscoveredDevice(
            name = name.ifEmpty { ip },
            ip = ip,
            port = if (port in 1..65_535) port else PacketCodec.DEFAULT_AUDIO_PORT,
            lastSeenMs = SystemClock.elapsedRealtime(),
        )
        listener?.onOffer(device)
    }

    /** PC 发起的探测：payload 空，header.timestampUs = T1（PC 发送时刻）。 */
    private fun handlePing(packet: DatagramPacket, buffer: ByteArray) {
        // PING is sent from the PC control port, not its audio destination port.
        // Snapshot the validated destination; the controller also checks the
        // snapshot in case the user switches targets before the callback runs.
        val target = synchronized(sendLock) {
            val host = targetHost ?: return
            if (packet.port != PacketCodec.CONTROL_PORT || packet.address != resolveTarget()) return
            Pair(host, targetPort)
        }
        val t1 = PacketCodec.timestampOf(buffer, packet.length)
        val receiveUs = (SystemClock.elapsedRealtimeNanos() / 1000L).toInt()
        val pong = PacketCodec.build(
            type = PacketCodec.TYPE_PONG,
            seq = 0,
            timestampUs = receiveUs,
            payload = intToBytes(t1),
            payloadLength = 4,
        )
        try {
            socket?.send(DatagramPacket(pong, pong.size, packet.address, packet.port))
        } catch (e: Exception) {
            Log.w(TAG, "send PONG failed", e)
            listener?.onError("回送 PONG 失败：" + (e.message ?: e.javaClass.simpleName))
        }
        listener?.onPing(target.first, target.second, t1, receiveUs)
    }

    private fun handleMicrophoneState(packet: DatagramPacket, buffer: ByteArray) {
        val status = synchronized(sendLock) {
            if (packet.length != 40 || session == 0 || packet.port != PacketCodec.CONTROL_PORT || packet.address != resolveTarget()) return
            if (PacketCodec.getIntLE(buffer, 16) != session) return
            val incomingRevision = PacketCodec.getIntLE(buffer, 8)
            if (revision != 0 && incomingRevision - revision < 0) return
            val ack = PacketCodec.getIntLE(buffer, 20)
            val muted = PacketCodec.getIntLE(buffer, 24)
            val volume = PacketCodec.getIntLE(buffer, 28)
            if (muted !in 0..1 || volume !in 0..100) return
            revision = incomingRevision; lastStateMs = SystemClock.elapsedRealtime()
            startPending = false; audioEnabled = true
            // While waiting for a request ACK, keep the phone's desired control values.
            if (pendingControl != null && ack - request < 0) return
            val previousRequest = pendingControl
            val overridden = previousRequest != null &&
                ((previousRequest[5].toInt() and 1 != 0 && PacketCodec.getIntLE(previousRequest, 24) != muted) ||
                 (previousRequest[5].toInt() and 2 != 0 && PacketCodec.getIntLE(previousRequest, 28) != volume))
            pendingControl = null
            MicrophoneStatus(targetHost ?: return, targetPort, muted != 0, volume,
                PacketCodec.getIntLE(buffer, 32) and 2 != 0, PacketCodec.getIntLE(buffer, 36) != 0, overridden, session)
        }
        listener?.onMicrophoneState(status)
    }

    private fun intToBytes(value: Int): ByteArray {
        val out = ByteArray(4)
        PacketCodec.putIntLE(out, 0, value)
        return out
    }

    private fun sendControlLocked(type: Int) {
        sendLocked(type, (SystemClock.elapsedRealtimeNanos() / 1000L).toInt(), null, 0)
    }

    private fun sendLocked(type: Int, timestampUs: Int, payload: ByteArray?, payloadLength: Int) {
        val sock = socket ?: return
        val address = resolveTarget() ?: return
        val packetBytes = PacketCodec.build(type, 0, timestampUs, payload, payloadLength)
        try {
            sock.send(DatagramPacket(packetBytes, packetBytes.size, address, targetPort))
        } catch (e: Exception) {
            Log.w(TAG, "send control " + type + " failed", e)
            listener?.onError("发送失败：" + (e.message ?: e.javaClass.simpleName))
        }
    }

    private fun acquireMulticastLock() {
        try {
            val wifi = appContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return
            multicastLock = wifi.createMulticastLock("blueaudiosink-udp").apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (_: Exception) {
            // 没有 Wi-Fi 时忽略，仍尝试直接收广播
        }
    }

    private fun releaseMulticastLock() {
        try {
            multicastLock?.release()
        } catch (_: Exception) {
        }
        multicastLock = null
    }

    private companion object {
        const val TAG = "BlueAudioSink"
    }
}
