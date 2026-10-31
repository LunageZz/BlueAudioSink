# 蓝牙麦克风：0.3.0 开发预览版

> 本文保留功能设计与开发历史。当前正式版本为双端 0.3.3，下载与现行验收记录以 [README](../README.md) 和 [正式发布记录](17-release-0.3.3.md) 为准。

0.3.1 Candidate 修复 x64 服务注册并增加双端关于入口，当前构建及验收结果见 [0.3.1 构建记录](14-release-0.3.1-candidate.md)。下文保留原传输设计与实机验收要求。

0.3.2 Candidate 默认在蓝牙模式只采集麦克风，媒体继续走系统 A2DP；不继承 Wi-Fi 的媒体采集开关。手机系统媒体输出必须选择电脑。只有额外开启“将媒体声加入虚拟麦克风”才请求屏幕/音频采集授权。成功连接过的蓝牙电脑在仍配对时会置顶并预选；开始/停止按钮固定在底部。详见 [行为与验证记录](15-bluetooth-capture-and-memory.md)。

此版本在 `feature/phone-microphone` 分支扩展了蓝牙传输。两端均需更新至本次版本。无需 Wi-Fi 或热点，也不需要新增驱动；会议软件继续通过 VB-CABLE 接收。

## 使用

1. 在手机和 Windows 系统蓝牙设置中完成配对，开启两端蓝牙。
2. Windows 打开 BlueAudioSink，选择「蓝牙」。主页面与「设置 → 麦克风」显示独立的麦克风通道状态。只传麦克风时，不必建立 A2DP 媒体连接。
3. 手机应用选择「蓝牙」，允许「附近设备」权限，在已配对列表选择电脑。列表是系统已配对设备，不会扫描附近未配对设备。配对后点击刷新。
4. 默认开启「手机麦克风」，点击固定在底部的「开始发送」，允许录音权限。默认不采集媒体，无需录屏授权。
5. 电脑安装 VB-CABLE 后，会议、聊天或录音软件选择 `CABLE Output` 为输入。BlueAudioSink 写入 `CABLE Input`，不要将麦克风输出切到扬声器。

媒体仍经系统 A2DP 传至电脑耳机。在 Windows 点击蓝牙「连接」，并在手机系统中选择电脑为媒体输出。麦克风和媒体可同时使用。若要把媒体也加入虚拟麦克风，在手机开启媒体来源并授权屏幕/音频采集，在 Windows 打开「分享手机声音」；媒体副本仅进入虚拟麦克风，耳机不重复播放。受保护内容及禁止 AudioPlaybackCapture 的应用可能无法分享。

静音、音量在两端同步；重连后以电脑保存的状态为准。静音不影响媒体；音量只在电脑混音时应用一次。电脑「断开」会同时断开媒体和麦克风；手机检测蓝牙断开会停止本次采集，需重新选择电脑并开始发送。切换 Wi-Fi/蓝牙前须停止发送；Windows 切换模式释放原模式的通道。

## 传输设计

- 原 A2DP 媒体接收与新的、要求配对认证的 RFCOMM 通道并行。Android 使用安全 RFCOMM socket，Windows 使用桌面 Winsock RFCOMM + SDP 服务注册，无需 MSIX/UWP 打包或额外蓝牙库。
- 自定义服务 UUID：`c774d750-0c0b-4cd9-a4af-a060d0b14925`。同一时间接收一个已配对手机的麦克风连接；第二个连接不会抢占当前会话。配对与授权使用系统界面。
- RFCOMM 字节流分帧：`payloadLength(u16 LE) + codec(u8) + reserved(u8=0) + payload`，长度限制 16–2048。codec 0 是 BAS1 控制，codec 1 是独立可解码的 IMA ADPCM BAS2 音频块。拆包使用精确读取，拒绝截断、超长和非法格式。
- ADPCM payload：BAS2 原始头(20) + channels(u8) + stride(u8) + reserved(2) + 每声道 predictor(i16 LE)、index(u8)、reserved(u8) + 低半字节优先的 IMA 编码。每包代表 5 ms；语音为 16 kHz 单声道（stride=3），分享媒体副本为 24 kHz 双声道（stride=2）。接收后扩展至现有 48 kHz / 16-bit / stereo 混音管线。原始 A2DP 耳机播放不经过此压缩。
- 每包携带初始解码状态，丢弃积压包后仍可继续解码。发送音频队列最多 24 包（120 ms），积压丢最旧包，控制指令优先；语音和媒体保持独立序号。蓝牙输出缓冲目标 160 ms。仅在电脑开启分享后发送媒体副本，减少竞争带宽。
- 复用双路会话、版本确认、静音/音量冲突处理和虚拟输出隔离。蓝牙模式不绑定 UDP，不发现 Wi-Fi 设备，不回退 BAS1 音频。会话用控制心跳维持，媒体副本关闭时不会因为没有媒体包而反复重建会话。

蓝牙共享射频带宽；适配器、驱动、距离和同时使用 A2DP 会影响延迟及稳定性。本版本优先限制积压；不承诺达到 Wi-Fi 的音质、延迟或稳定性。

## 验证与待验收

### Windows 服务注册故障回归

手机提示 `read failed, socket might closed or timeout, read ret: -1` 时，先查看电脑日志是否有 `Bluetooth.MicrophoneFault`。已定位并修复 x64 原生 `CSADDR_INFO` 布局错误：展开嵌套 `SOCKET_ADDRESS` 会丢失尾部对齐，使 SDP 注册返回 10022（无效参数）。此修复需要更新电脑端；手机端协议不变。

运行 `pwsh -NoProfile -File tools/Test-RfcommListener.ps1` 检查产品源码的原生结构布局；带 `-Hardware` 可在 Windows 蓝牙适配器上验证监听、SDP 注册及连续三次启动/释放。无需 .NET SDK。该检查不代表手机连接、音频传输或会议软件验收通过。临时诊断与构建下载文件统一放在可整体删除的 `artifacts/bluetooth-diagnostics/` 中。

分支原有构建记录包括 Windows 发布构建、34 项 C# 音频回归、Android APK 构建、13 项 SDK 单元测试与 lint（无错误）。新增两语言共享的参考音频向量验证 Kotlin 编码/C# 解码、独立包状态、流隔离；另测帧拆分/合并/截断、蓝牙会话不打开 UDP 和不重复耳机路由、Android 无 UDP 的握手/麦克风/媒体/静音音量确认。这些是此前的检查记录，不代表本次修复已重新执行完整构建。

上述原有检查在 Linux 构建环境完成。本次修复已在 Windows x64 真实蓝牙适配器上通过三次监听与 SDP 注册/释放检查；仍未完成手机端连接、Windows WASAPI、Android 并发采集和会议软件验收。以下仍需两台实机检查，不能用编译或离线回归替代：

- 关闭 Wi-Fi/移动网络，蓝牙麦克风可被录音软件接收，双方静音及 0–100 音量同步。
- A2DP 媒体播放与麦克风并行，耳机无麦克风回放、无重复媒体。
- 分享开/关仅影响虚拟麦克风的媒体副本，耳机连续播放。
- 断开、超出范围、撤销权限、重新配对、切换模式后无残留采集或声音。
- 连续通话 30 分钟，记录断音、延迟、音量和不同适配器兼容性。

参考：[Android RFCOMM](https://developer.android.com/develop/connectivity/bluetooth/connect-bluetooth-devices)、[Windows 服务注册](https://learn.microsoft.com/en-us/windows/win32/bluetooth/bluetooth-and-wsasetservice)。
