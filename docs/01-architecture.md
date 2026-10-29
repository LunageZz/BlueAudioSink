# 架构设计（PC 与 Android 共享）

> 本文是两端共同的**结构与行为基线**，与 `02-protocol.md` 配套使用。
> **线格式、字段、阈值以 `02-protocol.md` 为准**；本文负责数据流、模块职责、模式切换、状态映射、失败恢复与可观测性。
> 两端并行开发时，任何与本文不一致的实现都必须先改文档、再改代码；不得用“先做出来再说”绕过。
>
> 修订：v1.1（2026-10-30），与协议 v1.1 同步：AUDIO 为 **5 ms / 240 帧 / 960 字节 / 200 包每秒**；单包 IP 层 1004 字节，不分片。

## 1. 目的与范围

本文解决三个问题：

1. **两端对同一条链路的理解一致**：谁在什么时候产生、发送、接收、缓冲、播放、统计。
2. **PC 端两种模式不互相污染**：蓝牙模式（既有）与 Wi-Fi 模式（新增）互斥，资源、状态机、播放器各自独立。
3. **失败可归因、可恢复**：每一种失败都有唯一的检测信号、自动动作、用户可见结果与日志事件。

本文**不**规定具体类名、UI 像素、第三方库选型；这些属于 `03-pc-implementation.md` / `04-android-implementation.md`。本文中的“必须/禁止”是两端共同约束。

## 2. 术语（与协议 §0 一致）

| 术语 | 含义 | 关键点 |
| --- | --- | --- |
| 采样时刻 | 一个 PCM 帧被手机采集到的时刻 | 手机单调时钟；不是发送/读取时刻 |
| 发送时刻 | 报文交给 UDP 栈的时刻 | 两端各自单调时钟 |
| 到达时刻 | PC 从 UDP socket 读出报文的时刻 | PC 单调时钟 |
| 会话 | START（或首个 AUDIO 自动建立）到 STOP 排空/5 秒超时 | 一次会话内 seq 从 0 开始 |
| 抖动 J | 相邻包“到达间隔 − 采样间隔”的绝对偏差均值 | 最近 2 秒窗口 |
| 抖动缓冲深度 D | PC 已入队未播放的音频时长 | 初始/最小 40 ms；目标上限与硬上限均为 200 ms |
| lastTarget | 最近一次 500 ms 评估算出的目标 | 仅用于重缓冲阈值与显示 |
| 断流 | 音频时间轴上的可感知缺口/内容不连续 | 网络断流 vs 采集断流 |
| 丢包 | 手机已产出但 PC 未收到的 AUDIO 包 | 仅 seq 缺口 1~10 计入 |
| 待机（Armed） | 模式就绪、尚无会话 | Wi-Fi 下仍监听 45678、仍广播 OFFER |
| 端点 | 一次会话对应的手机 UDP 源 IP:端口 | 固定为 手机 IP:45678 |
| 预缓冲 | 会话开始后攒够 40 ms 再启动播放 | 避免一开播就欠载 |
| 重缓冲 | 欠载后暂停播放头、攒回 lastTarget 再恢复 | 不清 seq 基线 |
| 模式 | 蓝牙 / Wi-Fi | 互斥，同一时刻只有一个播放器 |

## 3. 系统总览

```
┌────────────────────── Android ──────────────────────┐        UDP/IPv4        ┌────────────────────── Windows PC ──────────────────────┐
│ UI（发现 / 开始 / 停止 / 状态 / RTT 存活）             │                        │ UI（模式 / 状态 / 输出选择 / 指标）                      │
│        │                                             │                        │        │                                                 │
│ SessionController（开始/停止顺序、错误归因）           │                        │ ModeCoordinator（BT / Wi-Fi / Switching，互斥）          │
│        │                                             │                        │        ├──────────────┐                                  │
│ CaptureService（前台服务 + MediaProjection）           │   45678 控制/保活       │        ▼              ▼                                  │
│        │                                             │   <──────────────>     │ SinkController     WifiController（新增）               │
│ AudioCapture（AudioRecord 48k/16/2）                  │                        │ （既有蓝牙）        ├─ UdpControl 45678                    │
│        │                                             │   45679 音频 200 pps    │                    ├─ UdpAudio 45679                      │
│ Packetizer（240 帧 / seq / ts / 补静音）               │   ────────────────>    │                    ├─ JitterBuffer（D/target）             │
│        │                                             │                        │                    ├─ AudioOutput（WasapiOut）             │
│ UdpTransport（单 socket 45678 + PONG 应答）            │                        │                    └─ WifiStats                          │
│        │                                             │                        │                                                          │
│ Discovery（OFFER 解析 / 6 秒老化 / 手动 IP）            │                        │ 共享：Settings / Log / SinkSnapshot / UI 定时刷新          │
└──────────────────────────────────────────────────────┘                        └──────────────────────────────────────────────────────────┘
```

三条硬边界：

1. **模式互斥**：蓝牙播放器与 Wi-Fi 播放器不得同时存在；切换过程由 `ModeCoordinator` 串行化。
2. **状态机独立**：蓝牙的 `SinkController` 不承载 Wi-Fi 的会话逻辑；Wi-Fi 的 `WifiController` 不复用 `AudioPlaybackConnection`。
3. **单一快照**：UI 只读 `SinkSnapshot`；两种模式把各自状态映射到同一套 `SinkState`，避免 UI 分叉。

## 4. PC 端模块职责

| 模块（建议文件） | 必须负责 | 禁止负责 |
| --- | --- | --- |
| `Program.cs` | 入口、单实例、CLI、模式分发、消息循环 | 具体音频/网络算法 |
| `Core/ModeCoordinator.cs`（新增） | 模式状态（BT/Wi-Fi/Switching）、切换顺序、资源占用仲裁、快照汇总 | 解析 AUDIO、调 WasapiOut |
| `Core/SinkController.cs`（既有） | 蓝牙四段状态机、连接对象持久化、退避重试、端点归因 | Wi-Fi 端口/会话/抖动缓冲 |
| `Core/WifiController.cs`（新增） | Wi-Fi 模式生命周期、会话状态（待机/接收中/排空中/故障）、START/STOP/超时、seq/丢包/统计 | 直接读 UDP socket、直接写播放器 |
| `Platform/UdpControl.cs`（新增） | 绑定 45678、OFFER 广播（2 s）、PING 定时器（2 s）、HELLO/START/STOP/PONG 解析、端点记忆 | 处理 AUDIO 大包、播放 |
| `Platform/UdpAudio.cs`（新增） | 绑定 45679、接收并校验 AUDIO（总长 976）、端点过滤、交给 JitterBuffer | 决定会话状态、丢弃策略以外的业务 |
| `Core/JitterBuffer.cs`（新增） | D 的维护、预缓冲/重缓冲、500 ms 调整、缺口补静音/重置、tailTs | 网络收发、UI |
| `Platform/AudioOutput.cs`（新增） | `BufferedWaveProvider`（1000 ms）+ `WasapiOut`（共享 50 ms）、格式适配、创建/释放、异常上报 | 会话/seq 判定 |
| `Core/WifiStats.cs`（新增） | 10 秒丢包窗口、会话计数、5 秒快照、统计字段 | 改变播放行为 |
| `Core/Models.cs` | `SinkState` 复用；`SinkSnapshot` 增加模式与 Wi-Fi 指标字段 | 把模式特有字段硬塞进蓝牙逻辑 |
| `Ui/MainWindow.cs` | 模式选择、状态/指标显示、输出选择、设置页 | 直接操作 socket/播放器 |
| `Platform/Settings.cs` | 首选输出、上次模式、自动连接等持久化 | 协议参数（协议参数在 02） |
| `Platform/Log.cs` | JSONL 滚动日志、事件字段 | 记录音频内容/隐私 |
| `tools/SinkProbe` | 端点/峰值/设备诊断 | Wi-Fi 会话控制（避免和主程序抢端口） |

**线程模型（PC）**

- `ModeCoordinator` 的命令在单一线程串行执行；模式切换不并发。
- `UdpControl` 一个接收线程 + 定时器（OFFER/PING）；`UdpAudio` 一个接收线程。
- `JitterBuffer` 的 500 ms 调整由独立定时器驱动；与 AUDIO 入队、播放器读取共用一把短锁，锁内不做 I/O。
- `WasapiOut` 自带播放线程；播放异常通过回调/状态轮询上报给 `WifiController`。
- UI 每 1 秒读一次不可变快照；不得在 UI 线程做 socket/设备枚举。
- 任何跨线程状态都通过快照或队列传递；禁止在持锁时 await。

## 5. Android 端模块职责

| 模块（建议） | 必须负责 | 禁止负责 |
| --- | --- | --- |
| `MainActivity` / UI | 发现列表、手动 IP、开始/停止、状态与 RTT 存活显示 | 直接读 AudioRecord |
| `CaptureService` | 前台服务、MediaProjection 生命周期、通知、保活 | 组包/网络协议细节 |
| `AudioCapture` | `AudioPlaybackCaptureConfiguration` + `AudioRecord`，48k/16/2；必要时重采样/下混 | 发 UDP |
| `Packetizer` | 重分块 240 帧、seq、timestampUs、静音补齐、末包补 0、bit0 | 决定 PC 端行为 |
| `UdpTransport` | 单 socket 绑定 45678；发送 HELLO/START/STOP/PONG/AUDIO；接收 OFFER/PING；PONG 5 ms 内应答 | 采集/播放 |
| `Discovery` | OFFER 解析、按 IP 去重、6 秒老化、手动 IP | 会话状态 |
| `SessionController` | 开始/停止顺序、错误归因、与 UI 的状态同步 | 阻塞音频线程 |
| `Stats/Log` | 200 pps 统计、队列丢弃、最后 PONG 年龄；tag `BlueAudioSink` | 记录音频内容 |

**线程与队列（Android）**

- 主线程：UI/状态；采集线程：`AudioRecord.read` → 重分块；发送线程：从有界队列取包 → `sendto`；接收线程：OFFER/PING → 立即 PONG。
- 发送队列上限 **30 ms（6 包）**；溢出丢最旧未发送包，**seq 仍消耗**（见 02 §3），不得阻塞采集。
- 接收线程不得被音频发送阻塞；PONG 必须在 5 ms 内发出。
- 音频路径复用 `byte[]`/`ByteBuffer`，避免每包分配；日志不得在音频线程做磁盘 I/O。
- 前台服务类型必须包含 `mediaProjection`；Android 14 需 `FOREGROUND_SERVICE_MEDIA_PROJECTION` 权限与用户授权。

## 6. 数据流

### 6.1 采集 → 播放

```
MediaProjection（playback capture）
   │
   ▼
AudioRecord 48 kHz / 16-bit / 立体声
   │  可能一次读到任意帧数
   ▼
Packetizer：攒够 240 帧 → seq++ → timestampUs=首帧采样时刻
   │  加 16 B 头 → 976 B UDP 报文，200 包/秒
   ▼
UDP 45679 ──────────────► PC UdpAudio：长度/magic/type/端点校验
                              │
                              ▼
                        WifiController：会话/seq/缺口/统计
                              │
                              ▼
                        JitterBuffer：D、预缓冲、500 ms 调整
                              │
                              ▼
                        BufferedWaveProvider → WasapiOut → 默认输出
```

关键点：

- 手机**永远不发送半包**；采集不足 240 帧就攒，最后一包补 0。
- 会话期间**永远不停发**；没有采集数据就发全 0 静音包，保持 200 包/秒。
- PC 收到的 AUDIO 总长必须是 **976**；否则按 malformed 丢弃，不得送进缓冲。

### 6.2 时钟与延迟链

```
手机采样时刻 ts ──(offset = 手机时钟 − PC 时钟)──► PC 时间基准
                                                    │
PC 到达时刻 T3 ──► RTT = T3 − T1 ──► offset 估计      │
                                                    ▼
tailTs（队列尾采样时刻）− D = playheadTs ──► age = now − (playheadTs − offset)
                                                    │
                                                    ▼
                                       显示延迟 = age + 50 ms（仅展示）
```

- `offset` 只用于展示；抖动缓冲调度只依赖 D 与 02 §6。
- 所有时间差用 u32 模 2³²；32 位回绕（约 71.6 分钟）不得触发断流/重置。
- PC 不得用 `DateTime.Now` 参与协议计时。

### 6.3 控制与发现

```
PC --OFFER(2s, 广播)--> 手机发现列表 --用户选择--> 手机 HELLO --> START --> AUDIO
PC --PING(2s)--------> 手机 --PONG(≤5ms)--> PC：RTT/offset/存活
手机 --STOP----------> PC：排空 ≤500ms --> 待机（仍监听）
PC 超时 5s 无 AUDIO --> 待机（仍监听）
```

## 7. 会话时序

### 7.1 正常开始

```
手机                                          PC
 │ 用户点开始                                    │
 │ MediaProjection 授权                          │
 │ AudioRecord 创建成功                           │
 │── HELLO ────────────────────────────────────►│ 记录设备名/端点，开始 PING
 │── START(T_start) ───────────────────────────►│ 绑定端点、清队列、清统计
 │                                              │ 创建播放器、预缓冲 40 ms
 │── AUDIO(seq=0, 5 ms) ──────────────────────►│ D 增长
 │── AUDIO(seq=1..) ──────────────────────────►│ D ≥ 40 ms → 播放（Flowing）
 │◄──────────────────── PING ──────────────────│ 每 2 s
 │── PONG(T1 回填, T2) ───────────────────────►│ RTT/offset 更新
 │                    ...                       │ 每 500 ms 调整 D
```

状态：`Busy → Armed → LinkReady（收到 START/AUDIO，预缓冲中）→ Flowing（播放且未重缓冲）`。

### 7.2 正常停止

1. 手机停止采集 → 末包补 0 发送 → 发一次 STOP → 不再发 AUDIO。
2. PC 收 STOP → 进入排空：不再接受 AUDIO，播放队列剩余，D 到 0 或 500 ms 上限。
3. D = 0 后最多再等 50 ms（WasapiOut 输出缓冲，且总排空 ≤500 ms），然后释放播放器 → **Armed**（Wi-Fi 模式仍监听、仍 OFFER/PING）；蓝牙模式则回 Idle。
4. 手机 socket 保持打开，继续应答 PING；界面回待机。

### 7.3 超时

- 接收中连续 **5 秒** 无合法 AUDIO（PING/PONG 不算）→ 丢弃队列、释放播放器、回 Armed。
- 收到 START 后 5 秒无 AUDIO → 同样回 Armed。
- 排空超过 500 ms → 强制丢弃、回 Armed。
- 超时**不关闭** 45678/45679，不停止 OFFER/PING；下一次 START 可立即开始。

### 7.4 快速重启 / START 丢失

- 手机 STOP 后立刻 START：PC 收到 START 即丢弃旧队列，按新会话处理；旧包按 timestampUs < T_start−100 ms 丢弃。
- START 丢失：PC 在 Armed 收到首个 AUDIO 自动建会话，基线取该包 seq；不要求首包为 0。
- 新会话统计清零；旧端点 AUDIO 计 foreign。

### 7.5 丢包/抖动

- 缺口 d ≤ 5（M ≤ 25 ms）：补静音，不记断流。
- 缺口 6~10（25 ms < M ≤ 50 ms）：补静音，记 1 次断流。
- d > 10：当前包作为重置后的首包接受，清队列、重建基线并重缓冲；记 resets + 1 次断流，不计丢包。**不清会话统计**，只清抖动窗口。
- 抖动升高：下一次 500 ms 评估把 D 向 200 ms 抬；抖动回落：每 500 ms 降 5 ms。
- 欠载：补静音并重缓冲到 lastTarget，不清 seq 基线。

## 8. 模式切换时序

### 8.1 蓝牙 → Wi-Fi

```
[BT 活跃]
  │ 用户选 Wi-Fi
  ▼
[Switching] 禁用模式/连接按钮
  │ 1) 绑定 45678 + 45679（先不创建播放器）
  │    失败 → 关闭已绑定的 socket，保持 BT，提示“端口占用/权限”，结束
  │ 2) 释放 BT 连接（既有 ReleaseAsync，≤5 s；超时强制 Dispose）
  │ 3) 启动 OFFER（2 s）与 PING（2 s）定时器
  ▼
[Wi-Fi Armed]（无会话；等手机 START）
```

顺序理由：先绑定新资源，失败可无损失回滚；Wi-Fi 播放器直到 START 才创建，因此释放 BT 期间不会双重出声。

### 8.2 Wi-Fi → 蓝牙

```
[Wi-Fi 活跃/Armed]
  │ 用户选蓝牙
  ▼
[Switching]
  │ 1) 停止接受新 AUDIO；排空/释放 Wi-Fi 播放器
  │ 2) 关闭 45678/45679，停止 OFFER/PING
  │ 3) 走既有 BT 连接流程（StartAsync/OpenAsync + 退避）
  │    成功 → [BT Armed]
  │    失败 → [BT Faulted/Backoff]，模式仍为 BT；不自动切回 Wi-Fi
  ▼
[BT Armed] 或 [BT Faulted]
```

- 先停 Wi-Fi 输出再建 BT，保证任何时刻最多一个播放器。
- BT 失败不自动恢复 Wi-Fi；用户可再次切换（会重新绑定端口）。
- 手机侧在此期间收不到 PONG，6 秒后显示“PC 未响应”，属预期。
- 超时上限：**BT→Wi-Fi 10 秒**（绑定 + 释放 BT ≤5 秒 + 余量）；**Wi-Fi→BT 30 秒**（沿用既有 ArmTimeout 8 秒、OpenTimeout 15 秒、稳定观察 1.6 秒 + 余量）。超时才强制清理并进入 Faulted，日志记录。

### 8.3 不变量

1. 同一时刻只有一个模式持有播放器；切换**完成后**，另一个模式的 socket/定时器必须已关闭。过渡期允许旧模式资源暂存（用于失败回滚），但禁止启动第二路播放。
2. OFFER/PING 只在 Wi-Fi 模式运行；BT 模式不广播。
3. 切换期间 UI 状态为 Busy，禁止重复点击；命令串行执行。
4. 切换失败必须回滚到“切换前模式”或明确进入 Faulted，不允许“两模式各半”。
5. 手机始终是会话发起方；PC 不主动开始/停止手机采集。

## 9. 状态模型与 UI 映射

复用 `SinkState`，两种模式语义如下：

| SinkState | 蓝牙模式 | Wi-Fi 模式 |
| --- | --- | --- |
| Idle | 未连接/已断开 | 模式关闭（切到 BT 或退出） |
| Busy | StartAsync/OpenAsync 中 | 绑定端口/创建播放器/切换中 |
| Armed | 控制层就绪，等待手机音频 | 端口已绑定、OFFER/PING 运行、无会话 |
| LinkReady | A2DP 端点 Active，但无峰值 | 已收到 START/首包，预缓冲或重缓冲中 |
| Flowing | 峰值 > 阈值 | 播放已启动且未重缓冲（静音也算 Flowing） |
| Faulted | 建链失败/重试耗尽 | 绑定/播放器/参数致命错误；丢弃 AUDIO，等 START 重试 |
| Backoff | 退避重试等待 | 不用于 Wi-Fi；端口/播放器失败直接 Faulted |

补充：

- Wi-Fi 的 STOP/超时结束后回 **Armed**，不是 Idle；只有关闭 Wi-Fi 模式才回 Idle。
- Wi-Fi 的 Flowing **不能**用峰值判定；手机播放静音时峰值恒 0，但传输正常。
- Wi-Fi 的 LinkReady 是短暂过渡（预缓冲 40 ms），不是错误态。
- `SinkSnapshot` 需增加：`Mode`、`PhoneName`、`PhoneEndpoint`、`RttMs`、`BufferMs`、`TargetMs`、`LossPercent`、`Underruns`、`Discontinuities`。未知值用 `null`/"--"。
- UI 每 1 秒刷新；状态变化立即刷新。数字四舍五入：RTT/缓冲为整数 ms，丢包率 1 位小数。
- 蓝牙文案与行为保持现状，不因 Wi-Fi 改动而回归。

## 10. 失败与恢复路径

| 故障 | 检测信号 | 自动动作 | 用户可见 | 日志事件 |
| --- | --- | --- | --- | --- |
| PC 45678/45679 绑定失败 | bind 异常 | 不启动 OFFER/PING；Faulted；**不自动重试**，避免死循环 | “端口被占用/无权限，请关闭占用程序或换端口后重试” | `Wifi.BindFailed` |
| PC 播放器创建失败（START 时） | WasapiOut 初始化异常 | 丢弃本会话 AUDIO；Faulted；停止 PING；等下一次 START 重试 | “音频输出不可用，请检查默认输出设备后重试” | `Player.Fault` |
| PC 播放中设备被拔/失效 | WasapiOut 异常/停止 | 释放播放器、清队列、回 Armed；下一次 AUDIO 自动建会话并重建播放器 | 短暂断音；恢复后继续 | `Player.Fault`、`Wifi.Reset` |
| PC 5 秒无 AUDIO | 定时器 | 释放播放器、回 Armed；socket/OFFER 保留 | “等待手机发送音频” | `Wifi.Timeout` |
| PC socket 接收异常 | 异常/错误码 | 关闭该 socket；Faulted；不自动重试 | “网络接收失败，请重新选择 Wi-Fi 模式” | `Wifi.Fault` |
| 输出格式不支持 48k/16/2 | 初始化失败 | 先尝试转换为设备混音格式；仍失败则 Faulted | “输出设备不支持该格式” | `Player.Fault` |
| 手机 45678 绑定失败 | bind 异常 | 拒绝开始；不建采集；不置“已连接” | “端口 45678 被占用，请关闭其他实例” | `Wifi.Fault` |
| MediaProjection 拒绝 | 授权回调 | 不创建 AudioRecord；不 START | “需要屏幕/音频捕获授权” | `Capture.Fault` |
| AudioRecord 创建失败（START 前） | 异常 | 不 START；释放资源 | “无法开始采集” | `Capture.Fault` |
| AudioRecord 失败（START 后） | 读异常 | 发一次 STOP，停止发送 | “采集中断，已停止” | `Capture.Fault`、`Wifi.StopSent` |
| 手机 App 被杀/后台清理 | PC 5 秒无 AUDIO | PC 回 Armed；手机无日志 | PC“等待手机发送音频” | `Wifi.Timeout` |
| 手机换 Wi-Fi/IP 变化 | 发送失败/新源地址 | 停止旧会话，重建 socket，重新 HELLO/START；PC 旧端点超时，新 START 抢占 | 手机提示“网络已变化，请重试” | `Wifi.Fault`、`Wifi.Start` |
| 网络丢包/抖动 | seq 缺口、J 升高 | 补静音/重置；D 向目标调整；重缓冲 | 短暂卡顿/断流提示 | `Audio.Gap`、`Audio.Underrun`、`Audio.Rebuffer` |
| 手机发送队列溢出 | 队列长度 > 6 | 丢最旧未发送包，seq 仍消耗；PC 按缺口补静音 | 通常不可感知 | `Send.QueueDrop` |
| 多手机先后 START | 新源 START | 新会话抢占；旧端点 AUDIO 计 foreign | 只有后启动者出声 | `Wifi.Start`、`foreign` |
| 模式切换失败 | 绑定/建链异常 | 按 §8 回滚或 Faulted；不双模式并行 | 提示失败原因 | `Wifi.ModeOn/Off`、`Wifi.Fault` |
| 防火墙/AP 隔离 | 无 OFFER/无 AUDIO | PC 保持 Armed；45678 被挡则手机发现不到/收不到 PONG，仅 45679 被挡则能发现但 5 秒超时 | 手机“找不到电脑”或“PC 未响应”；PC“等待手机发送音频” | `Wifi.OfferSent` 仍在，无 `Wifi.Start` |
| 蓝牙失败 | 既有错误码 | 既有 Backoff/Faulted 逻辑 | 既有文案 | 既有事件 |

**恢复原则：**

1. Wi-Fi 会话的发起权永远在手机；PC 只回 Armed 等待，不主动“重连”手机。
2. 只有 START 能修复 Faulted；Auto-start 只允许从 Armed 状态发生。
3. 蓝牙保持既有退避策略，不被 Wi-Fi 改动。
4. 任何自动动作都必须有次数/时间上限，禁止无限重试刷屏。

## 11. 可观测性与日志

### 11.1 日志通道

- PC：`%LocalAppData%\BlueAudioSink\logs\sink-YYYYMMDD.jsonl`，每行一个 JSON；字段至少 `ts/level/event`，其余按事件附字段。
- 手机：Logcat tag **`BlueAudioSink`**，格式 `事件名 key=value ...`；建议同时落一份应用内日志文件（可选）。
- 两端事件名必须一致，便于联调时并排比对。日志不得记录 PCM 内容、设备唯一标识等隐私数据。

### 11.2 事件词表（最低要求）

| 端 | 事件 | 关键字段 | 时机 |
| --- | --- | --- | --- |
| PC | `Wifi.ModeOn` / `Wifi.ModeOff` | mode | 模式切换完成 |
| PC | `Wifi.Bind` / `Wifi.BindFailed` | ports / error | 绑定 45678/45679 |
| PC | `Wifi.OfferSent` | name, audioPort | 首次 + 每 30 秒 |
| PC | `Wifi.Hello` | name, endpoint | 收到 HELLO |
| PC | `Wifi.Start` | endpoint, ts | 收到 START/自动建会话 |
| PC | `Wifi.FirstAudio` | seq, ts | 会话首包 |
| PC | `Wifi.Playing` | bufferMs | 预缓冲完成、开始播放 |
| PC | `Wifi.Stop` / `Wifi.DrainDone` | ts / drainedMs | STOP / 排空结束 |
| PC | `Wifi.Timeout` | silentMs | 5 秒无 AUDIO |
| PC | `Wifi.Reset` | oldSeq, newSeq | d > 10 重置 |
| PC | `Wifi.Fault` | code, detail | 端口/播放器/参数故障 |
| PC | `Player.Created` / `Player.Released` / `Player.Fault` | device / code | 播放器生命周期 |
| PC | `Pong.Recv` | rttMs, offsetMs | 每个有效 PONG |
| PC | `Audio.Gap` | missing, seq | 每次缺口 |
| PC | `Audio.Underrun` / `Audio.Rebuffer` | count / bufferMs | 欠载/重缓冲 |
| PC | `Packet.Malformed` | count, reason | 校验失败（按 1 秒聚合） |
| PC | `Wifi.Stats` | 见 11.3 | 每 5 秒 |
| 手机 | `Wifi.Discover` | pcName, pcIp | 收到 OFFER/更新列表 |
| 手机 | `Wifi.Select` | pcIp | 用户选择 PC |
| 手机 | `Capture.Start` / `Capture.Stop` / `Capture.Fault` | code | 采集生命周期 |
| 手机 | `Wifi.StartSent` / `Wifi.StopSent` | ts | 发送 START/STOP |
| 手机 | `Ping.Recv` / `Pong.Sent` | echo | 收到 PING/发出 PONG |
| 手机 | `Wifi.Stats` | sent, seq, dropped, lastPongAgeMs | 每 5 秒 |
| 手机 | `Send.QueueDrop` | count | 发送队列溢出 |
| 手机 | `Wifi.Fault` | code | 端口/采集/网络故障 |

高频事件（`Audio.Gap`、`Audio.Underrun`、`Send.QueueDrop`、`Packet.Malformed`）必须按 1 秒窗口聚合后记录，字段写累计值；不得每个丢包/欠载都写一行，避免日志在弱网下爆量。

### 11.3 `Wifi.Stats` 字段（PC）

`state, rttMs, offsetMs, bufferMs, targetMs, lossPct, received, missing, dup, late, resets, underruns, discontinuities, malformed, foreign, stale, unknown`（`targetMs` 即 lastTarget）

- `lossPct` 按最近 10 秒窗口计算（02 §7.5）；窗口无包时为 `null`。
- `received/missing` 为窗口内计数；其余为会话累计（`malformed`/`foreign`/`unknown` 全局累计）。
- 每 5 秒一条；状态变化时可额外立即输出一条。

## 12. 已知约束与风险

1. **包长/MTU**：5 ms / 240 帧 / 960 字节 / 总长 976；IP 层 1004 字节 < 1500，不分片。这是硬约束，任何一端不得擅自改包长；未来若改，必须同步两端并重新评估 MTU。
2. **包率/开销**：200 包/秒；有效载荷 1.536 Mbps，加 16 字节/包开销约 1.56 Mbps；手机 `sendto` 与 PC `recvfrom` 的 CPU 开销可忽略，但日志/锁竞争必须避免。
3. **Android 14 限制**：MediaProjection 必须配合 `mediaProjection` 类型前台服务与相应权限；每次采集都需要有效的 MediaProjection 授权令牌（是否重新弹窗由系统决定），因此 START 一定在授权成功之后。
4. **播放捕获策略**：部分 App 可设置 `ALLOW_CAPTURE_BY_NONE`；被禁止时手机只能发出静音，PC 无法区分“源静音”与“被禁止”。预验证已确认网易云可捕获（见 06）。
5. **后台保活**：MIUI/HyperOS 可能清理后台；必须前台服务 + 电池白名单；被清理时 PC 5 秒超时。
6. **Wi-Fi 省电/干扰**：省电模式与 2.4G 干扰会造成 100 ms 级抖动；由 J 驱动 D 上升吸收，不要用固定大缓冲硬扛。
7. **时钟漂移**：两端 48 kHz 晶振不同源；长时间运行 D 会缓慢漂移，由 500 ms 调整与重缓冲吸收；32 位微秒每 71.6 分钟回绕，必须用 u32 差值。
8. **Wasapi 共享模式格式**：PC 固定以 48k/16/2 打开；若设备混音格式不同，由 PC 转换；转换失败按 §10 处理。
9. **默认输出变化**：Wi-Fi 播放器在会话开始时绑定输出；会话中不跟随系统默认变化；下一次会话重新绑定。
10. **无加密/无鉴权**：v1 仅限可信局域网；不得暴露到公网；防火墙只放行专用网络。
11. **端口冲突**：PC 45678/45679、手机 45678 都可能被其他程序占用；启动时必须显式报错，禁止静默失败。
12. **多手机**：最后一个 START 抢占；不做混音；旧手机 AUDIO 计 foreign。
13. **IPv4 only**：广播依赖 IPv4；跨网段/AP 隔离时只能手动 IP；手动模式不依赖 OFFER。
14. **手机内存/CPU**：目标 ≤100 MB；音频路径零分配、发送队列 30 ms、日志限频；避免每包创建对象。
15. **协议版本**：线格式变更必须提升版本并同步两端；v1 不做版本协商。
16. **v1 没有音频接收 ACK**：手机只能通过 PONG 判断 PC 进程在线，不能判断 AUDIO 是否到达 PC。若只有 45679 被防火墙挡住，PC 会 5 秒超时，而手机仍可能显示“PC 在线”；这是 v1 的已知限制，联调时以 PC 日志/状态为准。

## 13. 与其它文档的关系

| 文档 | 定位 | 变更规则 |
| --- | --- | --- |
| `00-overview.md` | 目标、目录、验收基线 | 项目级变更 |
| `01-architecture.md`（本文） | 共享结构、状态、模式切换、失败恢复、可观测性 | 影响两端行为时先改本文 |
| `02-protocol.md` | 线格式与唯一契约 | **任何字段/长度/端口/阈值变更必须先改本文** |
| `03-pc-implementation.md` | PC 端实现细节 | 不违反 01/02 |
| `04-android-implementation.md` | Android 端实现细节 | 不违反 01/02 |
| `05-testing-acceptance.md` | 测试与验收 | 引用 02 §12 自检表 |
| `06-preroll-report.md` | 预验证实测 | 只增不改结论 |

并行开发约定：

1. 协议问题以 `02` 为准；架构/状态/失败路径问题以本文为准；两者冲突时先报告主 agent，不得各写一套。
2. 任何“实现更方便但和文档不同”的改动，先改文档并通知另一端，再改代码。
3. 联调前两端各自跑 `02 §12` 自检表；不通过不得进入验收。

## 附录 A：无手机时的回环注入脚本（PC 单端自测）

> 用途：在没有手机的情况下验证 PC 的 START/seq/缺口/断流/长度校验。
> 前提：PC 已切到 Wi-Fi 模式并绑定 45678/45679；在普通 PowerShell 7 中执行。
> 所有报文从**同一个** UDP socket 发出，保证源端口一致、属于同一会话。

```powershell
$ErrorActionPreference = 'Stop'
$script:Udp = [Net.Sockets.UdpClient]::new()

function Send-BasPacket {
    param(
        [byte]$Type,
        [uint32]$Seq,
        [uint32]$Ts,
        [byte[]]$Payload = [byte[]]@(),
        [int]$Port
    )
    $p = New-Object byte[] (16 + $Payload.Length)
    [Text.Encoding]::ASCII.GetBytes('BAS1').CopyTo($p, 0)
    $p[4] = $Type
    [BitConverter]::GetBytes([uint32]$Seq).CopyTo($p, 8)
    [BitConverter]::GetBytes([uint32]$Ts).CopyTo($p, 12)
    if ($Payload.Length -gt 0) { $Payload.CopyTo($p, 16) }
    [void]$script:Udp.Send($p, $p.Length, '127.0.0.1', $Port)
}

# 1) START（发到控制端口 45678）
Send-BasPacket -Type 2 -Seq 0 -Ts 1000000 -Port 45678

# 2) 10 个 5 ms AUDIO 包（960 字节），seq 0..4、12..16，制造 7 包缺口（35 ms）
$audio = [byte[]]::new(960)
foreach ($i in 0..4) {
    Send-BasPacket -Type 1 -Seq ([uint32]$i) -Ts ([uint32](1000000 + $i * 5000)) -Payload $audio -Port 45679
}
foreach ($i in 12..16) {
    Send-BasPacket -Type 1 -Seq ([uint32]$i) -Ts ([uint32](1000000 + $i * 5000)) -Payload $audio -Port 45679
}

# 给 PC 300 ms 完成预缓冲/播放，再发 STOP（否则可能来不及进入 Playing）
Start-Sleep -Milliseconds 300

# 3) 非法 magic（应计 malformed，不崩溃）
$bad = [byte[]]::new(16)
[Text.Encoding]::ASCII.GetBytes('XXXX').CopyTo($bad, 0)
$bad[4] = 1
[void]$script:Udp.Send($bad, $bad.Length, '127.0.0.1', 45679)

# 4) STOP（排空后回 Armed）
Send-BasPacket -Type 3 -Seq 0 -Ts 1100000 -Port 45678

$script:Udp.Dispose()
```

预期：PC 日志依次出现 `Wifi.Start`、`Wifi.FirstAudio`、`Wifi.Playing`、`Audio.Gap`（missing=7）、`Wifi.Stats` 的 `missing` 增长、`Wifi.Stop`、`Wifi.DrainDone`；状态回“待机（监听中）”，无崩溃。若 PC 只绑定了特定网卡而非 `0.0.0.0`，把 `127.0.0.1` 换成该网卡 IP。
