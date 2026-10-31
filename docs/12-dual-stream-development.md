# 手机媒体与麦克风：0.2.0 开发测试版

后续 0.3.0 已扩展蓝牙麦克风，使用方法、带宽限制和验收范围见 [蓝牙麦克风说明](13-bluetooth-microphone.md)。本页保留 Wi-Fi 双路基础设计。

## 已接入的功能

Android 可选择媒体、麦克风或两者。媒体使用 AudioPlaybackCapture（需要系统授权），麦克风使用单声道 AudioRecord MIC，扩展为 48 kHz / 16-bit / stereo 协议帧。仅麦克风不申请录屏授权；服务声明 microphone 与 mediaProjection 类型，按实际来源启动。停止、失败和系统撤销授权会结束采集并释放线程。

Windows 耳机只播放手机媒体；独立的虚拟输出接收麦克风及可选媒体混音。设置 → 麦克风中提供静音、0–100% 音量滑块、分享手机声音开关、耳机/虚拟设备选择和驱动安装入口。手机也提供静音、音量滑块和同步状态。分享开关默认关闭，开启不会中断耳机。不采集电脑系统声音。

Windows 对接收的麦克风应用一次增益；手机静音同时阻止真实麦克风 PCM 发送。静音及音量不会修改耳机媒体声或分享的媒体声。Windows 为控制状态的权威端，控制包携带会话、请求序号及已知版本；重复请求只回确认，过期版本不能覆盖 Windows 的较新状态。两端同时修改冲突时，音量和取消静音以 Windows 较新状态优先；新的静音请求始终生效并保留较新的音量，避免音量冲突意外取消静音。手机显示确认后的状态，若请求值被较新设置覆盖则提示。重连后以电脑保存的状态为准。

## 协议与兼容

BAS1 控制类型 10 请求双路会话，header.seq 为非零随机 session，payload u32 为来源位：1=媒体、2=麦克风。类型 11 为状态/确认，header.seq 为版本，payload 六个 u32：session、已确认请求序号、静音(0/1)、音量(0–100)、能力状态(1=支持双路、2=虚拟输出就绪)、分享媒体(0/1)。类型 12 为控制请求，header.seq 为请求序号，header.flags 位 0 表示修改静音、位 1 表示修改音量（有效掩码 1–3），payload 四个 u32：session、已知版本、静音、音量。

BAS2 数据使用独立 magic；20 字节头为 magic(4)、stream(1)、flags(1)、reserved(2)、session(4)、sequence(4)、timestampUs(4)，多字节均小端。stream 0 为媒体，1 为麦克风；两路独立序号、20 ms 重排窗口和缓冲。每包 5 ms / 960 字节 PCM。旧接收端不会把麦克风误当作媒体播放。

手机收到对应会话确认前不发送 BAS2；握手和未确认控制每 500 ms 重试，正常状态也每 500 ms 下发。仅媒体与旧 PC 协商 3 秒无确认时回退 BAS1；启用麦克风时不会回退到耳机输出。旧会话 STOP、其他手机或错误会话数据不影响当前源。

## 音频设备与驱动

使用标准 VB-CABLE 即可。默认自动寻找 CABLE Input；可在检测到的 CABLE Input 播放端中选择；不将物理扬声器或录音端作为虚拟输出。应用向 CABLE Input 写入，会议软件选择对应的 CABLE Output。物理耳机和虚拟设备独立打开；虚拟设备缺失时不把麦克风回退到物理耳机，设备断开会退避重试。

安装入口下载官方原始 Pack45 ZIP，校验固定 SHA-256，完整解压后以管理员权限启动独立官方安装器。用户阅读并接受第三方许可、点击 Install Driver，完成后按官方要求重启；应用不自动重启。来源 https://vb-audio.com/Cable/ ，许可与 donationware 说明 https://vb-audio.com/Services/licensing.htm 。也可使用单独提供的原始 ZIP 手工安装。

## 验证范围

C# 31 项音频回归覆盖 PCM 路由、分享、静音/音量、版本冲突与重复请求、会话来源隔离、序号回绕及原有媒体行为。Android 单元测试覆盖协议 golden header、连接限制、来源过滤、控制请求编码与状态确认/旧版本过滤。开发 APK 使用独立 .dev 包名和调试签名，可与正式版共存（运行时只开一个发送端）。

当前环境是 Linux，尚未完成 Windows WASAPI + Android 真机端到端验收；构建与离线回归不能证明实际设备、会议软件、Android 并发采集兼容性、端到端延迟或长时间时钟漂移均已通过。部分 Android 应用禁止媒体音频采集。开发包仅用于可信局域网测试。

## 构建与本轮交付

Windows：`dotnet publish src/SinkLite/SinkLite.csproj -c Release -p:EnableWindowsTargeting=true -p:PublishTrimmed=false`。开发预览包保留 COM/NAudio 完整运行依赖，优先验证功能。Android：在 JDK 17 和 Android SDK 35 环境执行 `cd android && ./gradlew :app:assembleDebug :app:testDebugUnitTest :app:lintDebug`，debug 默认包名带 .dev，无需原正式发布密钥。

本轮为代码路径已接通的功能预览版，不是 58e83a2 的协议核心包。Windows 构建与 31 项 C# 回归、Android APK 构建与 10 项 SDK 单元测试及 lint 构成本轮自动验证范围。Android 还使用真实本机 UDP 验证了未确认控制重试及收到确认后停止重发。真机设备与会议软件的人工验收仍待进行。
