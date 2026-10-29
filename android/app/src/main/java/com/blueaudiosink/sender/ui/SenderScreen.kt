package com.blueaudiosink.sender.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.blueaudiosink.sender.R
import com.blueaudiosink.sender.capture.BufferTier
import com.blueaudiosink.sender.core.LinkPhase
import com.blueaudiosink.sender.core.SenderUiState
import com.blueaudiosink.sender.net.DiscoveredDevice
import java.util.Locale

/**
 * 单页界面：状态 → 目标电脑 → 缓冲档位 → 开始/停止 → 底部 RTT/丢包。
 * 所有文案来自 res/values/strings.xml，字号用 sp、间距用 dp，随系统 DPI 自适应。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SenderScreen(
    state: SenderUiState,
    onStart: () -> Unit,
    onStop: () -> Unit,
    onSelectDevice: (DiscoveredDevice) -> Unit,
    onConnectManual: (String) -> Unit,
    onTierChange: (BufferTier) -> Unit,
    onDismissMessage: () -> Unit,
    onRefreshDiscovery: () -> Unit,
) {
    val snackbarHostState = remember { SnackbarHostState() }
    val message = state.message
    LaunchedEffect(state.messageToken) {
        if (!message.isNullOrBlank()) {
            snackbarHostState.showSnackbar(message)
            onDismissMessage()
        }
    }

    var manualIp by rememberSaveable { mutableStateOf(state.targetIp.orEmpty()) }
    LaunchedEffect(state.targetIp) {
        val ip = state.targetIp
        if (!ip.isNullOrBlank()) manualIp = ip
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.app_name), fontWeight = FontWeight.SemiBold) },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.surface,
                    titleContentColor = MaterialTheme.colorScheme.onSurface,
                ),
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
        containerColor = MaterialTheme.colorScheme.background,
    ) { innerPadding ->
        Column(modifier = Modifier.fillMaxSize().padding(innerPadding)) {
            Column(
                modifier = Modifier
                    .weight(1f)
                    .verticalScroll(rememberScrollState())
                    .padding(start = 20.dp, end = 20.dp, top = 12.dp, bottom = 20.dp),
                verticalArrangement = Arrangement.spacedBy(20.dp),
            ) {
                StatusCard(state)
                TargetSection(
                    state = state,
                    manualIp = manualIp,
                    onManualIpChange = { manualIp = it },
                    onSelectDevice = onSelectDevice,
                    onConnectManual = onConnectManual,
                    onRefreshDiscovery = onRefreshDiscovery,
                )
                TierSection(state.tier, onTierChange)
                PrimaryButton(state, onStart, onStop)
                Text(
                    text = stringResource(R.string.consent_hint),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(
                    text = stringResource(R.string.stats_hint),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            StatsBar(state)
        }
    }
}

@Composable
private fun StatusCard(state: SenderUiState) {
    val dotColor = when (state.phase) {
        LinkPhase.STREAMING -> MaterialTheme.colorScheme.primary
        LinkPhase.CONNECTED -> Color(0xFF2E9E5B)
        LinkPhase.IDLE -> MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.45f)
    }
    val statusText = when (state.phase) {
        LinkPhase.STREAMING -> stringResource(R.string.status_streaming)
        LinkPhase.CONNECTED -> stringResource(R.string.status_connected)
        LinkPhase.IDLE -> stringResource(R.string.status_disconnected)
    }
    val targetText = if (state.targetIp.isNullOrBlank()) {
        stringResource(R.string.target_none)
    } else {
        (state.targetName ?: state.targetIp) + "  ·  " + state.targetIp
    }

    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
    ) {
        Column(
            modifier = Modifier.padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(14.dp).background(dotColor, CircleShape))
                Spacer(Modifier.width(10.dp))
                Text(
                    text = statusText,
                    style = MaterialTheme.typography.headlineSmall,
                    fontWeight = FontWeight.SemiBold,
                )
                Spacer(Modifier.weight(1f))
                if (state.phase == LinkPhase.STREAMING) {
                    Text(
                        text = state.packetsSent.toString() + " " + stringResource(R.string.unit_packets),
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            Text(
                text = targetText,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun TargetSection(
    state: SenderUiState,
    manualIp: String,
    onManualIpChange: (String) -> Unit,
    onSelectDevice: (DiscoveredDevice) -> Unit,
    onConnectManual: (String) -> Unit,
    onRefreshDiscovery: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = stringResource(R.string.target_title),
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold,
                modifier = Modifier.weight(1f),
            )
            TextButton(onClick = onRefreshDiscovery) {
                Text(stringResource(R.string.action_refresh))
            }
        }
        if (state.devices.isEmpty()) {
            Text(
                text = stringResource(R.string.device_empty),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        } else {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                state.devices.forEach { device ->
                    DeviceRow(
                        device = device,
                        selected = device.ip == state.targetIp,
                        onClick = { onSelectDevice(device) },
                    )
                }
            }
        }
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            OutlinedTextField(
                value = manualIp,
                onValueChange = onManualIpChange,
                modifier = Modifier.weight(1f),
                label = { Text(stringResource(R.string.manual_ip_label)) },
                placeholder = { Text(stringResource(R.string.manual_ip_hint)) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(
                    keyboardType = KeyboardType.Text,
                    imeAction = ImeAction.Done,
                ),
                keyboardActions = KeyboardActions(onDone = { onConnectManual(manualIp) }),
            )
            Button(
                onClick = { onConnectManual(manualIp) },
                enabled = manualIp.isNotBlank(),
            ) {
                Text(stringResource(R.string.action_connect))
            }
        }
    }
}

@Composable
private fun DeviceRow(device: DiscoveredDevice, selected: Boolean, onClick: () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = if (selected) {
                MaterialTheme.colorScheme.primaryContainer
            } else {
                MaterialTheme.colorScheme.surface
            },
        ),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .clickable(onClick = onClick)
                .padding(horizontal = 16.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(Modifier.size(8.dp).background(MaterialTheme.colorScheme.primary, CircleShape))
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(device.name, style = MaterialTheme.typography.bodyLarge)
                Text(
                    text = device.ip + ":" + device.port,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (selected) {
                Text(
                    text = stringResource(R.string.device_selected),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.primary,
                )
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TierSection(tier: BufferTier, onTierChange: (BufferTier) -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text(
            text = stringResource(R.string.tier_title),
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold,
        )
        SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
            BufferTier.entries.forEachIndexed { index, item ->
                SegmentedButton(
                    selected = tier == item,
                    onClick = { onTierChange(item) },
                    shape = SegmentedButtonDefaults.itemShape(
                        index = index,
                        count = BufferTier.entries.size,
                    ),
                    label = { Text(stringResource(item.labelRes)) },
                )
            }
        }
        Text(
            text = stringResource(tier.descriptionRes),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun PrimaryButton(state: SenderUiState, onStart: () -> Unit, onStop: () -> Unit) {
    val streaming = state.phase == LinkPhase.STREAMING
    Button(
        onClick = { if (streaming) onStop() else onStart() },
        enabled = streaming || !state.targetIp.isNullOrBlank(),
        modifier = Modifier.fillMaxWidth().heightIn(min = 72.dp),
        shape = MaterialTheme.shapes.large,
        colors = ButtonDefaults.buttonColors(
            containerColor = if (streaming) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary,
            contentColor = Color.White,
            disabledContainerColor = MaterialTheme.colorScheme.surfaceVariant,
            disabledContentColor = MaterialTheme.colorScheme.onSurfaceVariant,
        ),
    ) {
        Text(
            text = stringResource(if (streaming) R.string.action_stop else R.string.action_start),
            style = MaterialTheme.typography.titleLarge,
            fontWeight = FontWeight.SemiBold,
        )
    }
}

@Composable
private fun StatsBar(state: SenderUiState) {
    val lossText = String.format(Locale.US, "%.1f%%", state.lossPermille / 10.0)
    Surface(color = MaterialTheme.colorScheme.surface, shadowElevation = 8.dp) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .navigationBarsPadding()
                .padding(horizontal = 24.dp, vertical = 12.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            StatItem(stringResource(R.string.stats_latency), stringResource(R.string.value_on_pc))
            StatItem(stringResource(R.string.stats_loss), lossText)
            StatItem(stringResource(R.string.stats_packets), state.packetsSent.toString())
        }
    }
}

@Composable
private fun StatItem(label: String, value: String) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Text(
            text = value,
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold,
        )
        Text(
            text = label,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}