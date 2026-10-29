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
| 连续播放时长 | 30 分钟 | **60 秒**（首轮） |
| ≥100ms 静音断点 | 0~1 次 | **0 次**（60 秒 / 3000 样本，仅 1 个 20ms 静音样本） |
| 丢包率 | < 0.5% | **0.0%**（received=2000, missing=0） |
| 手机内存 | ≤ 100 MB | **114.3 MB**（略超，PSS） |
| PC 内存 | ≤ 60 MB | 待填（未单独测 Wi-Fi 模式下的稳定值） |
| 缓冲深度波动 | 40~200ms 内自适应 | **41~50ms**（target=50，自适应生效） |

### 3.3 延迟

| 指标 | 目标 | 实测 |
| --- | --- | --- |
| 正常延迟 | ≤ 80 ms | **约 70~80ms 估算**（RTT 16~19ms + 缓冲 41~50ms + 播放 50ms） |
| 抖动时延迟 | ≤ 150 ms | 待填（本轮 RTT 抖动最高 133ms） |
| RTT | — | **16~19ms**（Wi-Fi 5GHz 同子网） |

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
## 7. 首轮端到端实测记录（2026-10-30）

### 7.1 链路

手机 Redmi K70 Ultra（192.168.0.116）→ Wi-Fi 5GHz → PC（192.168.0.165）
手机 App 通过 MediaProjection 捕获网易云音乐（`com.netease.cloudmusic`），UDP 200 包/秒发送。

### 7.2 结果

| 项 | 12 秒测 | 60 秒测 |
| --- | --- | --- |
| 样本数（20ms 间隔） | 600 | 3000 |
| 静音样本 | 0 | 1（20ms） |
| 最长连续静音 | 0ms | 20ms |
| ≥100ms 断点 | 0 | 0 |

### 7.3 统计（Wifi.Stats）

```
state=Flowing rttMs=17.8 offsetMs=1838219 bufferMs=41~50 targetMs=50
lossPct=0 received=2000 missing=0 dup=0 late=0 resets=0
underruns=17~19 discontinuities=3 malformed=0 foreign=0 stale=0
```

### 7.4 与蓝牙对比

| 指标 | 蓝牙 A2DP | Wi-Fi 模式 |
| --- | --- | --- |
| ≥100ms 断点 | 20 秒内 3 次 | 60 秒内 **0 次** |
| 最长连续静音 | **540 ms** | **20 ms** |
| 丢包率 | — | **0.0%** |

### 7.5 待改进

1. **underruns 17~19 次**：缓冲基数 40ms 偏小，Wi-Fi 抖动时会短暂欠载。
   按「优先稳定」原则，建议把 `bufferbasems` 默认从 40 提到 60~80 再复测。
2. **Android PSS 114.3 MB**：略超 100 MB 目标。
3. 30 分钟长跑未做。

### 7.6 发现的问题（已解决）

- 广播发现失败：PC 每 2 秒广播 OFFER，但手机未收到；同子网、防火墙已全关。
  手动输入 IP 后 HELLO 立即成功。**广播在部分路由器/AP 下会被过滤，手动 IP 是有效兜底。**
- 授权对话框需精确坐标：MediaProjection 系统对话框按钮 bounds 为
  `[628,2445][1097,2600]`，需用 uiautomator 获取而非估算。
