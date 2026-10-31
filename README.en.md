<p align="center"><img src="docs/images/app-icon.svg" width="88" height="88" alt="BlueAudioSink icon" /></p>
<h1 align="center">BlueAudioSink</h1>
<p align="center">Phone media to PC headphones. Phone microphone to a PC virtual microphone.</p>
<p align="center"><a href="README.md">简体中文（默认）</a> · English · <a href="LICENSE">MIT License</a></p>

Current release: **0.3.3** for both Windows and Android. Source: [main](https://github.com/LunageZz/BlueAudioSink/tree/main), release tag: [v0.3.3](https://github.com/LunageZz/BlueAudioSink/tree/v0.3.3).

## Downloads

| Asset | Download |
| --- | --- |
| Windows x64 standalone EXE | [BlueAudioSink-0.3.3.exe](https://github.com/LunageZz/BlueAudioSink/releases/download/v0.3.3/BlueAudioSink-0.3.3.exe) |
| Android APK | [BlueAudioSink-Sender-0.3.3.apk](https://github.com/LunageZz/BlueAudioSink/releases/download/v0.3.3/BlueAudioSink-Sender-0.3.3.apk) |
| Complete ZIP with apps, original driver ZIP and notices | [BlueAudioSink-0.3.3.zip](https://github.com/LunageZz/BlueAudioSink/releases/download/v0.3.3/BlueAudioSink-0.3.3.zip) |
| Original VB-CABLE driver package | [VBCABLE_Driver_Pack45.zip](https://github.com/LunageZz/BlueAudioSink/releases/download/v0.3.3/VBCABLE_Driver_Pack45.zip) |
| Checksums | [SHA256SUMS.txt](https://github.com/LunageZz/BlueAudioSink/releases/download/v0.3.3/SHA256SUMS.txt) |

Close the old Windows app before opening the new EXE. No .NET installation is required. The APK retains this fork's Candidate signing key and can update packages signed with that key; upstream or other differently signed packages cannot be updated in place.

## Features

- Native Bluetooth A2DP media playback, independent RFCOMM microphone transport, and local-network Wi-Fi audio.
- Bluetooth defaults to microphone-only capture without screen-sharing consent. Optional playback capture for the virtual microphone requires separate consent.
- Independent headphone and virtual microphone routing, synchronized microphone mute/volume, optional media sharing to the virtual microphone.
- Last successful paired Bluetooth target remembered and preselected without automatically recording.
- Start/Stop remains visible below long device lists.
- About views with version information and links to [the upstream repository](https://github.com/liubaiqing/BlueAudioSink) and [this fork](https://github.com/LunageZz/BlueAudioSink).
- Explicit **Reconnect media** rebuilds A2DP without intentionally stopping the independent microphone channel.
- Release review fixed stale settings overwriting microphone state and old Bluetooth workers overlapping a timed-out shutdown.

## Requirements and use

Windows 10 2004+ or Windows 11 x64; Android 10+. Bluetooth requires working adapter/driver support. The apps use a Chinese interface.

**Wi-Fi:** connect both devices to the same trusted LAN, choose Wi-Fi on the PC and phone, select the PC, choose sources, and tap Start. Ethernet on the PC is supported; USB and developer mode are not required.

**Bluetooth media:** pair in system Bluetooth settings, select Bluetooth and connect on the PC, then select the PC in the phone's **system media output selector**. Selecting the PC in the sender app alone does not switch media output. If media stays silent despite playback, use **重新连接媒体** (Reconnect media) on the PC.

**Bluetooth microphone:** select Bluetooth in the sender app, select the paired PC, then use the fixed Start button. By default this captures only the microphone and does not request screen sharing. Native A2DP playback continues independently. Only enable the optional media-copy switch if you also want to mix captured media into the virtual microphone.

**Virtual microphone:** install VB-CABLE if needed. BlueAudioSink writes to `CABLE Input`; select `CABLE Output` as the meeting or recording app's input. Do not route microphone audio to speakers.

## Build and verification

Windows requires .NET 8 SDK. Android requires JDK 17/21 and Android SDK Platform 35; the wrapper uses Gradle 8.11.1.

```powershell
# Publish the Windows standalone app.
dotnet publish src/SinkLite/SinkLite.csproj -c Release -r win-x64 -o artifacts/windows

# Run host audio regressions.
dotnet run --project tools/AudioRegression/AudioRegression.csproj -c Release
```

```powershell
# Build and test Android. Configure local signing.properties for a signed Release APK.
cd android
.\gradlew.bat assembleRelease testReleaseUnitTest lintRelease
```

Signing keys and passwords stay local and are excluded from Git. See the [signing example](android/signing.properties.example). Build downloads and temporary evidence live under `artifacts/`.

See the [0.3.3 release review and verification record](docs/17-release-0.3.3.md) and [Bluetooth guide](docs/13-bluetooth-microphone.md). Basic microphone and A2DP recovery received live user confirmation; long-running behavior across all phones, adapters and meeting apps remains unverified. The cause of occasional native A2DP silence is not fully resolved; this release provides a deliberate recovery action, not a claim of universal driver compatibility.

## License and origins

[MIT License](LICENSE). Upstream: [liubaiqing/BlueAudioSink](https://github.com/liubaiqing/BlueAudioSink). Fork: [LunageZz/BlueAudioSink](https://github.com/LunageZz/BlueAudioSink).

VB-CABLE is VB-Audio's Donationware: identify its [official origin](https://www.vb-cable.com/) and support the author if you find it useful. Its original ZIP is distributed unchanged with its own terms and is not covered by BlueAudioSink's MIT license. See [third-party notices](THIRD_PARTY_NOTICES.md).
