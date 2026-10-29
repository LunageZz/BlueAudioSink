# Android 端实现（Wi-Fi 发送端）

> 本文记录 `android/` 工程的结构、采集链、协议实现、UI、构建与验证方法。
> 协议以 `docs/02-protocol.md`（v1.1）为准；本文只描述实现细节与实测结论。

## 0. 交付信息（替换自检表占位符）

| 项 | 值 |
| --- | --- |
| **应用包名** | `com.blueaudiosink.sender` |
| 应用名 | 蓝音频 · 发送端 |
| APK 产物 | `android/app/build/outputs/apk/debug/app-debug.apk` |
| 启动 Activity | `com.blueaudiosink.sender/.MainActivity` |
| 前台服务 | `com.blueaudiosink.sender/.capture.AudioCaptureService` |

> 自检表里的 `<包名>` 一律替换为 `com.blueaudiosink.sender`；
> 例如 `adb shell dumpsys package com.blueaudiosink.sender`、
> `adb install -r -t android/app/build/outputs/apk/debug/app-debug.apk`。

## 1. 工程结构

```
android/
├─ settings.gradle.kts          # 仓库声明（google / mavenCentral）
├─ build.gradle.kts             # 插件版本：AGP 8.10.1、Kotlin 2.1.21、Compose 编译器插件
├─ gradle.properties            # AndroidX、并行、缓存
├─ local.properties             # sdk.dir=D:\Android\SDK（已被 .gitignore 忽略）
├─ gradlew / gradlew.bat        # Gradle 8.11.1 wrapper（已生成并验证）
├─ gradle/wrapper/…
└─ app/
   ├─ build.gradle.kts          # minSdk 29 / targetSdk 34 / compileSdk 35，Compose
   └─ src/main/
      ├─ AndroidManifest.xml
      ├─ res/values/strings.xml # 全部中文文案
      ├─ res/values/themes.xml
      ├─ res/drawable/…         # 启动图标、通知小图标
      └─ java/com/blueaudiosink/sender/
         ├─ MainActivity.kt             # 单 Activity：权限 → 授权 → 启动前台服务
         ├─ core/
         │  ├─ LinkController.kt        # 应用级连接状态 + 唯一 UDP 通道入口
         │  └─ UiState.kt               # SenderUiState / LinkPhase
         ├─ capture/
         │  ├─ MediaProjectionHelper.kt # 运行时权限、录屏授权 Intent
         │  ├─ AudioCaptureService.kt   # 前台服务（mediaProjection）
         │  ├─ PcmRecorder.kt           # AudioRecord + 环形缓冲 + 5 ms 定拍发送
         │  └─ BufferTier.kt            # 低延迟 / 平衡 / 稳定
         ├─ net/
         │  ├─ PacketCodec.kt           # 16 字节包头、类型、端口、音频常量
         │  ├─ UdpSender.kt             # 唯一 socket（45678）：收 OFFER/PING，发 PONG/HELLO/START/STOP/AUDIO
         │  └─ DiscoveryClient.kt       # OFFER 设备表 + 6 秒过期
         └─ ui/
            ├─ SenderScreen.kt          # 单页 Compose 界面
            └─ Theme.kt                 # Material3 主题
```

## 2. 构建环境与命令

| 项 | 值 |
| --- | --- |
| Gradle | **8.11.1**（wrapper 固定，避免 Gradle 9.4.1 与 AGP 不兼容） |
| AGP | **8.10.1**（要求 Gradle ≥ 8.11.1，已与本机缓存匹配） |
| Kotlin | 2.1.21 + `org.jetbrains.kotlin.plugin.compose` |
| JDK | 21（`D:\Android\Android Studio\jbr` 或 `D:\Java21\jdk-21.0.10`） |
| Android SDK | `D:\Android\SDK`（compileSdk 35；build-tools 35.0.0） |

```powershell
cd android
.\gradlew.bat :app:assembleDebug          # 产物：app/build/outputs/apk/debug/app-debug.apk
.\gradlew.bat :app:installDebug           # 可选：直接装到连接中的设备
```

### 2.1 本机环境的两处坑（只影响构建机，不影响产物）

1. **`GRADLE_USER_HOME` 被设为 `D:\apache-maven-3.9.14-bin\repository`**。
   该目录下 `wrapper/dists/gradle-8.11.1-bin` 曾下载中断（只剩 `.part`），
   wrapper 会重新联网下载，而 `services.gradle.org` 会 302 到 `github.com`（本机不可达）→ 构建失败。
   解决：把 `~/.gradle/wrapper/dists/gradle-8.11.1-bin/<hash>` 下已解压的发行版 + `.ok` 标记
   复制到该目录，之后完全离线复用。
2. **MIUI 的 USB 安装确认**：`adb install` 会在手机上弹「USB安装提示」，
   需要点「继续安装」；否则报 `INSTALL_FAILED_USER_RESTRICTED`。

## 3. 权限

| 权限 | 用途 |
| --- | --- |
| `INTERNET` | UDP 收发 |
| `ACCESS_NETWORK_STATE` / `ACCESS_WIFI_STATE` | 网络/Wi-Fi 状态 |
| `CHANGE_WIFI_MULTICAST_STATE` | 收 OFFER 广播时持有 MulticastLock（省电/息屏下仍可发现） |
| `FOREGROUND_SERVICE` + `FOREGROUND_SERVICE_MEDIA_PROJECTION` | `mediaProjection` 类型前台服务（Android 14 硬要求） |
| `POST_NOTIFICATIONS` | 前台服务通知（Android 13+ 运行时权限） |
| `RECORD_AUDIO` | **内录必需**：`AudioPlaybackCapture` 要求持有录音权限（运行时权限） |

> 界面只在你点「开始发送」时才申请运行时权限，然后才弹系统录屏授权对话框。

## 4. 采集链

```
用户点开始
  └─ MainActivity：运行时权限（RECORD_AUDIO / POST_NOTIFICATIONS）
      └─ 系统录屏授权对话框（每次都必须确认，Token 一次性）
          └─ startForegroundService(AudioCaptureService, resultCode, data)
              ├─ startForeground(id, notif, FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)   ← 必须先于 getMediaProjection
              ├─ getMediaProjection(resultCode, data) + registerCallback
              ├─ PcmRecorder.start()
              │    ├─ AudioPlaybackCaptureConfiguration(USAGE_MEDIA, USAGE_GAME)
              │    ├─ AudioRecord：48 kHz / 16 bit / 立体声，内部 buffer = max(2×minBufferSize, 4×960)
              │    ├─ 采集线程：READ_BLOCKING 攒满 240 帧（960 B），写环形缓冲（带首帧时间戳）
              │    └─ 发送线程：单调时钟定拍，每 5 ms 出一个整包；无数据补全 0 静音包
              └─ LinkController.startStreaming()：HELLO + START，seq 归零
```

关键点：

- **先 `startForeground` 再 `getMediaProjection`**（Android 14 要求）。启动新会话时只清理旧采集对象
  （`releaseProjection()`），**绝不能顺手 `stopForeground`**：否则会丢掉 mediaProjection 前台类型，
  `getMediaProjection` 抛 `SecurityException: Media projections require a foreground service of type …`
  （本机已踩过，见 `AudioCaptureService` 注释）。
- **200 包/秒是硬节拍**：发送线程用 `SystemClock.elapsedRealtimeNanos` + `LockSupport.parkNanos`
  按 5 ms 绝对节拍出包；环形缓冲为空时补全 0 静音包，**不降频、不停发**。
- **分包**：只发 240 帧 / 960 字节整包；短读用 0 补齐到 960 字节，绝不发半包。
- **timestampUs = 包内第 0 帧的采样时刻**（不是发送时刻、也不是读取完成时刻）：
  采集线程每次读满 960 B 后调用 `AudioRecord.getTimestamp(TIMEBASE_MONOTONIC)` 拿到
  「帧号 ↔ 单调时钟」锚点，反推首帧时间 `anchorTime − (anchorFrame − firstFrame)/48000`；
  平台不支持或结果明显不合理（与本地时钟偏差 > 200 ms）时退回「读到整包时刻 − 5 ms」。
- **时间轴平滑**（`PcmRecorder.smoothTimestampUs`）：以「上一包 + 5 ms」为基准，用硬件首帧时间
  做**每包最多 1 ms** 的缓慢校正，偏差 > 100 ms 的锚点直接丢弃。原因：`getTimestamp` 的锚点
  偶发数十毫秒跳变，原始值直接上送会让 PC 的 offset/抖动估计失真（实测未平滑时 ts 间隔
  出现 4 µs ~ 27.5 ms 的毛刺）。平滑后实测 ts 间隔稳定在 **4000–6000 µs，均值 4999.1**。
- **静音包时间戳 = 上一包 + 5000 µs**（与协议 v1.1 一致），平滑逻辑对静音包即返回该值。
- **零拷贝**：整链只有两个 960 B 缓冲（采集 chunk、出包 outBuffer）和一个 976 B 发送帧缓冲，
  每包不产生新对象，内存占用稳定。

### 4.1 缓冲档位

档位调的是「预填充深度」（发送前先在环形缓冲里攒多少音频），直接体现为手机侧排队延迟：

| 档位 | 预填充 | 说明 |
| --- | --- | --- |
| 低延迟 | 2 包（10 ms） | 延迟最低，采集线程被调度延迟时更容易补静音 |
| 平衡 | 5 包（25 ms） | 默认 |
| 稳定 | 10 包（50 ms） | 最抗抖动，端到端延迟更高 |

PC 端自适应抖动缓冲（协议第 6 节）会在此基础上再叠加，因此档位只是「手机侧预留」。

## 5. 协议实现（逐字段核对 `docs/02-protocol.md` v1.1）

### 5.1 统一包头（16 字节，小端）

| 偏移 | 字段 | 实现 |
| --- | --- | --- |
| 0 | magic `"BAS1"` | `0x42 0x41 0x53 0x31`（`PacketCodec.writeHeader`） |
| 4 | type u8 | 1 AUDIO / 2 START / 3 STOP / 4 PING / 5 PONG / 6 OFFER / 7 HELLO |
| 5 | flags u8 | bit0 = 内容不连续，**仅手机置位** |
| 6 | reserved u16 | 恒 0 |
| 8 | seq u32 | AUDIO 从 0 递增；其余报文填 0 |
| 12 | timestampUs u32 | AUDIO = 首帧采样时刻微秒低 32 位；PONG = 手机回包时刻；其余填 0 |

### 5.2 音频包

- 采样率 **48000**、**16 bit**、**立体声**、**240 帧 = 960 字节 payload**、总长 **976 字节**（IP 层 1004，不分片）
- 包速率 **200 包/秒**，带宽约 1.55 Mbps
- START 后 seq 从 0 连续递增；PC 端 5 秒无包自动待机

### 5.3 单 socket 架构

手机只建 **一个** UDP socket，绑定 `0.0.0.0:45678`，发现/控制/音频同源端口：

| 方向 | 报文 | 目标 |
| --- | --- | --- |
| 收 | OFFER（广播） | 解析设备名 + 音频端口，进设备列表（6 秒过期） |
| 收 | PING（PC 发起） | 取 header.timestampUs 作为 T1 |
| 发 | PONG | payload 4 字节 = **原样回填 T1**；header.timestampUs = 手机当前单调时钟 T2；seq=0；发回 PING 的源地址:源端口 |
| 发 | HELLO / START / STOP / AUDIO | 目标 = OFFER 携带的音频端口（缺省 45679），源端口恒为 45678 |

- 绑定失败统一提示「**端口 45678 被占用**」。
- **PING 由 PC 发起**，手机不主动 PING；START/STOP 不重传（START 靠首个 AUDIO 兜底，STOP 靠 PC 5 秒超时兜底）。
- 所有 send 都在后台线程执行（控制包走单线程 `controlExecutor`，音频走出包线程）：
  Android 禁止主线程做 socket 收发，否则抛 `NetworkOnMainThreadException`（本机已踩过）。
- `START` 真正发出后才打开 AUDIO 闸门，保证「START 一定先于第一个 AUDIO」。

### 5.4 v1.1 五点对齐清单

| # | 要求 | 实现位置 | 状态 |
| --- | --- | --- | --- |
| 1 | 会话期间保持 200 包/秒，无数据补全 0 静音包，静音包 ts = 上一包 + 5000 | `PcmRecorder.sendLoop` | ✅ |
| 2 | 只发 240 帧 / 960 字节，不足补齐，不发半包 | `PcmRecorder.captureLoop`（READ_BLOCKING 攒满 + 短读补 0） | ✅ |
| 3 | 单 socket 绑定 `0.0.0.0:45678`，全部报文同源；失败提示「端口 45678 被占用」 | `UdpSender.open` | ✅ |
| 4 | PC 每 2s 发 PING；手机回 PONG（payload=T1 原样，ts=T2，seq=0） | `UdpSender.handlePing` | ✅ |
| 5 | 文档写明实际包名 | 本文第 0 节：`com.blueaudiosink.sender` | ✅ |

## 6. UI 结构（单页，全中文）

| 区块 | 内容 |
| --- | --- |
| 顶部栏 | 「蓝音频 · 发送端」 |
| 状态卡 | 未连接（灰）/ 已连接（绿）/ 发送中（蓝）+ 目标电脑 + 发送中显示包数 |
| 目标电脑 | 发现列表（点选即连接，显示 `名称 / IP:端口`）+ 手动输入 IP + 连接按钮 + 刷新 |
| 缓冲档位 | Material3 分段按钮：低延迟 / 平衡 / 稳定 + 当前档位说明 |
| 主按钮 | 开始发送（蓝）/ 停止发送（红），高 72 dp |
| 底部指标 | 实时延迟「见电脑端」/ 丢包率 / 已发送，底部导航栏安全区内 |
| 提示 | 录屏授权每次需手动确认；PING 由电脑端发起，RTT 在电脑端测量 |

- 字号全部 `sp`、间距全部 `dp`，Compose 按系统 DPI/字体缩放自适应；深色模式跟随系统。
- 状态由 `LinkController.state: StateFlow<SenderUiState>` 驱动，Activity 与服务同进程共享。

## 7. 验证方法（本机实测）

### 7.1 构建与安装

```powershell
cd android
.\gradlew.bat :app:assembleDebug     # BUILD SUCCESSFUL
adb install -r -t app\build\outputs\apk\debug\app-debug.apk
adb shell am start -n com.blueaudiosink.sender/.MainActivity
```

### 7.2 协议抓包（PC 侧临时 Python 探针，不入库）

在 PC 上绑一个端口（本机 45678/45679 被占用，测试用 46000）：

1. 每秒向 `255.255.255.255:45678` + 手机单播发 OFFER（设备名 + `0x00` + 端口）；
2. 每 2 秒向 `手机IP:45678` 发 PING（T1 放在 header.timestampUs）；
3. 校验：PONG 是否原样回填 T1、AUDIO 是否 976 字节 / seq 连续 / ts 间隔 5000 / 速率 200 pps。

## 8. 实测结果（Redmi K70 Ultra / Android 14 / 192.168.0.116 ↔ PC 192.168.0.165）

| 检查项 | 结果 |
| --- | --- |
| Debug 构建 | ✅ BUILD SUCCESSFUL（Gradle 8.11.1 + AGP 8.10.1 + JDK 21） |
| 安装 | ✅ MIUI 弹「USB安装提示」→ 继续安装后 Success |
| 启动 / 界面 | ✅ 中文单页正常渲染；未连接 → 已连接 → 发送中三态切换正常 |
| OFFER 发现 | ✅ 广播与单播均能发现，显示 `DSH-TEST-PC 192.168.0.165:46000` |
| HELLO | ✅ `len=26`（16 + 10 字节机型名），源端口 45678 |
| START | ✅ `len=16, seq=0, ts=0`，先于首个 AUDIO |
| AUDIO | ✅ 每包 `976` 字节、seq 从 0 连续、源端口 45678 |
| 发送速率 | ✅ 24.94 s 发 4989 包 = **200.0 包/秒**（另两次：31.4 s / 6279 包 ≈ 200.1、19 s / 3759 包 ≈ 198） |
| seq 连续性 | ✅ 0 → 4988 连续，**0 次跳变** |
| timestampUs 间隔 | ✅ 4000–6000 µs，均值 **4999.1**（5000 ± 1 ms 的平滑窗口） |
| flags.bit0 | ✅ 4989 包中 **0 次**置位（采集全程有数据，未补静音） |
| PONG | ✅ PC 每次 PING 都收到 PONG；`payload == T1` 逐包一致；header.timestampUs 单调递增 |
| STOP | ✅ `len=16`；界面回到「已连接」，前台通知移除，服务结束 |
| 前台服务类型 | ✅ `dumpsys activity services`：`types=00000020`（mediaProjection） |
| 应用包名 | `com.blueaudiosink.sender`（自检表 `<包名>` 占位按此替换） |
| 录屏授权 | ✅ 每次开始都弹系统「开始录制或投放」对话框，授权后正常出流 |

> 探针日志里 PC 自算的 RTT 数值是脚本 bug（32 位截断），与 App 无关；
> PONG 的 T1 回填、时间戳单调性均已逐包核对。

### 8.1 内存

- 无队列、无每包分配；两个 960 B 缓冲 + 一个 976 B 发送缓冲 + 一个 2 KB 收包缓冲。
- Android 自身（Compose + MediaProjection）约 40–60 MB，满足 ≤ 100 MB 目标（长跑未压测）。

## 9. 已知限制与风险

1. **手机端不显示 RTT**（主 agent 已裁决 MVP 不扩展协议）：PING 由 PC 发起，手机只回 PONG，
   拿不到 T3，无法计算 RTT。底部改为「实时延迟 · 见电脑端」，避免被误读为故障。
   后续若要显示，可在协议预留的 reserved/flags 位上加 PC→手机回传。
2. **丢包率是控制通道口径**：PC 不发音频，手机无法统计 AUDIO 丢包；界面显示的是 PING 丢失率
   （间隔 > 2.5 s 视为丢一次），真正的 AUDIO 丢包率应在 PC 端统计。
3. **时钟漂移未校正**：时间戳锚定在 `AudioRecord.getTimestamp` 上，长时间（>30 min）仍可能
   与 PC 时钟缓慢偏离；PC 自适应缓冲可吸收，但需长跑验证。
4. **MIUI/HyperOS 后台限制**：前台服务 + 通知已保活；极端省电策略仍可能杀掉服务，需真机长跑观察。
5. **MediaProjection 每次都要授权**：系统限制，无法跳过；停止后再次开始必须重新点确认。
6. **内录范围**：只匹配 `USAGE_MEDIA` 与 `USAGE_GAME`；通话、闹钟、以及做了 capture policy
   禁止的 App（部分版权内容）不会被收录。
7. **端口固定**：手机必须能绑定 45678；被占用时提示「端口 45678 被占用」。
8. **只支持 IPv4**：发现与单播均按 IPv4 处理（OFFER 广播本身即 IPv4）。
9. **未做 30 分钟长跑与 PC 端播放联调**：本次只验证协议字段与 31 秒连续发送；
   端到端延迟、断点、播放侧丢包需与 PC 端一起测（见 `docs/05-testing-acceptance.md`）。