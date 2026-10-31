# 0.3.1 Candidate 构建与验收记录

日期：2026-10-31。分支：`feature/phone-microphone`。这是候选版本，不代表手机/电脑端到端音频验收完成。

## 改动

- Windows x64 `CSADDR_INFO` 保留嵌套 `SOCKET_ADDRESS` 的原生对齐，修复 SDP 注册返回 10022。
- 两端移除底部“开发作者”署名，关于中显示版本、原作者和当前项目仓库链接。
- Windows 启动日志从程序集元数据获取版本，不再固定记录 0.1.0。
- Windows 发布缺少 MSVC editbin 时，使用自带 PowerShell 脚本设置 PE GUI 子系统。
- Android versionCode=5、versionName=`0.3.1-candidate`；Windows 使用相同版本。
- 中文 README 为默认首页，保留英文版及中文旧入口，加入本次关于页面截图。双端交付包含 MIT 许可证。

## 构建条件与结果

| 项目 | 环境与结果 |
| --- | --- |
| Windows SDK | 项目诊断目录内的 .NET SDK 8.0.427，下载包已对照微软发布元数据验证 SHA512 |
| Windows 发布 | Release / win-x64 / self-contained / single-file / trimmed，成功；保留 NAudio、内建 COM 的 IL2026/IL2104 裁剪警告 |
| Windows PE | GUI 子系统为 2；实际启动正常，无额外控制台窗口 |
| C# 音频回归 | 34/34 通过，包含蓝牙 ADPCM、分帧及不打开 UDP 的通道检查 |
| RFCOMM | 修复前真实监听代码注册失败 10022；修复后结构检查与真实适配器三次 bind/listen/SDP 注册/释放通过 |
| Android | 现有 JDK 21.0.10、Gradle 8.13（满足本项目 AGP 要求；Wrapper 仍固定 8.11.1）、AGP 8.10.1、Platform 35、Build Tools 35.0.0 |
| Android 构建 | assembleRelease、testReleaseUnitTest、lintRelease 成功；13/13 单元测试通过；lint 无 Error，9 Warning、1 Hint |
| Android 签名 | APK v2 校验通过，RSA 3072，证书主体 `CN=BlueAudioSink, OU=LunageZz` |
| Android 包信息 | `com.blueaudiosink.sender`、versionCode=5、versionName=`0.3.1-candidate`，无 debuggable 标记，包含 `assets/LICENSE` |

本地新建发布密钥位于 `android/.signing/`，配置位于 `android/signing.properties`；两者已确认由 Git 忽略，不随公开包交付。后续覆盖升级必须沿用该密钥，应另行安全备份。此密钥与原作者的签名不相同，不能覆盖不同签名的旧安装。

## 界面验收

- Windows：实际启动发布入口；主界面无常驻作者署名，麦克风通道显示等待手机连接；关于页版本、两仓库按钮及许可证显示完整，Tab 焦点正常。没有完成 Windows 仓库按钮打开浏览器及 Esc 返回的交互核验。
- Android：Release APK 安装到现有 API 36 模拟器；关于布局、版本及链接正常；点击两个链接分别发出正确的 VIEW Intent；返回浏览器后关闭弹窗正常，底部只保留统计指标。
- README 里的当前关于截图分别来自 Windows 实际运行程序和 Android 模拟器；旧主界面截图单独标为历史参考。

## 产物与待验收

交付目录：`artifacts/BlueAudioSink-0.3.1-Candidate/`。包含 Windows 单文件程序、Android Release APK、MIT LICENSE、安装说明及 SHA256SUMS。临时 SDK 下载、诊断脚本副本、模拟器截图和构建日志集中在 `artifacts/bluetooth-diagnostics/`，删除不影响源码或本地发布签名。

仍需实体手机确认蓝牙连接、真实麦克风音频、A2DP 并行、VB-CABLE 与会议软件，以及连续运行和断连恢复。ADB 本次未连接实体手机；模拟器验收不能代替这些项目。
