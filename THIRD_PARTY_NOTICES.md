# 第三方软件说明

## VB-CABLE Driver Pack 45

VB-CABLE 由 **VB-Audio Software / Vincent Burel** 提供，原项目地址为 <https://www.vb-cable.com/>，官方产品页为 <https://vb-audio.com/Cable/>。

**VB-CABLE 为 Donationware，欢迎在使用后向原作者捐赠或购买许可，支持其继续开发。** 捐赠、使用及专业场景许可信息见 [VB-Audio 官方许可说明](https://vb-audio.com/Services/licensing.htm)。

BlueAudioSink 的正式发布附带普通 VB-CABLE 的原版 `VBCABLE_Driver_Pack45.zip`，保持文件原样，不重新打包或修改其中的驱动、安装器、readme 或许可。该驱动不属于 BlueAudioSink 的 MIT 许可证，也不包含 VB-CABLE A+B、C+D 等其他产品。

- 官方下载：<https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip>
- SHA-256：`b950e39f01af1d04ea623c8f6d8eb9b6ea5c477c637295fabf20631c85116bfb`
- 安装方式：解压官方包，由用户运行 `VBCABLE_Setup_x64.exe` 并确认其独立安装流程；BlueAudioSink 不静默安装驱动。
- 分发依据：原包 `readme.txt` 允许原样复制分发，并要求说明来源与 Donationware 性质；另参阅官方许可页的 VB-CABLE 分发部分。

## NAudio

Windows 客户端使用 NAudio 2.2.1 的 Core / WASAPI 组件，采用 MIT 许可证。原项目：<https://github.com/naudio/NAudio>。版权归 NAudio 作者及贡献者所有，具体条款见发行包附带的 `licenses/NAudio-LICENSE.txt`。

## .NET

Windows 自包含客户端包含 Microsoft .NET 运行时组件。相关声明随正式包的 `licenses/` 目录提供；.NET 项目使用 MIT 等开源许可证，具体以组件随附许可与第三方声明为准。原项目：<https://github.com/dotnet/runtime>。

附带的 .NET 许可与第三方声明取自用于构建的官方 .NET SDK 8.0.427，涵盖该分发中的组件，部分条目可能并不包含在裁剪后的客户端中。

## AndroidX、Kotlin 与协程

Android 客户端使用 AndroidX / Jetpack Compose、Kotlin 与 kotlinx.coroutines，主要采用 Apache License 2.0。许可证随 APK 的 `assets/Apache-2.0.txt` 及发布包的 `licenses/Apache-2.0.txt` 提供，组件版权归各自作者所有。

- AndroidX：<https://android.googlesource.com/platform/frameworks/support/>
- Kotlin：<https://github.com/JetBrains/kotlin>
- kotlinx.coroutines：<https://github.com/Kotlin/kotlinx.coroutines>

## BlueAudioSink 来源

BlueAudioSink 原作者仓库：<https://github.com/liubaiqing/BlueAudioSink>；当前仓库：<https://github.com/LunageZz/BlueAudioSink>。保留根目录 `LICENSE` 及两端“关于”中的来源信息。
