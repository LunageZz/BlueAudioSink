# 0.3.2 Candidate：A2DP 媒体、独立麦克风与设备记忆

日期：2026-10-31。Windows 与 Android 版本均为 `0.3.2-candidate`，Android versionCode=6。

## 使用约定

- 媒体继续通过系统 A2DP 播放到电脑。在手机的系统媒体输出中选择电脑（例如 LUNAGE），并在电脑端选择蓝牙模式、连接手机。应用内选电脑不等于系统媒体输出已切换。
- 手机发送端切换到蓝牙时默认开启麦克风、关闭媒体副本。选择电脑后点击固定在底部的“开始发送”，只启动录音及蓝牙麦克风传输，不申请屏幕共享。
- 如果还需要把音乐混入虚拟麦克风，可以主动开启“将媒体声加入虚拟麦克风”，确认系统屏幕/音频采集授权，并在电脑开启“分享手机声音”。该功能不重复向耳机播放媒体。
- Wi-Fi 的媒体来源开关与蓝牙媒体副本开关分开保存于界面状态；切换模式不会互相覆盖。蓝牙媒体副本在进程重启后默认关闭。
- 记住最近一次成功收到电脑心跳的蓝牙目标。下次启动恢复连接模式；目标仍在系统配对列表中时，置顶、标记“上次使用”并预选。刷新不覆盖用户当前选择，不恢复已取消配对的地址，不自动开启录音。

麦克风输入与其他应用媒体采集是两条路径。后者需要 MediaProjection 用户授权，参见 [Android 音频播放采集说明](https://developer.android.com/media/platform/av-capture)。纯麦克风仍需正常录音、蓝牙权限及前台服务通知。

## 原因与实现

用户已确认：新版 APK 麦克风可用，手机系统媒体输出改选 LUNAGE 后，媒体声音恢复。此次保留 A2DP，不把媒体改为应用压缩转发。

原发送端同时把媒体开关用于 Wi-Fi 和蓝牙，默认勾选的 Wi-Fi 媒体采集会让蓝牙麦克风走屏幕授权流程。`SenderUiState.captureMedia` 现在统一决定授权、服务 Intent 和协议协商：蓝牙读取独立的 `bluetoothMediaCopyEnabled`，Wi-Fi 继续读取 `mediaEnabled`。仅麦克风时 `AudioCaptureService` 不创建 MediaProjection，前台类型为 microphone。

原主按钮位于可滚动长设备列表之后，容易被挤到屏幕外。按钮已移动到滚动区域外，统计栏上方。设备历史沿用 SharedPreferences，新增独立蓝牙地址与连接模式键，不把 MAC 写入 Wi-Fi IP 历史，也不新增依赖。

电脑本来就会保存最近选择的蓝牙手机，本次保留。诊断工具 `SinkProbe routes` 新增只读默认输出、静音、音量及 A2DP 输入峰值检查，不更改系统路由。

## 验证

| 检查 | 结果 |
| --- | --- |
| 长配对列表的真实 Compose 界面回归 | 修复前失败“component is not displayed”，固定主按钮后通过 |
| 媒体采集策略回归 | 旧策略两项失败；分离蓝牙媒体副本与 Wi-Fi 开关后通过 |
| Android Release 单元测试 | 19/19 通过，覆盖设备记忆、预选/取消配对、模式隔离和原传输测试 |
| Android 模拟器 UI 回归 | 1/1 通过，实际运行 SenderScreen 的长配对列表 |
| Android Release、lint | 构建通过，lint 无 Error；11 Warning、1 Hint（兼容性、依赖更新及风格建议等） |
| Android APK | v2 签名校验通过，versionCode=6，无 debuggable 标记；沿用 0.3.1 本地发布密钥，可覆盖同签名 Candidate |
| Windows Release | 自包含 x64 单文件发布通过、PE 为 GUI 子系统；保留原有 NAudio/COM 裁剪警告 |

新版本仍需实体手机复测：纯麦克风启动不弹屏幕共享、A2DP 与麦克风并行、重启恢复上次设备及授权撤销。用户对旧 Candidate 的现场反馈不替代 0.3.2 真机验收。

产物位于 `artifacts/BlueAudioSink-0.3.2-Candidate/`，临时验证日志位于 `artifacts/bluetooth-diagnostics/`。密钥仍仅保留在被 Git 忽略的 `android/.signing/` 与 `android/signing.properties`，不包含在公开压缩包中。
