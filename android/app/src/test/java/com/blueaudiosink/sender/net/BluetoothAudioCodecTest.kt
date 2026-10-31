package com.blueaudiosink.sender.net

import org.junit.Assert.*
import org.junit.Test
import java.io.File

class BluetoothAudioCodecTest {
    @Test fun crossLanguageGoldenPacketsAndIndependentState() {
        // Fixture generated with an independent reference implementation; also decoded by C# regression.
        val json=File("../../testdata/bluetooth-adpcm.json").readText()
        val encoded=Regex("\"encoded\": \"([^\"]+)\"").findAll(json).map { it.groupValues[1] }.toList()
        val pcm=Regex("\"pcm\": \"([^\"]+)\"").findAll(json).map { it.groupValues[1] }.toList()
        assertEquals(4,pcm.size); assertEquals(4,encoded.size)
        val encoder=BluetoothAudioCodec.Encoder()
        for (i in pcm.indices) assertArrayEquals(hex(encoded[i]),encoder.encode(hex(pcm[i])))
        assertEquals(152,hex(encoded[0]).size)
        assertEquals(68,hex(encoded[2]).size)
    }
    private fun hex(s: String)=s.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
}
