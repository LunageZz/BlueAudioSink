package com.blueaudiosink.sender.capture

import com.blueaudiosink.sender.R

/**
 * 缓冲档位：控制「采集 → 发送」之间的预填充深度（1 包 = 5 ms）。
 *
 * 手机端必须保持 200 包/秒的连续发送，因此这里调的是抖动缓冲深度：
 * 越深越抗采集线程被调度延迟，代价是端到端延迟更大。
 * PC 端自适应抖动缓冲（协议第 6 节）仍会在此基础上再叠加。
 *
 * 界面只显示三个标签（低延迟 / 平衡 / 稳定），不再附带解释文字。
 */
enum class BufferTier(
    /** 发送前预填充的包数（5 ms/包）。 */
    val prefillPackets: Int,
    val labelRes: Int,
) {
    LOW_LATENCY(2, R.string.tier_low),
    BALANCED(5, R.string.tier_balanced),
    STABLE(10, R.string.tier_stable);

    companion object {
        fun fromName(name: String?): BufferTier = entries.firstOrNull { it.name == name } ?: BALANCED
    }
}
