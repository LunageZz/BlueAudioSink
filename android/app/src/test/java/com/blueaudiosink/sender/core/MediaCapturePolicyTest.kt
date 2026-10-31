package com.blueaudiosink.sender.core

import org.junit.Assert.*
import org.junit.Test

/** The same policy drives projection consent, the service intent and stream negotiation. */
class MediaCapturePolicyTest {
    @Test fun bluetoothMicDoesNotInheritWifiScreenCaptureSelection() {
        val wifi = SenderUiState(mediaEnabled = true, microphoneEnabled = true)
        assertTrue(wifi.captureMedia)
        val bluetooth = wifi.copy(bluetooth = true)
        assertFalse("Native A2DP plus microphone must not request screen capture", bluetooth.captureMedia)
        assertTrue(bluetooth.copy(bluetooth = false).captureMedia)
    }

    @Test fun onlyExplicitBluetoothMediaCopyRequestsProjection() {
        val bluetooth = SenderUiState(bluetooth = true, mediaEnabled = false, microphoneEnabled = true)
        assertFalse(bluetooth.captureMedia)
        assertTrue(bluetooth.copy(bluetoothMediaCopyEnabled = true).captureMedia)
        assertFalse(bluetooth.copy(bluetooth = false).captureMedia)
    }
}
