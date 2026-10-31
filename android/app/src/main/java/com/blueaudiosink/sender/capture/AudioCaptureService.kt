package com.blueaudiosink.sender.capture

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.blueaudiosink.sender.MainActivity
import com.blueaudiosink.sender.R
import com.blueaudiosink.sender.core.LinkController

/**
 * 采集前台服务。
 *
 * Android 14 起 mediaProjection 类型的授权只能由“已进入前台”的服务消费：
 * 因此顺序必须是 startForeground(...) → getMediaProjection(...) → AudioRecord。
 * 仅采集媒体副本时需要每次重新确认系统授权；纯麦克风不使用 MediaProjection。
 */
class AudioCaptureService : Service() {

    companion object {
        private const val CHANNEL_ID = "capture_status"
        private const val NOTIFICATION_ID = 1001

        const val ACTION_START = "com.blueaudiosink.sender.action.START"
        const val ACTION_STOP = "com.blueaudiosink.sender.action.STOP"

        private const val EXTRA_RESULT_CODE = "extra_result_code"
        private const val EXTRA_RESULT_DATA = "extra_result_data"
        private const val EXTRA_TIER = "extra_tier"
        private const val EXTRA_DEVICE_NAME = "extra_device_name"

        fun startIntent(
            context: Context,
            resultCode: Int,
            resultData: Intent?,
            tierName: String,
            deviceName: String,
            mediaEnabled: Boolean = true,
            microphoneEnabled: Boolean = false,
        ): Intent = Intent(context, AudioCaptureService::class.java).apply {
            action = ACTION_START
            putExtra("media_enabled", mediaEnabled)
            putExtra("microphone_enabled", microphoneEnabled)
            putExtra(EXTRA_RESULT_CODE, resultCode)
            putExtra(EXTRA_RESULT_DATA, resultData)
            putExtra(EXTRA_TIER, tierName)
            putExtra(EXTRA_DEVICE_NAME, deviceName)
        }

        fun stopIntent(context: Context): Intent =
            Intent(context, AudioCaptureService::class.java).apply { action = ACTION_STOP }
    }

    private var projection: MediaProjection? = null
    private var recorder: PcmRecorder? = null
    private var microphoneRecorder: PcmRecorder? = null
    @Volatile private var captureGeneration = 0
    private var projectionCallback: MediaProjection.Callback? = null

    private val mainHandler = Handler(Looper.getMainLooper())

    private val TAG = "BlueAudioSink"

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        LinkController.attach(applicationContext)
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                LinkController.stopStreaming()
                releaseProjection()
                val media = intent.getBooleanExtra("media_enabled", true)
                val mic = intent.getBooleanExtra("microphone_enabled", false)
                if (!media && !mic) { failAndStop("请至少选择一路音频"); return START_NOT_STICKY }
                try { startForegroundCompat(getString(R.string.notification_capturing), media, mic) }
                catch (ex: Exception) { failAndStop("无法启动采集服务：" + ex.message); return START_NOT_STICKY }
                val resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0)
                val resultData: Intent? = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    intent.getParcelableExtra(EXTRA_RESULT_DATA, Intent::class.java)
                } else {
                    @Suppress("DEPRECATION")
                    intent.getParcelableExtra(EXTRA_RESULT_DATA)
                }
                val tier = BufferTier.fromName(intent.getStringExtra(EXTRA_TIER))
                val deviceName = intent.getStringExtra(EXTRA_DEVICE_NAME)
                    ?: getString(R.string.default_device_name)
                if (media && resultData == null) {
                    failAndStop(getString(R.string.error_no_projection))
                } else {
                    // 只清理上一次的采集会话，绝不能在这里 stopForeground：
                    // 否则会丢掉 mediaProjection 前台类型，导致 getMediaProjection 抛 SecurityException
                    releaseProjection()
                    startCapture(resultCode, resultData, tier, deviceName, media, mic)
                }
            }

            ACTION_STOP -> {
                stopCapture()
                stopSelf()
            }

            else -> stopSelf()
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        LinkController.stopStreaming()
        releaseCapture()
        super.onDestroy()
    }

    // ---------------------------------------------------------------- 采集

    private fun startCapture(resultCode: Int, resultData: Intent?, tier: BufferTier, deviceName: String,
                             media: Boolean, mic: Boolean) {
        val generation = captureGeneration
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            failAndStop(getString(R.string.error_record_permission)); return
        }
        if (media) {
            val mp = try { getSystemService(MediaProjectionManager::class.java)?.getMediaProjection(resultCode, requireNotNull(resultData)) }
            catch (ex: Exception) { null }
            if (mp == null) { failAndStop(getString(R.string.error_projection_denied)); return }
            val callback = object : MediaProjection.Callback() {
                override fun onStop() {
                    if (generation != captureGeneration) return
                    stopCapture(); LinkController.postMessage(getString(R.string.error_projection_stopped)); stopSelf()
                }
            }
            projection = mp; projectionCallback = callback
            mp.registerCallback(callback, mainHandler)
            recorder = PcmRecorder(mp, tier = tier,
                onPacket = { b, n, ts, gap -> if (generation == captureGeneration) LinkController.sendAudio(b,n,ts,gap,0) },
                onError = { code -> mainHandler.post { if (generation == captureGeneration) handleRecorderError(code) } })
            if (recorder?.start() != true) { failAndStop("媒体音频采集启动失败"); return }
        }
        if (mic) {
            microphoneRecorder = PcmRecorder(null, microphone = true, tier = tier,
                onPacket = { b, n, ts, gap -> if (generation == captureGeneration) LinkController.sendAudio(b,n,ts,gap,1) },
                onError = { code -> mainHandler.post { if (generation == captureGeneration) handleRecorderError(code) } })
            if (microphoneRecorder?.start() != true) { failAndStop("麦克风采集启动失败，请检查录音权限及占用情况"); return }
        }
        if (!LinkController.startStreaming(deviceName)) { releaseCapture(); stopSelf(); return }
        updateNotification(if (mic && media) "正在发送媒体声音与麦克风" else if (mic) "正在发送麦克风" else getString(R.string.notification_streaming))
    }

    private fun handleRecorderError(code: Int) {
        val text = when (code) {
            PcmRecorder.ERROR_PERMISSION -> getString(R.string.error_record_permission)
            PcmRecorder.ERROR_BUILD -> getString(R.string.error_recorder_build)
            PcmRecorder.ERROR_START -> getString(R.string.error_recorder_start)
            else -> getString(R.string.error_recorder_read)
        }
        failAndStop(text)
    }

    private fun failAndStop(message: String) {
        Log.w(TAG, "capture failed: " + message)
        LinkController.stopStreaming()
        LinkController.fail(message)
        releaseCapture()
        stopSelf()
    }

    private fun stopCapture() {
        LinkController.stopStreaming()
        releaseCapture()
    }

    /** 只释放采集对象，保留前台服务状态（启动新会话时使用）。 */
    private fun releaseProjection() {
        captureGeneration++
        microphoneRecorder?.stop(); microphoneRecorder = null
        recorder?.stop()
        recorder = null
        projection?.let {
            try {
                projectionCallback?.let { callback -> it.unregisterCallback(callback) }
                it.stop()
            } catch (_: Exception) {
            }
        }
        projection = null
        projectionCallback = null
    }

    /** 释放采集对象并退出前台状态（停止/失败/销毁时使用）。 */
    private fun releaseCapture() {
        releaseProjection()
        try {
            stopForeground(STOP_FOREGROUND_REMOVE)
        } catch (_: Exception) {
        }
        val manager = getSystemService(NotificationManager::class.java)
        manager?.cancel(NOTIFICATION_ID)
    }

    // ---------------------------------------------------------------- 通知

    private fun createNotificationChannel() {
        val manager = getSystemService(NotificationManager::class.java) ?: return
        val channel = NotificationChannel(
            CHANNEL_ID,
            getString(R.string.notification_channel_name),
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = getString(R.string.notification_channel_desc)
            setShowBadge(false)
        }
        manager.createNotificationChannel(channel)
    }

    private fun startForegroundCompat(text: String, media: Boolean, mic: Boolean) {
        startForeground(
            NOTIFICATION_ID,
            buildNotification(text),
            (if (media) ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION else 0) or
                (if (mic && Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE else 0),
        )
    }

    private fun updateNotification(text: String) {
        val manager = getSystemService(NotificationManager::class.java) ?: return
        manager.notify(NOTIFICATION_ID, buildNotification(text))
    }

    private fun buildNotification(text: String): Notification {
        val openApp = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val stopAction = PendingIntent.getService(
            this,
            1,
            stopIntent(this),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_audio)
            .setContentTitle(getString(R.string.notification_title))
            .setContentText(text)
            .setOngoing(true)
            .setSilent(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setContentIntent(openApp)
            .addAction(0, getString(R.string.action_stop), stopAction)
            .build()
    }
}
