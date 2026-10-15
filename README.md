# BlueAudioSink

让 Windows 电脑作为蓝牙音频接收端，把手机的声音传过来用电脑的耳机/音箱播放。

本项目是**全新实现**，不是对旧 WinUI 项目的修补。核心修正只有一个：

> 旧实现把 `OpenAsync` 返回 Success 当成了"已连接"。但那只是**控制层**成功，
> **不代表手机已经在发送音频**。于是界面显示"已连接"，而用户听到的是静音。

## 它到底能保证什么

| 层面 | 由谁决定 | 本项目 |
| --- | --- | --- |
| 控制层：Windows 是否准备好接收 | 本程序 | 可完全控制（持久连接对象、退避重试、禁止拆连重建） |
| 链路层：手机是否开始发送音频 | **手机端** | 只能**检测并归因**，无法强制 |
| 音频层：是否真的在出声 | Windows 音频引擎 | 可检测默认输出端点峰值，可纠正输出路由 |
| 音频质量：卡顿/丢包 | 蓝牙驱动 + 射频环境 | **无法干预**（不自己写 A2DP 解码器） |

**因此本项目不承诺"音频一定不卡顿、一定能传进来"**，它承诺的是：连接稳定、失败可归因、
音频流向可检测可纠正，并且**不会用假的"已连接"骗你**。

## 四段状态

旧实现只有"已连接/未连接"两态，信息量为零。本项目把状态拆成四段：

```
Idle → Busy → Armed(控制层就绪) → LinkReady(手机端点出现) → Flowing(峰值>0，确实在出声)
                    ↑ 手机没播放时停在这里，界面明确写"等待手机发送音频"
```

- **Armed**：`StartAsync` 与 `OpenAsync` 成功。显示"已就绪，等待手机发送音频"，**不显示"已连接"**。
- **LinkReady**：手机的 A2DP 音频端点在系统中出现（这是链路真正建立的标志）。
- **Flowing**：默认输出端点峰值大于阈值，确认音频到达。
- **Faulted**：失败并给出具体排查建议。

## 实测基线与已核实结论

| 项 | 实测结果 |
| --- | --- |
| 控制层连接 | `OpenAsync` 返回 Success，约 1.9 秒，随即 `State` = Opened |
| 手机未播放时 | 45 秒内峰值恒为 0，系统端点列表中**没有任何 A2DP 端点出现** |
| 手机端点形态 | 只有在链路建立时才出现；未连接时全机 48 个端点中也没有 |
| 发布体积 | **单文件 10.67 MB**，无 .NET 运行时依赖（旧 WinUI 版为 162.5 MB / 448 文件） |
| 托盘常驻 | 工作集约 37 MB，空闲 CPU 接近 0% |

## 使用

```powershell
# 托盘常驻（默认；双击 exe 也是这个模式，不会弹控制台窗口）
.BlueAudioSink.exe

# 只列出可接收的已配对设备
.BlueAudioSink.exe --list

# 连接并实时打印状态（Ctrl+C 退出）
.BlueAudioSink.exe --connect

# 诊断：采样输出三层归因（默认 30 秒）
.BlueAudioSink.exe --diagnose 30

# 指定采样时长/查看帮助
.BlueAudioSink.exe --help
```

托盘右键菜单：连接/重连、断开、**切换输出到物理设备**、诊断、打开日志目录、开机自启开关、退出。

### 排查"连上了没声音"

按顺序检查，本项目会直接告诉你卡在哪一段：

1. **状态停在 Armed** → 控制层已就绪，**手机没有发送音频**。请确认：
   - 手机正在播放；
   - 手机蓝牙输出指向本机；
   - Windows「蓝牙和其他设备」中该设备的属性里，允许其连接用于音频。
2. **状态到达 LinkReady 但仍听不到** → 音频被其他输出设备抢占了。
   本机有 5 个活动输出端点 + 3 个虚拟声卡，很容易被抢。
   用托盘菜单的「切换输出到物理设备」，或手动到「设置 → 系统 → 声音」选择正确输出。
3. **状态到达 Flowing** → 音频确实到达电脑，问题在后续的播放链路。

## 构建

需要 .NET 8 SDK（本项目在 VS 2022 17.14 + dotnet SDK 9.0.312 上验证）。

```powershell
dotnet build .BlueAudioSink.sln -c Debug -p:Platform=x64
dotnet publish .srcSinkLiteSinkLite.csproj -c Release -p:Platform=x64
```

发布产物：`srcSinkLiteindRelease
et8.0-windows10.0.19041.0win-x64publishBlueAudioSink.exe`（约 10.7 MB 单文件）。

### 构建上的两个坑（已在项目内规避）

1. **绝不能改 `OutputType` 为 `WinExe`**：SDK 会因此引入 `Microsoft.WindowsDesktop.App`，实测体积 10 MB → 44 MB。
   托盘是不弹黑窗的需求，改用发布后 `editbin /SUBSYSTEM:WINDOWS` 实现。
2. **绝不能引用 `NAudio` 主包**：它会连带 `NAudio.WinForms`，进而把 WPF 运行时拖进发布产物。
   只引用 `NAudio.Wasapi`（实测 51.8 MB → 10.67 MB）。

## 诊断工具

```powershell
dotnet run --project .	oolsSinkProbe -- list        # 列出可接收设备
dotnet run --project .	oolsSinkProbe -- endpoints   # 列出全部音频端点（含未插入/禁用）
dotnet run --project .	oolsSinkProbe -- meter 10    # 读默认输出峰值
dotnet run --project .	oolsSinkProbe -- watch 60    # 只观察端点变化，不做连接动作
dotnet run --project .	oolsSinkProbe -- open 30     # 武装+打开并观察状态
```

## 配置

`%LocalAppData%BlueAudioSinksettings.ini`

```ini
deviceid=              # 留空则使用第一个可用设备
autoconnect=1          # 启动后自动连接
sampleseconds=2        # 状态采样间隔
```

日志：`%LocalAppData%BlueAudioSinklogssink-YYYYMMDD.jsonl`（保留 7 天，单文件上限 8 MB）。

## 明确不做的事

- 不自己实现 A2DP 解码/播放（那只会引入新的卡顿与延迟）。
- 不做应用内配对；请先在 Windows 蓝牙设置中配对手机。
- 不做多设备同时混音。
- 不承诺修复射频层卡顿（Wi-Fi 2.4G 共存、USB 3.0 干扰、手机省电策略）。

## 项目结构

```
src/SinkLite/
  Program.cs              入口：控制台释放、单实例、模式分发
  Core/Models.cs          四段状态与数据模型
  Core/Policy.cs          全部可调参数（超时/退避/阈值）
  Core/SinkController.cs  四段状态机（连接对象持久化、禁止拆连重建）
  Core/FlowMonitor.cs     三层归因（控制层/链路层/音频层）
  Tray/TrayHost.cs        托盘：Shell_NotifyIcon + 隐藏窗口
  Platform/EndpointWatcher.cs  音频端点观察与峰值读取
  Platform/RouteControl.cs     默认输出端点切换、虚拟声卡识别
  Platform/Log.cs              滚动 JSONL 日志
  Platform/Settings.cs         配置读写
  Platform/AutoStart.cs        开机自启（HKCU Run）
tools/SinkProbe/          独立诊断工具
```
