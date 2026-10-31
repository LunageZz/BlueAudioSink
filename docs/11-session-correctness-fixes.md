# 会话时间戳与目标连接修复

针对 main 的静态审查修复四项问题：

- Wi-Fi 接收端在首包之后以最近接受的音频时间戳为比较基准，避免固定 START 基准在约 35 分 47 秒后产生有符号溢出。保留启动旧包过滤，以及正常的 UDP 重排和序号处理。
- 播放器启动返回成功/失败。失败时接收器明确进入 Faulted 并分离输出对象，不再误置 playing 或继续向失败设备堆积音频。检查设备后重新连接可重建输出。
- Android 只认可目标电脑地址、PC 控制端口 45678 发来的心跳；回调携带目标快照，连接状态层再次核对，过滤切换目标前的延迟回调。音频目标端口仍可自定义。
- 发送期间拒绝切换电脑，提示先停止发送；发现广播也不会在发送中改变音频端口，保持停止按钮与采集状态一致。

## 回归验证

- Windows 产品 Release 跨平台构建：0 警告、0 错误。
- `tools/AudioRegression`：22 项通过。新增 75 分钟模拟时间戳跨界、跨回绕启动旧包过滤、IWavePlayer 启动异常注入。模拟时间不等同于 75 分钟真机传输；纯 PCM 测试不启用 Windows MMCSS。
- Android 新增 4 项 JUnit 检查，位于 `android/app/src/test`。覆盖当前目标控制端口过滤、旧目标回调、发送中目标切换，以及发现广播更改端口。在临时 JVM 环境直接编译产品连接/网络源码和这些测试，使用 Android 平台及偏好存储桩，4 项通过。
- 完整 Android SDK 环境可执行 `cd android && ./gradlew :app:testDebugUnitTest :app:assembleDebug`。本次没有完成完整 APK 构建，也未进行 Windows WASAPI 或 Android 真机验收。
