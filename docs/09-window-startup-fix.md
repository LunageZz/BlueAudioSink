# 启动窗口与单实例修复（2026-10-30）

现象：进程与 Wi-Fi 日志持续运行，但没有可见/可定位的主窗口；重复启动遇到 Global mutex 后静默退出。

实施顺序：先检查 RegisterClassEx/CreateWindowEx 返回值并记录 Win32 错误，禁止窗口创建失败后进入空消息循环占据单实例；重复启动时查找并恢复现有主窗口，等待首次启动完成后重试；启动失败在日志与窗口提示中显示具体原因。最终通过桌面窗口截图、关闭后重新启动、最小化后重复启动验证，再更新本地 Release。

## 完成与核验

- 为 RegisterClassEx/CreateWindowEx 增加返回值检查和具体 Win32 错误，防止窗口失败后空转并持有单实例。
- 第一次 ShowWindow 之后再次显式显示，避免启动器 SW_HIDE 参数压住界面；微软行为说明：https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow 。
- 已有实例时最多等待 1.5 秒查找主窗口，恢复并激活；没有可恢复窗口时显示原因，不静默退出。
- 桌面 Computer Use 实测：正式 publish 路径打开成功；最小化后从 artifacts 的 EXE 再次启动，恢复同一窗口且仍只有一个进程；Alt+F4 后窗口和进程退出；从交付 EXE 冷启动重新出现窗口。
- Release 已同步到 artifacts/BlueAudioSink-0.1.0.exe、artifacts/pc/BlueAudioSink.exe 和原 publish 路径。最终运行的是交付 EXE。
