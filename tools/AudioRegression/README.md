# 音频回归与录音工具

直接链接产品源码，验证播放缓冲内容保真与恢复机制，不启动产品窗口；网络集成项临时绑定系统分配的 UDP 端口，不占用产品的 45678/45679 端口。

```powershell
dotnet run --project tools/AudioRegression/AudioRegression.csproj -c Release
```

10 项音频回归包括：健康 PCM 保真、真实补零计数、恢复阈值、正常排空、空暂存队列不误欠载、重复 START 的消费者隔离、乱序、真正缺包截止、序号回绕、90 秒双节拍连续流。另有 5 项 UI 状态映射回归，覆盖静音、过期端点、断流、恢复/排空/输出失败和启动故障，再加 4 项多设备发现、来源锁定与真实双 UDP 发送端切换测试，共 19 项。

录制 Windows 默认输出端点（标准 WAV，真实采样格式写入文件头；JSON 输出设备和格式）：

```powershell
dotnet run --project tools/AudioRegression/AudioRegression.csproj -c Release -- --record 60 C:\Users\Public\received.wav
```

将原 MP3 解码成 48 kHz / 16-bit / stereo 标准 WAV，供匹配同一音乐片段使用：

```powershell
dotnet run --project tools/AudioRegression/AudioRegression.csproj -c Release -- --decode 'D:\music.mp3' 'C:\Users\Public\reference.wav'
```

不要以自然音乐的峰值/全曲 RMS 分位数直接判定音频是否压缩；先匹配片段、增益和时间轴。Read 计数只覆盖应用主动补零，仍应结合输出录音和输入最长零帧段，区分采集端与系统音频栈的异常。

检查标准 WAV 中的内部近零片段（需要 numpy/scipy）：

```powershell
python tools/AudioRegression/analyze_recording.py C:\Users\Public\received.wav
```
