# BlueAudioSink

English | [简体中文](README.zh-CN.md)

Play your phone's audio through your Windows PC's headphones or speakers. BlueAudioSink provides a lightweight Windows receiver with **Bluetooth** and **local-network Wi-Fi** modes, plus a companion Android app for Wi-Fi streaming.

Developed by **liubaiqing**. The current app interfaces are in Chinese; this README explains the main controls in English.

## Download

Get the Windows executable and Android APK from [Releases](https://github.com/liubaiqing/BlueAudioSink/releases).

| Platform | Download | Requirements |
| --- | --- | --- |
| Windows | `BlueAudioSink-0.1.0.exe` | Windows 10 version 2004 (build 19041) or later, x64 |
| Android sender | `BlueAudioSink-Sender-0.1.0.apk` | Android 10 or later; needed for Wi-Fi mode only |

The Windows app is a self-contained executable: no .NET installation is required. The Android APK is a Release build signed with a dedicated release key. `SHA256SUMS.txt` contains checksums for both downloads.

**Upgrading from the earlier debug-signed Android APK:** uninstall that version before installing the release-signed APK. Uninstalling clears saved app data, including remembered computers. Future release builds will use the same release key so they can update in place.

## Wi-Fi setup

1. Connect your phone and PC to the same local network. A 5 GHz Wi-Fi connection is preferable; the PC can also use Ethernet on the same router.
2. Run BlueAudioSink on the PC and select **Wi-Fi**.
3. Install and open the Android sender, select the discovered PC, and start sending. Grant the recording and screen/audio capture permissions requested by Android.
4. Play audio on your phone. The PC displays connection, receiving, and playback status.

No USB connection, developer options, or wireless debugging is needed.

If audio works, leave the firewall helper off. If the PC cannot receive audio, open **Settings → Network** (`设置 → 网络`) and try **Allow through firewall** (`尝试开启放行`), then approve the Windows administrator prompt. This adds inbound UDP rules for the current executable, private networks, and local-subnet sources only. You can remove the app's rules from the same page. Moving the executable requires configuring the rules again. Public-network restrictions, router client isolation, third-party firewalls, or organization policies can still prevent communication.

Android playback capture depends on the source app allowing it. Some apps and protected content cannot be captured. Wi-Fi transport uses unencrypted UDP; use it on a trusted local network.

## Bluetooth setup

1. Pair your phone with the PC in Windows Bluetooth settings.
2. Open BlueAudioSink and select your phone in **Settings → Devices** (`设置 → 连接设备`). Choose the PC's playback device in **General** (`常规`).
3. Select **Bluetooth** (`蓝牙`) and connect. Play audio on the phone and select the PC as its Bluetooth media output if needed.

The Android sender is not required for Bluetooth mode. Bluetooth audio reception depends on Windows, the adapter, and its driver. With auto-connect enabled, the app attempts to connect to the preferred phone; the phone and operating system still control whether audio is actually sent. The UI distinguishes readiness from detected audio activity.

## Shared settings

The **Settings** (`设置`) button serves both modes. Opening settings keeps the current audio session running.

- **General** (`常规`): playback device, launch at Windows sign-in, Bluetooth auto-connect, and Wi-Fi prebuffer.
- **Devices** (`连接设备`): select or switch Bluetooth / Wi-Fi phones. A Wi-Fi phone must first select the PC in the sender app. Selecting a source locks reception to that phone for the current Wi-Fi session.
- **Network** (`网络`): optional firewall troubleshooting controls.

Closing the window exits the app. Mixing multiple phones simultaneously is not supported.

## Build from source

### Windows

Use Windows with the .NET 8 SDK or a compatible newer SDK.

```powershell
dotnet build .\BlueAudioSink.sln -c Debug -p:Platform=x64
dotnet publish .\src\SinkLite\SinkLite.csproj -c Release -p:Platform=x64
```

Output: `src/SinkLite/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/publish/BlueAudioSink.exe`.

To keep the executable small, the project uses `OutputType=Exe` and `NAudio.Wasapi`. A post-publish step uses Visual Studio's `editbin` to set the Windows subsystem and hide the console. Override the tool path for your MSVC installation:

```powershell
dotnet publish .\src\SinkLite\SinkLite.csproj -c Release -p:Platform=x64 '-p:EditBinPath=C:\path\to\editbin.exe'
```

If `editbin` is unavailable, publishing continues, but the app may open a console window.

### Android

Use JDK 21 and Android SDK 35. The repository includes the Gradle Wrapper.

```powershell
cd android
# Set ANDROID_HOME, or configure sdk.dir in your local.properties file.
.\gradlew.bat assembleDebug
```

Debug output: `android/app/build/outputs/apk/debug/app-debug.apk`.

For a signed Release build, copy `android/signing.properties.example` to `android/signing.properties`, fill in your own keystore path and credentials, then run:

```powershell
.\gradlew.bat assembleRelease
```

Signed output: `android/app/build/outputs/apk/release/app-release.apk`. Without local signing configuration, Gradle produces an unsigned Release APK. Keystores, passwords, and local SDK settings must stay out of Git. Back up your release key and credentials: subsequent app updates must use the same signing identity. Forks should use their own key.

## Validation and diagnostics

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

Settings: `%LocalAppData%\BlueAudioSink\settings.ini`. Logs: `%LocalAppData%\BlueAudioSink\logs\`.

## Repository layout

- `src/SinkLite/`: Windows receiver, Win32 / GDI UI, Bluetooth, Wi-Fi, and WASAPI playback.
- `android/`: Android sender with Jetpack Compose and system playback capture.
- `tools/`: regression checks, diagnostics, and asset tools.
- `docs/`: architecture, protocol, implementation, and test records, primarily in Chinese.

See the [architecture](docs/01-architecture.md), [wire protocol](docs/02-protocol.md), [Android implementation](docs/04-android-implementation.md), [audio playback fix](docs/08-audio-playout-fix.md), and [desktop UX notes](docs/10-desktop-ux.md). Historical test results describe the devices and network conditions used at the time.

## License

[MIT](LICENSE). Third-party dependencies and tools retain their respective licenses.
