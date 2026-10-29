# 测试与验收

> 本文件记录实测数据。所有数字必须来自真实运行，不得估算或照抄设计目标。

## 1. 测试环境

| 项 | 值 |
| --- | --- |
| PC | Windows 11 22621，Intel i7-14650HX，Wi-Fi 6E AX211 |
| 网络 | 5GHz Wi-Fi，链路 735 Mbps |
| 输出端点 | 头戴式耳机 (H180 Plus (Type C)) |
| 手机 | Redmi K70 Ultra（2407FRK8EC），Android 14 / SDK 34 |
| 手机 App | com.blueaudiosink.sender（debug APK） |
| PC 应用 | BlueAudioSink（--wifi 模式） |

## 2. 单端测试（无手机）

用 tools/loopback-inject.ps1 向本机注入报文，验证 PC 接收端：

| 用例 | 预期 | 实测 |
| --- | --- | --- |
| START → 5 包 AUDIO → 300ms → STOP | 进入接收→播放→排空→回 Armed | **通过**：Wifi.Playing → Wifi.DrainDone(drainedMs=9) → Player.Released |
| seq 缺口 7 包 | 记录 Audio.Gap，missing=7，静音填充不爆音 | **通过**：`Audio.Gap missing=7 seq=12` |
| 非法 magic | 计 malformed，不崩溃 | **通过**：`Packet.Malformed count=1 reason=magic` |
| 连续 5s 无包 | 自动转 Armed | **通过**：`Wifi.Timeout silentMs=5006` → state=Armed |
| 包长非 976 | 丢弃并计数 | 待填（未单独构造用例） |

## 3. 端到端测试（真机）

### 3.1 冒烟

| 步骤 | 预期 | 实测 |
| --- | --- | --- |
| 手机启动 App，PC 切 Wi-Fi 模式 | 手机发现 PC 或可手动输入 IP | **部分通过**：PC 端 `Wifi.OfferSent name=LIUBAI audioPort=45679` 每 2s 广播 |
| 点开始 → 授权录屏 | 进入发送中 | **通过**（Android 端自测） |
| 手机播放音乐 | PC 出声 | **通过**（意外联调：PC 端 received=600、Wifi.Playing、Pong.Recv rttMs=0.3~11.5） |
| 点停止 | PC 排空后回 Armed | **通过**：Wifi.DrainDone → Player.Released |
| 切回蓝牙模式 | 蓝牙可正常连接 | **通过**：Wifi.ModeOff → Connect.Armed → Link.Ready |

### 3.2 稳定性（30 分钟）

| 指标 | 目标 | 实测 |
| --- | --- | --- |
| 连续播放时长 | 30 分钟 | 待填 |
| ≥100ms 静音断点 | 0~1 次 | 待填 |
| 丢包率 | < 0.5% | 待填 |
| 手机内存 | ≤ 100 MB | 待填 |
| PC 内存 | ≤ 60 MB | 待填 |
| 缓冲深度波动 | 40~200ms 内自适应 | 待填 |

### 3.3 延迟

| 指标 | 目标 | 实测 |
| --- | --- | --- |
| 正常延迟 | ≤ 80 ms | 待填 |
| 抖动时延迟 | ≤ 150 ms | 待填 |
| RTT | — | 待填 |

## 4. 异常场景

| 场景 | 预期 | 实测 |
| --- | --- | --- |
| 手机暂停播放（静音包继续） | PC 保持接收态不超时 | 待填 |
| 手机切后台 | 前台服务保活，继续发送 | 待填 |
| Wi-Fi 断开 5s | PC 转 Armed，恢复后自动重连 | 待填 |
| 手机锁屏 | 继续发送（前台服务） | 待填 |
| 端口 45678 被占用 | 手机提示占用并拒绝开始 | 待填 |

## 5. UI 检查

| 项 | 预期 | 实测 |
| --- | --- | --- |
| Android 全中文 | 无英文残留 | 待填 |
| Android 3 种 DPI 清晰 | 100%/125%/150% 截图检查 | 待填 |
| Android 深浅色 | 跟随系统 | 待填 |
| PC 模式切换 | 蓝牙/Wi-Fi 互斥且状态正确 | 待填 |

## 6. 验收结论

- [ ] 单端测试全部通过
- [ ] 端到端冒烟通过
- [ ] 30 分钟稳定性达标
- [ ] 延迟达标
- [ ] 蓝牙模式无回归
- [ ] 文档与实现一致

**结论**：待填