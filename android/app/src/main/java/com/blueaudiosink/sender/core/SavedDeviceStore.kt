package com.blueaudiosink.sender.core

import android.content.Context
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject

/** 一台「记住」的电脑：成功连上过一次就会被持久化。 */
data class SavedDevice(
    val name: String,
    val ip: String,
    val port: Int,
    /** 最近一次成功连接的本地时间（毫秒），用于倒序展示。 */
    val lastUsedMs: Long,
)

/**
 * 已连接设备的本地记忆。
 *
 * 存储：SharedPreferences（文件名 [PREFS_NAME]）里的单个字符串键 [KEY_SAVED_DEVICES]，
 * 内容是 JSON 数组，元素形如
 * `{"name":"DESKTOP-1","ip":"192.168.1.10","port":45679,"lastUsed":1730000000000}`。
 * 用 `org.json`（Android 框架自带）而不是 Gson，避免引入新依赖。
 *
 * 规则：
 * - 同 IP 视为同一台设备，重复连接只刷新名称/端口/时间；
 * - 按 lastUsed 倒序，最多保存 [MAX_DEVICES] 台，超出淘汰最旧；
 * - 读失败（数据损坏/格式演进）退回空列表，绝不让界面崩。
 */
class SavedDeviceStore(context: Context) {

    companion object {
        private const val TAG = "BlueAudioSink"
        private const val PREFS_NAME = "blueaudio_sender"
        private const val KEY_SAVED_DEVICES = "saved_devices"
        /** 最多记住的设备数。 */
        const val MAX_DEVICES = 5
    }

    private val lock = Any()

    private val prefs =
        context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    /** 读取已保存设备（最近使用在前）。 */
    fun load(): List<SavedDevice> = synchronized(lock) { readLocked() }

    /** 记住一台设备（成功连上后调用），返回更新后的完整列表。 */
    fun remember(name: String, ip: String, port: Int): List<SavedDevice> = synchronized(lock) {
        val host = ip.trim()
        if (host.isEmpty()) return@synchronized readLocked()
        val safePort = if (port in 1..65_535) port else 45_679
        val entry = SavedDevice(
            name = name.trim().ifBlank { host },
            ip = host,
            port = safePort,
            lastUsedMs = System.currentTimeMillis(),
        )
        val next = (listOf(entry) + readLocked().filterNot { it.ip == host })
            .sortedByDescending { it.lastUsedMs }
            .take(MAX_DEVICES)
        writeLocked(next)
        next
    }

    /** 删除一台设备，返回剩余列表。 */
    fun remove(ip: String): List<SavedDevice> = synchronized(lock) {
        val host = ip.trim()
        val next = readLocked().filterNot { it.ip == host }
        writeLocked(next)
        next
    }

    // ---------------------------------------------------------------- 内部

    private fun readLocked(): List<SavedDevice> {
        val raw = try {
            prefs.getString(KEY_SAVED_DEVICES, null)
        } catch (e: Exception) {
            Log.w(TAG, "read saved devices failed", e)
            null
        } ?: return emptyList()
        return try {
            val array = JSONArray(raw)
            val list = ArrayList<SavedDevice>(array.length())
            for (i in 0 until array.length()) {
                val obj = array.optJSONObject(i) ?: continue
                val ip = obj.optString("ip").trim()
                if (ip.isEmpty()) continue
                val port = obj.optInt("port", 45_679)
                list.add(
                    SavedDevice(
                        name = obj.optString("name").trim().ifBlank { ip },
                        ip = ip,
                        port = if (port in 1..65_535) port else 45_679,
                        lastUsedMs = obj.optLong("lastUsed", 0L),
                    ),
                )
            }
            list.sortedByDescending { it.lastUsedMs }.take(MAX_DEVICES)
        } catch (e: Exception) {
            Log.w(TAG, "saved devices corrupted, ignored", e)
            emptyList()
        }
    }

    private fun writeLocked(list: List<SavedDevice>) {
        val array = JSONArray()
        list.forEach { device ->
            array.put(
                JSONObject().apply {
                    put("name", device.name)
                    put("ip", device.ip)
                    put("port", device.port)
                    put("lastUsed", device.lastUsedMs)
                },
            )
        }
        try {
            prefs.edit().putString(KEY_SAVED_DEVICES, array.toString()).apply()
        } catch (e: Exception) {
            Log.w(TAG, "write saved devices failed", e)
        }
    }
}
