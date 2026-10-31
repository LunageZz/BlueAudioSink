package com.blueaudiosink.sender.core

import android.content.SharedPreferences
import com.blueaudiosink.sender.net.DiscoveredDevice
import org.junit.Assert.*
import org.junit.Test
import java.lang.reflect.Proxy

class BluetoothMemoryTest {
    // A SharedPreferences contract fake lets a new store instance read persisted values.
    private fun preferences(): SharedPreferences {
        val values = mutableMapOf<String, Any?>()
        val pending = mutableMapOf<String, Any?>()
        lateinit var editor: SharedPreferences.Editor
        editor = Proxy.newProxyInstance(javaClass.classLoader, arrayOf(SharedPreferences.Editor::class.java)) { _, method, args ->
            when (method.name) {
                "putString", "putBoolean" -> { pending[args[0] as String] = args[1]; editor }
                "apply" -> { values.putAll(pending); pending.clear(); null }
                else -> throw UnsupportedOperationException(method.name)
            }
        } as SharedPreferences.Editor
        return Proxy.newProxyInstance(javaClass.classLoader, arrayOf(SharedPreferences::class.java)) { _, method, args ->
            when (method.name) {
                "getString", "getBoolean" -> values[args[0]] ?: args[1]
                "edit" -> editor
                else -> throw UnsupportedOperationException(method.name)
            }
        } as SharedPreferences
    }

    @Test fun lastSuccessfulBluetoothTargetAndModeSurviveStoreRecreation() {
        val prefs = preferences()
        SavedDeviceStore(prefs).apply {
            rememberBluetoothAddress("aa:bb:cc:dd:ee:ff")
            saveBluetoothMode(true)
        }
        val reopened = SavedDeviceStore(prefs)
        assertEquals("AA:BB:CC:DD:EE:FF", reopened.loadLastBluetoothAddress())
        assertTrue(reopened.loadBluetoothMode())
        reopened.saveBluetoothMode(false)
        assertFalse(SavedDeviceStore(prefs).loadBluetoothMode())
        assertEquals("AA:BB:CC:DD:EE:FF", reopened.loadLastBluetoothAddress())
    }

    @Test fun invalidStoredAddressIsNotRestored() {
        val prefs = preferences()
        prefs.edit().putString("last_bluetooth_address", "192.168.1.10").apply()
        assertNull(SavedDeviceStore(prefs).loadLastBluetoothAddress())
    }

    @Test fun lastSuccessfulTargetIsFirstAndPreselectedOnlyWhilePaired() {
        val first = DiscoveredDevice("A", "01:23:45:67:89:AB", 45679, 0)
        val last = DiscoveredDevice("Z", "AA:BB:CC:DD:EE:FF", 45679, 0)
        val paired = listOf(first, last)
        assertEquals(last, BluetoothDeviceSelection.ordered(paired, last.ip).first())
        assertEquals(last, BluetoothDeviceSelection.target(paired, null, last.ip))
        assertEquals(first, BluetoothDeviceSelection.target(paired, first.ip, last.ip))
        assertNull(BluetoothDeviceSelection.target(listOf(first), null, last.ip))
    }
}
