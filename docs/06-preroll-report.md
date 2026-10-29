# 预验证报告（第 0 阶段）

> 目的：在正式开发前，用最小成本验证两条关键假设——
> ① 常用播放器是否允许被 MediaProjection 捕获；
> ② Wi-Fi/adb 传输相对蓝牙是否真的更稳定。

## 一、环境

| 项 | 值 |
| --- | --- |
| 手机 | Xiaomi Redmi 2407FRK8EC（Redmi K70 Ultra） |
| Android | 14（SDK 34） |
| 设备序列号 | 6HYXIJ7XQW7T9XQK（USB 连接） |
| 验证工具 | scrcpy 3.3.3（\`E:\scrcpy-win64-v3.3.3\`） |
| 播放器 | 网易云音乐 \`com.netease.cloudmusic\`（uid 10296） |
| PC 输出端点 | 头戴式耳机 (H180 Plus (Type C)) |
| 采样工具 | 自建 \`glitch\`（每 20 ms 读 MasterPeakValue） |

## 二、方法

1. \`scrcpy --no-video --no-control --no-window --audio-source=playback --audio-buffer=100\`
2. 通过 \`adb shell cmd media_session dispatch play\` 触发网易云播放
3. 用 \`glitch\` 每 20 ms 采样 PC 默认输出端点峰值，统计静音比例与断点

## 三、结果

### 1. 捕获可用性

| 项目 | 结果 |
| --- | --- |
| 网易云是否允许被捕获 | **是**（未设置 \`ALLOW_CAPTURE_BY_NONE\`） |
| 采样样本 | 600（12 秒） |
| **静音样本** | **0（0.0%）** |
| **≥100 ms 断点** | **0 次** |
| 波形 | 全程有声（\`################\`） |

### 2. 与蓝牙对比

| 指标 | 蓝牙 A2DP | Wi-Fi/adb（本次） |
| --- | --- | --- |
| 观察时长 | 20 秒 | 12 秒 |
| 静音比例 | 5.6% | **0%** |
| 最长连续静音 | **540 ms** | **0 ms** |
| ≥100 ms 断点 | **3 次** | **0 次** |

## 四、结论

1. **MediaProjection App 路线可行**：网易云可被 playback 源捕获，说明其 capture policy 为允许。
2. **Wi-Fi 传输稳定性显著优于蓝牙**：本次 12 秒零断点，蓝牙同期有 3 次断点、最长 540 ms。
3. 因此按原方案开发：**手机端 MediaProjection 采集 + UDP 传输，PC 端接收播放**。

## 五、遗留与后续

- scrcpy 的 \`playback\` 底层经 shell 权限走 \`REMOTE_SUBMIX\` 并叠加 capture policy 过滤，与我们 App 的 MediaProjection 路径在"是否允许捕获"这一判定上等价；但**音频采集延迟、缓冲策略不同**，实际延迟需在我们自己的实现中测量。
- 本次未测得精确端到端延迟；正式实现后由 PC 端 PING/PONG 统计。
- 需在正式联调时复测：视频类 App（B 站）、系统音效、静音场景。

## 六、风险提示

- 若后续发现某个 App 静音，走 Plan B（PC 端内置 adb 转发）。
- MIUI/HyperOS 可能限制后台运行，需在真机测试前台服务保活。
