# 0.3.3 正式发布审查与验证

发布日期：2026-10-31。双端版本 `0.3.3`，Android versionCode=7。目标分支 `main`，标签 `v0.3.3`。

## 审查范围与处理

以审查时的 `origin/main`（`d8c88354b4e23f008bbab2d0573282c178d9cecc`）为基线，包含手机麦克风功能分支及本次协作的未提交修改、新增源码、测试、文档。规格来自本次用户需求与 docs/12–16；分别执行 Standards 与 Spec 审查，再复核修复。

### Standards

- 发现旧设置快照分开读取和保存，可能覆盖远端刚同步的静音/音量。已将全部产品设置写入统一为 `Settings.Update`，同一锁内读取、修改、写入，删除公共 `Save`，从 API 上阻止旧快照整份写回。界面在更新后重读最新快照。
- 发现 BluetoothMicrophoneReceiver 在 Stop 的 Join 超时后允许新线程启动，旧线程 finally 可能误清理新服务。现在旧 worker 存活时拒绝重复启动，明确提示关闭中；Stop 检查 Join 结果。
- 两项修复复审通过，无剩余可确证的发布阻断项。

### Spec

- 默认蓝牙只传麦克风、媒体保留系统 A2DP、独立媒体副本授权、设备记忆/预选、固定发送按钮、双端关于及中文 README 与要求一致。
- 同样发现设置持久化问题，修复复审通过。双端正式版本已统一，历史 Candidate 记录不作为本次全部硬件验收的证明。

## 构建与检查

| 检查 | 结果 |
| --- | --- |
| C# 音频回归 | 35/35 通过，新增真实 Join 超时回归在修复前失败、修复后通过；测试不会绑定真实蓝牙服务 |
| 防火墙回归 | 6/6 通过，仅假 COM 存储及真实只读查询，不修改系统规则 |
| Android Release 单元测试 | 19/19 通过 |
| Android UI 回归 | 专用 API 36 模拟器 1/1 通过，长配对列表下发送按钮仍可见 |
| Android Release/lint | assembleRelease、lintRelease 通过，无 Error；保留兼容性、依赖更新及风格建议（11 Warning、1 Hint） |
| Windows 发布 | .NET SDK 8.0.427，Release/win-x64，自包含、单文件、裁剪；保留现有 NAudio/COM 裁剪警告 |
| APK 签名 | 沿用本仓库本地 Release 密钥，发布流程验证 APK v2 签名、versionCode=7、versionName=0.3.3，未启用 debuggable |
| 驱动包 | 官方 VB-CABLE Pack45 原 ZIP，SHA-256 与产品固定值一致，x64 安装器 Authenticode 签名有效 |
| 文档与产物 | 当前 README 指向 main/v0.3.3，附第三方许可、来源及 Donationware 说明；公开包不含签名密钥、密码或本机配置 |

源码与 Release 附件对应同一发布标签；发布时生成的构建清单记录准确 commit、文件 SHA-256 和 APK 证书指纹。临时下载、测试和构建证据集中在 `artifacts/`，不入库。

## 发布附件

- `BlueAudioSink-0.3.3.exe`
- `BlueAudioSink-Sender-0.3.3.apk`
- `VBCABLE_Driver_Pack45.zip`（官方原包，不修改）
- `BlueAudioSink-0.3.3.zip`（完整分发，包含两端、驱动原包、许可和使用说明）
- `SHA256SUMS.txt`

VB-CABLE 原包许可允许原样分发；最终用户可清楚识别 VB-Audio 来源并自行捐赠/购买许可。驱动只由用户显式运行其独立安装器，不静默安装。详见根目录第三方说明。

## 已确认的现场反馈与边界

用户已确认此前 Candidate 的蓝牙麦克风可用；手机系统媒体输出选择 LUNAGE 后能播放媒体。另一次无声在关闭麦克风时仍发生，完整释放并重建 A2DP 后恢复；直接输入峰值从持续为零变为约 0.04–0.36，输出也恢复。

底层 A2DP 偶发无声原因尚未完全确认，正式版保留手动恢复，不增加可能打断正常暂停的定时重连。新恢复按钮与麦克风长期并行、不同适配器/手机/会议软件矩阵及长时间压力测试未全部执行。正式版本号不表示上述未执行项目已验收。
