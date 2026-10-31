package com.blueaudiosink.sender.ui

import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.blueaudiosink.sender.core.SenderUiState
import com.blueaudiosink.sender.net.DiscoveredDevice
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/** A long paired-device list must never hide the only action that starts audio. */
@RunWith(AndroidJUnit4::class)
class SenderScreenTest {
    @get:Rule val compose = createComposeRule()

    @Test fun startActionIsVisibleWithLongBluetoothDeviceList() {
        val devices = (1..12).map { DiscoveredDevice("Paired computer $it", "01:23:45:67:89:%02X".format(it), 45679, 0) }
        compose.setContent {
            BlueAudioSinkTheme {
                SenderScreen(
                    state = SenderUiState(bluetooth = true, pairedDevices = devices, targetIp = devices[0].ip),
                    onStart = {}, onTransportChange = {}, onBluetoothSettings = {}, onStop = {},
                    onConnect = { _, _ -> }, onConnectManual = {}, onRemoveSaved = {},
                    onTierChange = {}, onDismissMessage = {}, onRefreshDiscovery = {},
                )
            }
        }
        compose.onNodeWithText("开始发送").assertIsDisplayed()
    }
}
