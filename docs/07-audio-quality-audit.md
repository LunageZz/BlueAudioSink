# 音频质量独立检查（2026-10-30）

本次检查针对提交 `2547f83` 的代码；未修改产品实现。检查包含源码审计、直接链接原源码的离线 C# 复现、既有原始录音复核、历史日志复核，以及 Android/NAudio 官方接口核对。没有开展新的真机听音 A/B，因此以下明确区分已复现缺陷与待证实因素。

## 1. 结论

应首先修复 PC 播放调度和缓冲控制，再判断手机采集路径是否还存在额外音色失真。现有证据不足以把所有噪点归因于小米/杜比音效，也不足以声称 scrcpy 的音质提升了某个倍数。

## 2. 已确认的高优先级缺陷

### 2.1 自适应缓冲直接改变音频内容

位置：`src/SinkLite/Core/WifiReceiver.cs:1003`（AdaptLocked），`AudioOutput.cs` 的 AddSilenceMs / DiscardOldest。

每 500 ms 计算一次目标，深度不足时在 BufferedWaveProvider 队尾插入最多 10 ms 全零；过深时从播放缓冲/接收队列删除音频，常规单次调整为 -5 ms。删除未交叉淡化；插入前未淡出。设置 fadeInPending 也不能消除音乐突然切到零的边沿，并且该全局标志并不与具体队列帧绑定。

隔离程序直接编译上述原源码，未启动声卡、网络或手机。输入连续的非静音 PCM，base=80、max=200、jitter=0：

| 输入缓冲 | 计算目标 | 原代码实际结果 | missing / underruns |
| --- | --- | --- | --- |
| 45 ms | 60 ms | 输出 55 ms，其中追加 10 ms 静音 | 0 / 0 |
| 80 ms | 60 ms | 输出 75 ms，删除最旧 5 ms 音频 | 0 / 0 |

因此“零丢包、零欠载计数”不能证明播放内容连续。该机制本身就能制造短暂停顿和波形不连续；是否对应某一次听到的噪点，仍需带调整事件时间戳的同步录音验证。

此外，配置的 BufferBaseMs=80 在 AdaptLocked 中被按 `max(60000, baseMs*1000/2)` 作为下限，实际稳态目标可降到 60 ms。80 ms 并非持续保持的下限。

### 2.2 双层缓冲的欠载判定与实际声卡缺数脱节

位置：WifiReceiver.cs:869、888、930；AudioOutput.cs:ReadFully。

启动时把预缓冲从接收队列移入 BufferedWaveProvider；PumpFrameLocked 随后只要发现接收队列为空，即计欠载并进入重缓冲，没有检查播放器是否还持有足够音频。

直接复现：接收队列为空、BufferedWaveProvider 尚有 60 ms 数据时，调用 PumpFrameLocked 立即得到 rebuffering=true、underruns=1。

另一方面，ReadFully=true 会在声卡读取不足时自动补零，原实现没有监测这些补零字节。复现中从 60 ms 缓冲读取 100 ms，产生 40 ms 自动静音，接收器计数没有随之增加。因此该计数既可能提前报告，也可能遗漏真实的输出补零。

重缓冲结束时也没有像首次启动那样把攒回的音频整体预填至播放缓冲，而是回到每 5 ms 泵一包。接收队列与声卡拉取批次之间的调度问题仍可能造成补零。

### 2.3 实际没有包重排窗口

位置：WifiReceiver.cs:504。

原代码离线输入 seq=0,2,1 的结果：收到 2 时立即认定丢失 1 并插入静音；真正的 1 随后到达则计迟到并丢弃。即便预留了几十毫秒缓冲也不等待重排。严重迟到的后向包还可能进入“序号重置”分支。

这在存在 UDP 乱序时会把可恢复抖动变成音频缺口；旧日志中部分会话 late=0，不能认定它是当时的主要触发因素。

## 3. 既有录音与日志复核

以下文件虽名为 .wav，旧脚本按无文件头的 float32 little-endian 双声道读取。本次沿用该格式与 48 kHz 假设；这些历史文件不含完整采集元数据，不能当作严格受控实验。

以双声道同时绝对值 <1e-7、连续至少 24 帧（0.5 ms）、排除首尾静音为条件：

| 文件（C:/Users/Public） | 估算时长 | 内部近零片段 ≥0.5 ms | 最长内部近零 |
| --- | --- | --- | --- |
| pc_direct.wav | 20.55 s | 0 | 0.146 ms |
| misound.wav | 11.95 s | 5 | 10 ms |
| remote_submix.wav | 11.96 s | 0 | 0 ms |
| loopback_out.wav | 13.32 s | 8 | 10 ms |
| transmitted.wav | 15.00 s | 8 | 10 ms |

misound.wav 的五个片段起点约为 1.941、2.976、6.056、6.576、8.621 s，长度为 5、5、5、5、10 ms。这与程序的插静音粒度吻合，但还没有事件级同步来唯一归因。

旧回环测试存在 foreign 大量增加，说明测试流与手机流曾竞争会话，因此不能据此宣称 PC 链路已经排除；该结果同样不能单独证明所有缺口来自 PC。

历史日志最新一段 Wifi.Stats 在 2026-10-30 03:29:58.707 仍报告 lossPct=0、missing=0、underruns=18、discontinuities=4、targetMs=60；03:30:00.987 又记录 Audio.Underrun 与 Audio.Rebuffer。欠载计数定义有缺陷，不能把这些数直接转换为可听断点数，但它显然不支持“欠载问题已彻底解决”。

## 4. Android 端的次要风险（未确定真机发生率）

- PcmRecorder.kt:203：短读直接补零到 960 字节，没有继续攒满。READ_BLOCKING 正常应满足请求，故不能假设短读频发；但异常短读一旦发生就会向内容插零。
- PcmRecorder.kt:275：采集线程按音频时钟生产，发送线程按固定 5 ms 系统时钟消费；长期没有根据环形缓冲占用平滑校正时钟漂移。环空补零、环满丢最旧包，都可能造成内容不连续，网络 seq 仍可连续。
- PcmRecorder.kt:294：parkNanos 返回后没有循环检查发送截止时间；调度延迟后会追赶发送。需要发送节奏/环空/溢出计数确认影响。
- PcmRecorder.kt:235：AudioTimestamp.TIMEBASE_MONOTONIC 与 elapsedRealtimeNanos（含深睡）混用；可能使硬件锚点反复失效并退回估算时间，影响抖动估计。不是“PCM 字节被音效修改”的证据。
- UdpSender.kt:sendAudio：发送失败时 seq 不递增，下一次有效包会复用该序号，从而使丢包统计漏计发送失败对应的音频内容。

## 5. 对旧音质归因的纠正

`cmp_all.py` / `perceptual.py` 分别对不同时长、未对齐音乐片段统计 RMS p90/p10；片段内容、静音筛选和音量都会影响结果。23.4、2.8、6.8 这些比值本身不能证明动态压缩，更不能换算成“音质提升 2.4 倍”。频谱平坦度也不是音乐噪声的独立测量值。

compare_audio.py 的对齐过程还存在明确偏移错误：在 transmitted 的 best_start 处取模板后，最终比较没有从匹配 offset 中减掉 best_start。align_compare.py 使用 8 样本搜索步长，且没有增益匹配、采样率漂移校正、分段时移校正，差分误差不宜直接解释为失真。

手机音效、播放器 DSP、采集路由、Windows APO/共享模式重采样都仍是候选因素，需要相同源文件、相同片段、匹配增益和时间轴后验证。

## 6. 后续修复与验证顺序

1. 增加播放 provider 实际读取/补零、主动插零/删样、手机环空/溢出/短读/发送失败计数，并使计数可以同步到录音。
2. 用固定充足预缓冲做隔离对照，停止常规自适应阶段直接插静音/删音频；统一队列与声卡驱动的消费模型，重缓冲时正确预填。长期时钟偏差用平滑采样率校正处理。
3. 加入有界包重排与播放截止时间，播放前才判定真正缺包。
4. 用相同已知测试信号和歌曲片段，分别保存手机采集后、网络接收后、播放 provider 实际输出的数据，逐段比较。录音旁附真实格式、起止时刻、音量与路由元数据。
5. 完成上述隔离后，再做 MediaProjection / scrcpy 路径的受控 A/B；不通过扩展器或低通去掩盖尚未定位的断点。

## 7. 复现资料与依据

离线复现位于 `C:/Users/Public/BlueAudioSink-quality-audit/`：Audit.csproj、Program.cs、inspect_evidence.py。Audit.csproj 直接链接产品源文件，复现针对本次提交版本；以后产品源码变化会改变结果。运行 `dotnet run --project Audit.csproj -c Release`，以及 `python inspect_evidence.py`。

官方接口依据：
- NAudio 2.2.1 BufferedWaveProvider：https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Core/Wave/WaveProviders/BufferedWaveProvider.cs
- Android AudioRecord：https://developer.android.com/reference/android/media/AudioRecord
- Android SystemClock：https://developer.android.com/reference/android/os/SystemClock

本次无新的现场听音验收，不声称已确定所有噪声来源或已解决音质问题。
