package com.blueaudiosink.sender.capture

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.media.projection.MediaProjectionManager
import android.os.Build
import androidx.core.content.ContextCompat

/**
 * MediaProjection 授权辅助。
 *
 * Android 的硬性限制：录屏/内录授权每次开始都必须由用户在系统对话框点确认，
 * 且一次授权只能启动一次采集会话，不能缓存复用。
 */
object MediaProjectionHelper {

    private val runtimePermissions: Array<String> =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            arrayOf(Manifest.permission.RECORD_AUDIO, Manifest.permission.POST_NOTIFICATIONS)
        } else {
            arrayOf(Manifest.permission.RECORD_AUDIO)
        }

    fun missingPermissions(context: Context): List<String> =
        runtimePermissions.filter {
            ContextCompat.checkSelfPermission(context, it) != PackageManager.PERMISSION_GRANTED
        }

    /** 弹出系统录屏授权对话框的 Intent（结果需交给前台服务使用）。 */
    fun createConsentIntent(context: Context): Intent? {
        val manager = context.getSystemService(Context.MEDIA_PROJECTION_SERVICE) as? MediaProjectionManager
        return manager?.createScreenCaptureIntent()
    }
}
