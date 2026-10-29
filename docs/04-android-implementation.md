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
         │  ├─ SavedDeviceStore.kt      # 已连接设备记忆（SharedPreferences + JSON）
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
            ├─ SenderScreen.kt          # 单页 Compose 界面（状态卡 / 设备列表 / 档位 / 主按钮 / 底部指标）
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

## 6. UI 结构（单页，全中文，只保留必要文案）

设计原则：**界面上不出现超过一行的解释性文字**。长句说明全部删除，必要信息只用短标签 + 数值表达
（档位只有「低延迟 / 平衡 / 稳定」三个词；底部只有两个数字）。

| 区块 | 内容 |
| --- | --- |
| 顶部栏 | 「蓝音频 · 发送端」 |
| 状态卡 | 一行：状态点 + 未连接（灰）/ 已连接（绿）/ 发送中（蓝）+ 当前设备名（未选设备时留空） |
| 设备列表 | 已保存设备在前（最近使用的一台带「上次使用」标记，✕ 可删除），广播发现到的设备在后；一台都没有时只显示「未发现设备」 |
| 次要入口 | 列表底部一行：「+ 手动添加」→ 展开 IP 输入框 + 连接按钮（连接后自动收起），右侧「刷新」 |
| 缓冲档位 | Material3 分段按钮：低延迟 / 平衡 / 稳定（**无说明文字**） |
| 主按钮 | 开始发送（蓝）/ 停止发送（红），高 72 dp；未选设备时禁用 |
| 底部指标 | 丢包率 / 已发送，两个数值，已去掉「实时延迟 · 见电脑端」栏位 |

- 字号全部 `sp`、间距全部 `dp`，Compose 按系统 DPI/字体缩放自适应；深色模式跟随系统。
- 状态由 `LinkController.state: StateFlow<SenderUiState>` 驱动，Activity 与服务同进程共享。
- 底部指标栏**不要**再加 `navigationBarsPadding()`：Scaffold 的 `innerPadding` 已包含系统栏高度，
  重复添加会把「丢包率 / 已发送」挤出屏幕底部（本机已踩过）。

### 6.1 已连接设备记忆

目的：广播发现经常被路由器过滤，手动输入的 IP 不应每次重输。

| 项 | 实现 |
| --- | --- |
| 存储 | `SharedPreferences`（文件 `blueaudio_sender`，键 `saved_devices`），一个字符串存整个 JSON 数组 |
| 格式 | `[{"name":"客厅电脑","ip":"192.168.0.165","port":45679,"lastUsed":1793301859635}]` |
| 解析 | `org.json`（Android 框架自带），**不引入 Gson 等第三方依赖** |
| 上限 | 5 台（`SavedDeviceStore.MAX_DEVICES`），按 `lastUsed` 倒序，超出淘汰最旧 |
| 去重 | 同 IP 视为同一台：覆盖名称/端口并刷新 `lastUsed` |
| 读取时机 | `LinkController.attach()`（Activity/Service 首次拿到 Context）→ `load()` → 写入 `SenderUiState.savedDevices` |
| 写入时机 | **收到 PC 首个 PING 即判定连接成功**（`LinkController.onPing` → `rememberTarget()`）；同一 IP 每次连接只写一次，避免每 2 秒落盘 |
| 名称补全 | 之后收到该 IP 的 OFFER 时，用真实电脑名刷新已存条目（名称比 IP 好认） |
| 删除 | 列表项 ✕ → `LinkController.removeSavedDevice(ip)` → 立即落盘并刷新界面。删的若是**当前连接**，本会话内不再自动写回（否则下一个 PING 2 秒内又存回来），重新点一次该设备才再次记住 |
| 损坏兜底 | JSON 解析异常时返回空列表并打日志，界面不崩 |

界面合并规则：`savedDevices` 全量在前（最近使用的一台标「上次使用」）；
`devices`（广播发现）中与已保存 IP 重复的项过滤掉，其余追加在后。
没有电脑名时列表项只显示一行地址，避免同一行把 IP 重复两次。

## 7. 验证方法（本机实测）

### 7.1 构建与安装

```powershell
cd android
.\gradlew.bat :app:assembleDebug     # BUILD SUCCESSFUL
adb install -r -t app\build\outputs\apk\debug\app-debug.apk
adb shell am start -n com.blueaudiosink.sender/.MainActivity

# 界面观感检查：截图落在项目外，避免污染仓库
adb shell screencap -p /sdcard/ui.png
adb pull /sdcard/ui.png C:\Users\Public\ui-check.png

# 查看已保存设备（debug 包可用 run-as 读 SharedPreferences）
adb shell run-as com.blueaudiosink.sender cat /data/data/com.blueaudiosink.sender/shared_prefs/blueaudio_sender.xml
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

### 8.2 UI 与设备记忆（真机实测，截图见 `C:\Users\Public\ui-check-*.png`，不入库）

| 检查项 | 结果 |
| --- | --- |
| 文案精简 | ✅ 全屏无长句；档位只有「低延迟 / 平衡 / 稳定」；底部只有「0.0% 丢包率 / 1200 已发送」 |
| 空状态 | ✅ 无设备时只有一行「未发现设备」，手动输入折叠为「+ 手动添加」 |
| 手动添加 | ✅ 展开输入 → 点「连接」→ 输入框自动收起；收到首个 PING 后写入记忆 |
| 记忆持久化 | ✅ 强杀进程重启后设备仍在列表首位，带「上次使用」标记 |
| 点已保存设备直连 | ✅ 点一下即发 HELLO，状态卡变「已连接 + IP」，无需再输 IP |
| 删除设备 | ✅ 点 ✕ 立即消失，`saved_devices` 同步减少；连接中删除也不会被后续 PING 写回 |
| 多设备排序 | ✅ 两台设备按最近使用倒序，只有最近一台带「上次使用」 |
| 广播发现 | ✅ PC 侧 OFFER 探测包可被列出（灰点、无删除按钮、无「上次使用」标记），6 秒过期后自动消失 |
| 发送/停止 | ✅ 授权后 6 秒 1200 包、丢包率 0.0%；停止后回到「已连接」，前台通知移除 |
| 底部指标栏 | ✅ 数值完全可见（修掉重复 `navigationBarsPadding` 导致的裁切） |

## 9. 已知限制与风险

1. **手机端不显示 RTT**（主 agent 已裁决 MVP 不扩展协议）：PING 由 PC 发起，手机只回 PONG，
   拿不到 T3，无法计算 RTT。底部只保留「丢包率 / 已发送」两个数值，不再放延迟栏位
   （避免出现「见电脑端」这类解释性文字）。
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