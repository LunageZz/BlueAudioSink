package com.blueaudiosink.sender.core

import com.blueaudiosink.sender.net.DiscoveredDevice

/** Restore a successful target only while it is still paired; selection does not start capture. */
internal object BluetoothDeviceSelection {
    fun ordered(devices: List<DiscoveredDevice>, lastAddress: String?): List<DiscoveredDevice> =
        devices.sortedWith(compareByDescending<DiscoveredDevice> { it.ip.equals(lastAddress, ignoreCase = true) }.thenBy { it.name })

    fun target(devices: List<DiscoveredDevice>, currentAddress: String?, lastAddress: String?): DiscoveredDevice? =
        devices.firstOrNull { it.ip.equals(currentAddress, ignoreCase = true) }
            ?: if (currentAddress == null) devices.firstOrNull { it.ip.equals(lastAddress, ignoreCase = true) } else null
}
