package com.blueaudiosink.sender.net
data class MicrophoneStatus(val host: String, val port: Int, val muted: Boolean,
    val volume: Int, val deviceReady: Boolean, val shareMedia: Boolean, val requestOverridden: Boolean = false, val sessionId: Int = 0)
