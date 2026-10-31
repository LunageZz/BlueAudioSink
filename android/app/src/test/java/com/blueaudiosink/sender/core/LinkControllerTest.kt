package com.blueaudiosink.sender.core

import kotlinx.coroutines.flow.MutableStateFlow
import com.blueaudiosink.sender.net.DiscoveredDevice
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test

class LinkControllerTest {
    @Suppress("UNCHECKED_CAST")
    private val mutableState = LinkController::class.java.getDeclaredField("_state").let {
        it.isAccessible = true
        it.get(LinkController) as MutableStateFlow<SenderUiState>
    }
    private val streaming = LinkController::class.java.getDeclaredField("streaming").apply {
        isAccessible = true
    }

    @Before
    fun reset() {
        streaming.setBoolean(LinkController, false)
        mutableState.value = SenderUiState(targetIp = "127.0.0.1", targetPort = 50001)
    }

    @After
    fun cleanup() {
        streaming.setBoolean(LinkController, false)
        mutableState.value = SenderUiState()
    }

    @Test
    fun switchingComputerWhileStreamingPreservesDestinationAndStopButton() {
        streaming.setBoolean(LinkController, true)
        mutableState.value = mutableState.value.copy(phase = LinkPhase.STREAMING, packetsSent = 99)
        assertFalse(LinkController.connect("127.0.0.2", 50002))
        val state = LinkController.state.value
        assertEquals("127.0.0.1", state.targetIp)
        assertEquals(50001, state.targetPort)
        assertEquals(LinkPhase.STREAMING, state.phase)
        assertEquals(99L, state.packetsSent)
        assertTrue(state.message.orEmpty().contains("停止发送"))
    }

    @Test
    fun discoveryCannotChangeAudioPortDuringStreaming() {
        streaming.setBoolean(LinkController, true)
        mutableState.value = mutableState.value.copy(phase = LinkPhase.STREAMING)
        LinkController.onOffer(DiscoveredDevice("PC", "127.0.0.1", 50002, 1))
        assertEquals(50001, LinkController.state.value.targetPort)
        assertEquals(LinkPhase.STREAMING, LinkController.state.value.phase)
    }

    @Test
    fun delayedHeartbeatCallbackFromPreviousTargetCannotMarkNewTargetConnected() {
        LinkController.onPing("127.0.0.2", 50001, 1, 2)
        LinkController.onPing("127.0.0.1", 50002, 1, 2)
        assertEquals(LinkPhase.IDLE, LinkController.state.value.phase)
        LinkController.onPing("127.0.0.1", 50001, 1, 2)
        assertEquals(LinkPhase.CONNECTED, LinkController.state.value.phase)
    }
}
