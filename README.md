# BlueAudioSink

让 Windows 电脑接收手机音频，通过电脑的耳机或音箱播放。支持蓝牙和局域网 Wi-Fi 两种模式，提供轻量桌面客户端和配套 Android 发送端。

## 下载

在 [Releases](https://github.com/liubaiqing/BlueAudioSink/releases) 下载：

- **Windows x64**：`BlueAudioSink-0.1.0.exe`，自包含单文件，直接运行，无需安装 .NET。
- **Android 10 及以上**：`BlueAudioSink-Sender-0.1.0.apk`，用于 Wi-Fi 发送音频。目前提供测试 APK（Debug 签名），应用内版本号为 1.0.0。

Windows 端需要 Windows 10 2004（19041）或更新版本。蓝牙接收还依赖系统、蓝牙适配器和驱动支持。

## 快速开始

### Wi-Fi 模式

1. 电脑和手机接入同一局域网，优先使用 5 GHz Wi-Fi；电脑也可以使用同一路由器下的有线网络。
2. 电脑运行 BlueAudioSink，切换到「Wi-Fi」。
3. 手机安装并打开配套 Android App，选择发现的电脑并开始发送，按系统提示授予录音与屏幕音频采集权限。
4. 手机播放音频，电脑会显示接收和播放状态。无需 USB、开发者模式或无线调试。

如果 Wi-Fi 可以正常收音，无需开启防火墙放行功能。遇到无法收音时，可在电脑「设置 → 网络」点击「尝试开启放行」，并确认 Windows 管理员授权。程序仅为当前 exe 添加专用网络、局域网来源的 UDP 发现与音频端口规则；同页可移除本程序规则。移动 exe 后需重新配置。公共网络、路由器隔离或组织网络策略仍可能阻止连接。

Android 只能采集允许系统播放采集的应用音频；部分应用或受保护内容可能无法传输。Wi-Fi 音频使用未加密的 UDP，请在可信局域网中使用。

### 蓝牙模式

1. 在 Windows 系统蓝牙设置中先配对手机。
2. 打开 BlueAudioSink，在「设置 → 连接设备」选择手机，并在「常规」选择电脑播放设备。
3. 切换到「蓝牙」并连接，在手机上播放音频，必要时将蓝牙媒体输出切换到电脑。

蓝牙模式无需安装配套 Android App。启用蓝牙自动连接后，程序会尝试连接首选设备；手机是否实际发送音频仍由手机与系统决定。界面会区分准备接收、等待手机和检测到音频等状态。

### 统一设置

顶部「设置」同时服务两种模式，进入设置不会停止当前音频。

- **常规**：播放设备、开机启动、蓝牙自动连接和 Wi-Fi 预缓冲。
- **连接设备**：选择或切换蓝牙 / Wi-Fi 手机。Wi-Fi 手机需要先在发送端选择电脑；选定来源后只接收该手机的音频。
- **网络**：可选的防火墙故障排查功能。

关闭窗口即退出程序。当前不支持多台手机同时混音。

## 从源码构建

### Windows

需要 .NET 8 SDK 或兼容的新版本 SDK，以及 Windows 开发环境。

```powershell
dotnet build .\BlueAudioSink.sln -c Debug -p:Platform=x64
dotnet publish .\src\SinkLite\SinkLite.csproj -c Release -p:Platform=x64
```

产物位于 `src/SinkLite/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/publish/BlueAudioSink.exe`。

为保持单文件体积，项目使用 `OutputType=Exe` 和 `NAudio.Wasapi`。发布后通过 Visual Studio 的 `editbin` 设置 Windows 子系统以隐藏控制台；请按本机 MSVC 安装位置传入路径：

```powershell
dotnet publish .\src\SinkLite\SinkLite.csproj -c Release -p:Platform=x64 '-p:EditBinPath=C:\path\to\editbin.exe'
```

若找不到 `editbin`，构建仍会继续，但启动时可能出现控制台窗口。

### Android

需要 JDK 21、Android SDK 35。仓库提供 Gradle Wrapper。

```powershell
cd android
# 配置 ANDROID_HOME，或在未入库的 local.properties 中填写 sdk.dir
.\gradlew.bat assembleDebug
```

测试 APK 位于 `android/app/build/outputs/apk/debug/app-debug.apk`。正式分发时请使用自己的签名配置构建 Release APK；不要提交密钥或本机 SDK 配置。

## 验证与诊断

```powershell
dotnet run --project tools/AudioRegression/AudioRegression.csproj -c Release
dotnet run --project tools/FirewallRegression/FirewallRegression.csproj -c Release

.\BlueAudioSink.exe --list
.\BlueAudioSink.exe --connect
.\BlueAudioSink.exe --diagnose 30
.\BlueAudioSink.exe --help

dotnet run --project tools/SinkProbe -- endpoints
dotnet run --project tools/SinkProbe -- meter 10
```

配置保存在 `%LocalAppData%\BlueAudioSink\settings.ini`，日志位于 `%LocalAppData%\BlueAudioSink\logs\`。

## 项目结构

- `src/SinkLite/`：Windows 客户端，Win32 / GDI 界面、蓝牙连接、Wi-Fi 接收和 WASAPI 播放。
- `android/`：Android 发送端，Jetpack Compose 界面与系统播放音频采集。
- `tools/`：音频、防火墙回归验证及诊断工具。
- `docs/`：架构、协议、实现与测试记录。

进一步阅读：[架构](docs/01-architecture.md)、[协议](docs/02-protocol.md)、[Android 实现](docs/04-android-implementation.md)、[音频播放修复](docs/08-audio-playout-fix.md)、[桌面交互](docs/10-desktop-ux.md)。文档中的实测记录对应当时的设备与网络条件。

## 许可证

[MIT](LICENSE)。第三方依赖及工具保留各自的许可证。
