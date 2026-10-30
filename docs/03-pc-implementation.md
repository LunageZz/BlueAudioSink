> 电脑端界面与状态呈现已更新：以 [10-desktop-ux.md](10-desktop-ux.md) 为准。下方旧版 UI 排布仅作历史说明。

> 2026-10-30 播放行为按协议 v1.2 更新；历史 seq 表和旧验收数据请以协议修订与修复报告为准。

# PC 端实现（Wi-Fi 模式）

> 对应协议：`docs/02-protocol.md` v1.1（5 ms / 960 字节 / 200 包每秒）。
> 本文只描述 PC 端实现，不改变协议；与 01/02 冲突时以文档为准。
> 最后更新：2026-10-30。

## 1. 范围与文件

| 文件 | 职责 |
| --- | --- |
| `src/SinkLite/Core/WifiTransport.cs` | 协议常量、16 字节包头编解码、OFFER / PONG 组包、UTF-8 截断 |
| `src/SinkLite/Core/WifiReceiver.cs` | 绑定 45678/45679、双接收线程、会话/seq/缺口、抖动缓冲 D、PING/PONG、统计、播放调度 |
| `src/SinkLite/Core/AudioOutput.cs` | `BufferedWaveProvider`（1000 ms）+ `WasapiOut`（共享 50 ms）、静音/淡入、丢弃最旧、播放器生命周期 |
| `src/SinkLite/Core/WifiController.cs` | Wi-Fi 模式生命周期、端口绑定结果、OFFER 定时器、UI 快照 |
| `src/SinkLite/Ui/MainWindow.cs` | 顶部「蓝牙 / Wi-Fi」切换、Wi-Fi 页面绘制、模式互斥与切换顺序 |
| `src/SinkLite/Platform/Settings.cs` | `Mode` / `WifiPort` / `BufferBaseMs` / `BufferMaxMs` |
| `src/SinkLite/Platform/Cli.cs` | `--wifi` 参数 |
| `src/SinkLite/Program.cs` | 启动模式选择；Wi-Fi 启动时不做蓝牙自动连接 |
| `src/SinkLite/Platform/Log.cs` | 增加 uint 数字字段输出（协议 ts/seq 不变成字符串） |

**未改动**：`Core/SinkController.cs`（蓝牙状态机一行未动）、`android/`、`docs/00/01/02`、根目录文件。

## 2. 线程模型

```
UI 线程（Win32 消息循环，1 s 定时器）
  └─ WifiController.Current → WifiReceiver.GetSnapshot（短锁）

WifiReceiver
  ├─ WifiControl 线程：recvfrom 45678（HELLO/START/STOP/PONG/PING/OFFER）
  ├─ WifiAudio   线程：recvfrom 45679（AUDIO，实际实现也兼容控制包）
  ├─ WifiPlayout 线程：每 5 ms 调度一帧；500 ms 调整 D；1 s 聚合日志；5 s Wifi.Stats；2 s PING
  └─ AudioOutput → WasapiOut 自带播放线程
```

约束：

- 三个接收/播放线程都是后台线程；`running=false` + 关闭 socket 让阻塞的 `ReceiveFrom` 返回。
- 所有会话状态、队列、统计都在 `WifiReceiver.sync` 下；锁内不做 socket I/O、不做播放器创建/释放。
- **播放器打开和释放都在锁外执行**。WasapiOut 的 `Init/Stop/Dispose` 在部分设备上可能阻塞数百毫秒到数秒；若持锁会卡住 UDP 接收线程，实测会导致开场丢失整段音频。
- `GetSnapshot` 只返回不可变对象，不把队列/播放器引用交给 UI。
- 音频帧从 `ArrayPool<byte>` 租用，播放/丢弃/重置时归还；每秒约 200 次 960 字节租还，不产生大对象。

## 3. 端口与包处理

### 3.1 绑定

`WifiReceiver.Start()` 依次绑定：

| socket | 地址 | 用途 |
| --- | --- | --- |
| control | `0.0.0.0:45678` | OFFER 广播、PING、接收 HELLO/START/STOP/PONG |
| audio | `0.0.0.0:45679` | 接收 AUDIO |

任一绑定失败：关闭已绑定 socket，记录 `Wifi.BindFailed`（controlPort/audioPort/error），`WifiController.Start()` 返回 false。UI 保持蓝牙模式，不启动 OFFER/PING。

### 3.2 校验

- 长度 < 16、magic 不符 → `malformed`；
- AUDIO 必须总长 **976**（16 + 960），否则 `malformed`；
- PONG 必须总长 **20**；HELLO payload 1~64 字节；
- 未知 type → `unknown`；
- 所有多字节字段小端；时间差用 u32 模运算。

### 3.3 端点和会话

- `sessionEndpoint`：START 或待机首包 AUDIO 的源 `IP:端口`；只接受该端点的 AUDIO。
- `remote`：最近一次有效手机报文端点；待机时作为 PING 候选。
- 非会话端点 AUDIO → `foreign`，丢弃。
- START 可抢占当前会话；旧队列、旧基线、旧统计清空。
- STOP 后、下一次 START 前的 AUDIO → `late`，丢弃。
- 收到 START 时记录 `T_start`；AUDIO 的 `timestampUs` 早于 `T_start − 100 ms` → `stale`，丢弃。
- START 丢失：Armed 下首个合法 AUDIO 自动建会话，基线取该包 seq，不做 `T_start` 过滤。

## 4. seq 与丢包（协议 §7.1）

维护 `expectedSeq`（首个接受的 AUDIO 令其为 `seq+1`）。收到 seq 后：

```
d = (uint32)(seq - expectedSeq)
d == 0                 → 正常接受，expectedSeq = seq+1
1 <= d <= 10           → 缺口：向队尾追加 d 个 5 ms 静音帧，再追加当前包；
                          missing += d；d > 5 记 1 次断流；expectedSeq = seq+1
d == 2^32-1            → 重复，dup++
2^32-10 <= d <= 2^32-2 → 迟到/乱序，late++，丢弃
其他（前向 >10）        → 重置：当前包为新基线，清队列/播放缓冲，重缓冲；
                          resets++、discontinuities++，不计 missing
```

- 缺口静音在**入队时**追加，时间轴保持连续；播放线程不需要再按 seq 补静音。
- 缺口 d 1~5 也补静音；断流计数只在 d>5。
- 重置后保留会话统计（协议要求），只清抖动窗口与队列。
- AUDIO 的 `flags.bit0` 只由手机置位；PC 只记录一次 `discontinuities` 并安排 5 ms 淡入，绝不回写 flags。

## 5. 播放缓冲（v1.2，2026-10-30）

以 `02-protocol.md` §6/§7 和 `08-audio-playout-fix.md` 为准。

- WifiReceiver 每轮释放可用重排包并尽快送入 AudioOutput，2 ms 循环只做转交/超时/统计，不再决定 PCM 消费速率。
- PlayoutBuffer.Read 由 WasapiOut 驱动，是唯一消费点；计实际 PaddingFrames / Underruns / Overflows。
- 每 500 ms 只更新首次/恢复预填阈值（默认最低 80 ms），不能为调节正常队列深度插静音/裁剪。
- 空接收队列不等于声卡欠载；Read 真正不足才恢复。恢复期间保留未消费音频，达到完整阈值后恢复。
- 小幅乱序等待 20 ms；帧本地 FadeIn 标记确保淡入位置与实际断流包一致。
- 容量 1 秒，溢出明确故障。STOP 正常排空与故障补零分开统计。

## 6. 状态机（复用 SinkState）

| 状态 | Wi-Fi 含义 | 进入 | 离开 |
| --- | --- | --- | --- |
| Idle | 模式关闭（切到蓝牙/退出） | `WifiController.Stop` | 重新 Start |
| Busy | 绑定端口/切换中（短过渡） | — | 绑定成功/失败 |
| Armed | 45678/45679 已绑定，OFFER/PING 运行，无会话 | Start 成功；STOP 排空完成；5 s 超时；重置后释放 | START/首包 AUDIO |
| LinkReady | 预缓冲/重缓冲/排空中 | START/首包；欠载；d>10 重置；STOP | D 达标 → Flowing；排空完成 → Armed；故障 → Faulted |
| Flowing | 已启动播放且未重缓冲（静音也算） | 预缓冲完成 | 欠载/重缓冲、STOP、超时、故障 |
| Faulted | 端口/播放器/接收 socket 致命错误 | BindFailed/Player.Fault/ReceiveError | 下一次 START（播放器类）或用户重选 Wi-Fi（端口类） |
| Backoff | Wi-Fi 不使用 | — | — |

- STOP/超时回 **Armed**，不是 Idle；socket 与 OFFER/PING 保留。
- Flowing 不用峰值判定；手机发静音包时仍为 Flowing。
- UI 映射：Armed「等待手机连接」、LinkReady「正在缓冲」、Flowing「正在接收 Wi-Fi 音频」、Faulted「Wi-Fi 故障」。

## 7. PING/PONG 与延迟

- PC 每 2 秒从 **45678** socket 向 `sessionEndpoint ?? remote` 发 PING；条件是最近 10 秒内收到过该手机任意合法报文，且状态非 Faulted。
- PING：`seq=0`，`timestampUs=T1`（PC Stopwatch 微秒低 32 位）。
- 手机 PONG：payload = 原样回填 T1；header.timestampUs = T2（手机发送时刻）。
- PC 收到 PONG：
  - 校验源端点、payload == 最近一次 PING 的 T1；
  - `RTT = u32(T3 − T1)`，仅接受 `0 < RTT ≤ 2,000,000 µs`；
  - `offset(手机−PC) = (int32)(T2 − T1 − RTT/2)`；
  - 记录 `Pong.Recv`（rttMs、offsetMs）。
- 延迟显示（仅展示）：
  ```
  age = (int32)(nowPC − lastAudioTimestampUs) + offset
  显示延迟 = age/1000 + 50 ms
  ```
  仅当 Flowing、offset 有效、最近 10 秒有 PONG、`0 ≤ age ≤ 2 s` 时显示；否则 `—`。
- RTT/offset 在会话重置/回 Armed 时清空；PING 在 Armed 仍运行，因此可重新获得样本。

## 8. 播放器与格式

- `AudioOutput` 构造 `PlayoutBuffer(48000,16,2)`，容量 1 s；底层 ReadFully=false，真实补零由 Read 包装层计数。
- `WasapiOut(AudioClientShareMode.Shared, 50)` 打开**默认输出端点**；`Init` 不 `Play`，预填后再 `Play`。
- 事件：`Player.Created`（format/latencyMs/bufferMs）、`Player.Released`、`Player.Fault`。
- 播放器打开失败 → `Faulted`，停止 PING；下一次 START 重试。
- 打开/释放都在接收锁外；释放经 `pendingRelease` 交给播放线程在下一轮锁外 `Dispose`。

## 9. UI 与模式切换

### 9.1 UI 路径

1. 启动 `BlueAudioSink.exe` → 主窗口右上角「蓝牙 / Wi-Fi」。
2. 点「Wi-Fi」→ 绑定 45678/45679 成功后释放蓝牙；页面显示：
   - 标题/圆点：等待手机连接 / 正在缓冲 / 正在接收 Wi-Fi 音频 / Wi-Fi 故障；
   - 手机端点与设备名、本机 IP、音频端口、发现广播端口；
   - RTT、缓冲深度 D / lastTarget、估算延迟、丢包率（10 秒窗口，无包显示「—」）；
   - 缓冲条、收包/丢包/断流/欠载/溢出丢弃计数；
   - 提示文案。
3. 点「蓝牙」→ 先停 Wi-Fi 播放器并关闭 45678/45679，再走既有蓝牙连接流程。

### 9.2 切换顺序

- **蓝牙 → Wi-Fi**：先 `wifi.Start()` 绑定 45678/45679；成功后才 `controller.RequestDisconnect()`；绑定失败保持蓝牙不变（`Wifi.BindFailed`）。
- **Wi-Fi → 蓝牙**：先 `wifi.Stop("bluetooth")`（释放播放器、关闭 socket、停 OFFER/PING），再 `controller.RequestConnect(...)`。
- Wi-Fi 播放器只在 START/首包后创建，所以“绑定成功但蓝牙尚未释放”的过渡期不会双重出声。

### 9.3 设置与 CLI

- 设置文件：`%LocalAppData%\BlueAudioSink\settings.ini`
  `mode=wifi|bluetooth`、`wifiport=45679`、`bufferbasems=80`、`buffermaxms=200`。
- `--wifi`：直接以 Wi-Fi 模式启动（覆盖 settings.Mode）；启动后仍显示主窗口，可切回蓝牙。
- 无参数启动读取 `mode`；若 `mode=wifi` 则启动即 Wi-Fi。
- 单实例互斥仍生效：已有实例运行时第二个 `--wifi` 直接退出。

## 10. 日志事件（与 01 §11 对齐）

PC 端输出（JSONL，`%LocalAppData%\BlueAudioSink\logs\sink-YYYYMMDD.jsonl`）：

| 事件 | 关键字段 | 时机 |
| --- | --- | --- |
| `Wifi.ModeOn` / `Wifi.ModeOff` | mode | 模式绑定成功 / 关闭 |
| `Wifi.Bind` / `Wifi.BindFailed` | controlPort/audioPort/error | 绑定 45678/45679 |
| `Wifi.OfferSent` | name/audioPort | 首次 + 每 30 s |
| `Wifi.Hello` | name/endpoint | 收到 HELLO |
| `Wifi.Start` | endpoint/ts/auto | START 或首包自动建会话 |
| `Wifi.FirstAudio` | seq/ts | 会话首包 |
| `Wifi.Playing` | bufferMs | 预缓冲完成 |
| `Wifi.Stop` / `Wifi.DrainDone` | ts / drainedMs | STOP / 排空结束 |
| `Wifi.Timeout` | silentMs | 5 s 无 AUDIO |
| `Wifi.Reset` | oldSeq/newSeq | d > 10 |
| `Wifi.Fault` | code/detail | 端口/播放器/收发故障 |
| `Player.Created` / `Player.Released` / `Player.Fault` | format/code | 播放器生命周期 |
| `Pong.Recv` | rttMs/offsetMs | 每个有效 PONG |
| `Audio.Gap` | missing/seq | 缺口（1 s 聚合） |
| `Audio.Underrun` / `Audio.Rebuffer` | count / bufferMs | 欠载/重缓冲（1 s 聚合） |
| `Packet.Malformed` | count/reason | 校验失败（1 s 聚合） |
| `Wifi.Stats` | 见下 | 每 5 s（有会话或最近 10 s 有手机报文） |

`Wifi.Stats` 字段：
`state, rttMs, offsetMs, bufferMs, targetMs, lossPct, received, missing, dup, late, resets, underruns, discontinuities, malformed, foreign, stale, unknown`。

- `lossPct` 为最近 10 秒窗口百分比；窗口无包时为 `null`。
- `received/missing` 为窗口内计数；其余为会话累计（malformed/foreign/unknown 全局累计）。
- 数值四舍五入：RTT/缓冲整数 ms，丢包率 2 位小数（日志）/UI 1 位。

## 11. 验证方法（可复现）

### 11.1 编译

```powershell
dotnet build .\BlueAudioSink.sln -c Debug -p:Platform=x64
# 期望：0 个警告，0 个错误
```

### 11.2 端口

```powershell
.\src\SinkLite\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\BlueAudioSink.exe --wifi
Get-NetUDPEndpoint -LocalPort 45678,45679 |
  Format-Table LocalAddress,LocalPort,OwningProcess
# 期望：0.0.0.0:45678 与 0.0.0.0:45679 同属 BlueAudioSink
```

### 11.3 日志

```powershell
$log = Get-ChildItem "$env:LOCALAPPDATA\BlueAudioSink\logs\sink-*.jsonl" |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content $log.FullName -Tail 80
```

### 11.4 无手机回环

优先使用主 agent 的 `tools/loopback-inject.ps1`（单 socket、同源端口）：

1. START 发 `127.0.0.1:45678`；
2. AUDIO（960 字节）发 `127.0.0.1:45679`；
3. 注入 7 包缺口 → 期望 `Audio.Gap missing=7`；
4. 非法 magic → 期望 `Packet.Malformed count=1 reason=magic`；
5. STOP → 期望 `Wifi.Stop`、`Wifi.DrainDone`（≤500 ms）、`Player.Released`；
6. 只发 START 不发 AUDIO → 5 s 后 `Wifi.Timeout silentMs≈5000`，状态回 Armed。

> 注意：回环脚本的所有报文必须来自**同一个 UDP socket**，否则会被端点过滤判为 `foreign`。
> AUDIO 的 `timestampUs` 必须是手机单调时钟（与本机测试进程的 `process.hrtime`/Stopwatch 同源），且不早于 START−100 ms，否则会被判 `stale`。

### 11.5 已完成的实测

- 编译：0 警告 0 错误。
- 端口：45678/45679 均绑定 `0.0.0.0`。
- OFFER：`Wifi.OfferSent name=LIUBAI audioPort=45679`，每 2 s。
- 预缓冲/播放：`Player.Created format=48000/16/2`、`Wifi.Playing bufferMs=80`。
- 缺口：`Audio.Gap missing=7 seq=12`（7 包缺口精确匹配）。
- 非法包：`Packet.Malformed count=1 reason=magic`。
- 排空：`Wifi.DrainDone drainedMs=9` → `Player.Released`。
- 超时：`Wifi.Timeout silentMs=5006` → Armed。
- PING/PONG：`Pong.Recv rttMs=0.3~11.5`、`offsetMs` 稳定；UI 显示 RTT/缓冲/延迟/丢包率。
- 模式切换：`Wifi.ModeOff mode=bluetooth` → `Connect.Armed`；再切回 Wi-Fi 重新 `Wifi.Bind`。

## 12. 已知限制 / 实现差异

1. **没有独立 ModeCoordinator 类**：模式切换顺序由 `MainWindow.SetMode` 串行执行；UI 线程承担切换，未做“切换中禁用按钮/10 s 硬超时”的独立状态机。切换流程本身很快，但极端慢设备上可能阻塞 UI。
2. **Wi-Fi 输出固定为默认端点**：`WasapiOut` 使用默认输出；蓝牙页的“输出”选择只影响蓝牙路由，不改变 Wi-Fi 播放器。会话中不跟随系统默认变化，下一次会话重新绑定（协议 §12.9）。
3. **淡出不完整**：缺口/断流恢复做了 5 ms 淡入；缺口前最后一帧的 5 ms 淡出未实现（需要回改已入播放缓冲的数据）。静音帧本身避免爆音，属于可接受的 v1 差异。
4. **设备格式转换依赖 NAudio**：以 48k/16/2 打开共享模式，由 NAudio/WASAPI 处理设备混音格式；若设备不支持且转换失败，`Player.Fault` + Faulted，需用户检查默认输出设备。
5. **端点过滤较严格**：只接受当前会话端点（IP:端口）的 AUDIO；手动回环测试必须单 socket。手机换 IP/端口需重新 START。
6. **AUDIO 也在控制 socket 上被解析**：这是为兼容/容错保留的宽松行为，正常协议仍要求 AUDIO→45679、控制→45678。
7. **PING 目标使用当前端点原端口**：协议 v1.1 手机固定 45678，所以等价于“手机 IP:45678”；若手机未按规范绑定，PING 会发到其实际源端口。
8. **统计窗口**：`received/missing` 按最近 10 秒窗口，其余为会话累计；`malformed/foreign/unknown` 为进程级累计。`lossPct` 以百分比输出（UI/日志），与协议公式的 0~1 比例是同一数值的百分比表示。
9. **单实例**：已有 BlueAudioSink 运行时，`--wifi` 第二个进程直接退出；不会抢端口。
10. **E2E 延迟未用真机复测**：当前 50 ms 为 WasapiOut 固定估算；实际端到端延迟需真机联调确认。
11. **广播/防火墙**：OFFER 失败只记录 `Wifi.Fault code=OfferFailed`（限频）；不影响手动输入 IP 的音频接收。
12. **蓝牙零回归**：`SinkController.cs` 未改动；`MainWindow` 仅在主页面新增模式切换与 Wi-Fi 分支，蓝牙页面绘制/连接流程保持原逻辑。

## 13. 后续可做（非本次范围）

- 抽出 `ModeCoordinator`，把切换超时/回滚/禁用按钮集中管理。
- 缺口前后完整淡出/淡入；用 `tailTs` 精确实现 §5.3 延迟显示。
- Wi-Fi 输出端点选择（按 `PreferredRenderId` 打开指定 MMDevice），并与蓝牙输出选择统一。
- 30 分钟连续播放/弱网抖动压测，回填 `docs/05-testing-acceptance.md`。
