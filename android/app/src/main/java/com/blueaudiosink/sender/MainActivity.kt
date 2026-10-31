package com.blueaudiosink.sender

import android.app.Activity
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.core.content.ContextCompat
import com.blueaudiosink.sender.capture.AudioCaptureService
import com.blueaudiosink.sender.capture.MediaProjectionHelper
import com.blueaudiosink.sender.core.LinkController
import com.blueaudiosink.sender.ui.BlueAudioSinkTheme
import com.blueaudiosink.sender.ui.SenderScreen

/**
 * 唯一 Activity。
 *
 * 开始发送先申请录音/通知权限。只传麦克风时直接启动 microphone 前台服务；
 * 只有主动采集媒体副本时才申请 MediaProjection，并启动对应前台服务类型。
 */
class MainActivity : ComponentActivity() {

    private var pendingStart = false

    private val permissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
            val granted = result.values.all { it }
            if (!pendingStart) return@registerForActivityResult
            if (granted) {
                requestConsent()
            } else {
                pendingStart = false
                LinkController.postMessage(getString(R.string.error_permissions_required))
            }
        }

    private val bluetoothPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (granted) LinkController.setBluetooth(true)
            else LinkController.postMessage("需要附近设备权限才能使用蓝牙麦克风")
        }
    private fun selectTransport(bluetooth: Boolean) {
        if (bluetooth && Build.VERSION.SDK_INT >= 31 &&
            ContextCompat.checkSelfPermission(this,android.Manifest.permission.BLUETOOTH_CONNECT) != android.content.pm.PackageManager.PERMISSION_GRANTED)
            bluetoothPermissionLauncher.launch(android.Manifest.permission.BLUETOOTH_CONNECT)
        else LinkController.setBluetooth(bluetooth)
    }

    private val consentLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            pendingStart = false
            val data = result.data
            if (result.resultCode == Activity.RESULT_OK && data != null) {
                val state = LinkController.state.value
                val intent = AudioCaptureService.startIntent(
                    context = this,
                    resultCode = result.resultCode,
                    resultData = data,
                    tierName = state.tier.name,
                    deviceName = deviceName(),
                    mediaEnabled = state.captureMedia,
                    microphoneEnabled = state.microphoneEnabled,
                )
                ContextCompat.startForegroundService(this, intent)
            } else {
                LinkController.postMessage(getString(R.string.error_consent_cancelled))
            }
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        LinkController.attach(this)
        LinkController.setDeviceName(deviceName())
        setContent {
            BlueAudioSinkTheme {
                val state by LinkController.state.collectAsState()
                SenderScreen(
                    state = state,
                    onStart = ::handleStart,
                    onTransportChange = ::selectTransport,
                    onBluetoothSettings = { startActivity(android.content.Intent(android.provider.Settings.ACTION_BLUETOOTH_SETTINGS)) },
                    onStop = {
                        ContextCompat.startForegroundService(
                            this,
                            AudioCaptureService.stopIntent(this),
                        )
                    },
                    onConnect = { ip, port -> LinkController.connect(ip, port) },
                    onConnectManual = { ip -> LinkController.connect(ip) },
                    onRemoveSaved = { ip -> LinkController.removeSavedDevice(ip) },
                    onTierChange = { tier -> LinkController.setTier(tier) },
                    onDismissMessage = { LinkController.clearMessage() },
                    onRefreshDiscovery = { LinkController.restartDiscovery(this) },
                )
            }
        }
    }

    override fun onStart() {
        super.onStart()
        // 绑定 45678 属于网络操作，不能在主线程做
        val context = applicationContext
        Thread { LinkController.startDiscovery(context) }.start()
    }

    override fun onStop() {
        super.onStop()
        LinkController.stopDiscovery()
    }

    private fun handleStart() {
        if (LinkController.state.value.targetIp.isNullOrBlank()) {
            LinkController.postMessage(getString(R.string.error_no_target))
            return
        }
        val missing = MediaProjectionHelper.missingPermissions(this)
        if (missing.isNotEmpty()) {
            pendingStart = true
            permissionLauncher.launch(missing.toTypedArray())
            return
        }
        requestConsent()
    }

    private fun requestConsent() {
        val state = LinkController.state.value
        if (!state.captureMedia) {
            pendingStart = false
            if (!state.microphoneEnabled) { LinkController.postMessage("请至少选择一路音频"); return }
            ContextCompat.startForegroundService(this, AudioCaptureService.startIntent(
                this, 0, null, state.tier.name, deviceName(), false, true))
            return
        }
        val intent = MediaProjectionHelper.createConsentIntent(this)
        if (intent == null) {
            pendingStart = false
            LinkController.postMessage(getString(R.string.error_projection_unavailable))
            return
        }
        consentLauncher.launch(intent)
    }

    private fun deviceName(): String =
        Build.MODEL?.takeIf { it.isNotBlank() } ?: getString(R.string.default_device_name)
}
