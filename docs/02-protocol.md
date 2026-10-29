# 播放行为修订 v1.2（2026-10-30）

包头、类型、PCM 格式不变。以下 §6/§7 及手机真实 PCM 出包规则替代 v1.1 的插零/裁剪/立即判丢机制；历史裁决表与旧验收表中对应条目仅作记录，以本修订为准。

# 传输协议 v1（两端唯一契约）

> 本文件是 PC 端与 Android 端的**唯一事实来源**。任何一端实现前必须先读本文件；
> 实现与本文不符时，以本文为准（或先修订本文并同步另一端）。
>
> **文档修订 v1.1（2026-10-30）**：本版含一次**线格式修订**：AUDIO 包由 10 ms / 1920 字节改为 **5 ms / 960 字节**（避免 IP 分片）；同时澄清 PING/PONG 方向、seq/时间戳与抖动缓冲语义。type 编号、端口与包头不变。初稿中与此冲突的表述一律作废，以本版为准。
> 术语统一为：**采样时刻、发送时刻、到达时刻、会话、抖动、抖动缓冲深度、断流、丢包、待机、端点**。

## 0. 术语

| 术语 | 定义 |
| --- | --- |
| 采样时刻 | 一个 PCM 帧被手机采集到的时刻，取自手机单调时钟，不是发送时刻、不是读取完成时刻 |
| 发送时刻 | 手机或 PC 把报文交给 UDP 栈的时刻，取自各自单调时钟 |
| 到达时刻 | PC 从 UDP socket 读出报文的时刻，取自 PC 单调时钟 |
| 会话 | 从 START（或首个 AUDIO 自动建立）开始，到 STOP 排空结束或 5 秒超时为止的一段连续音频流 |
| 抖动 J | 相邻两个 AUDIO 包的“到达间隔”与“采样时刻间隔”之差绝对值的平均，见 §6 |
| 抖动缓冲深度 D | PC 已入队、尚未播放的音频时长，单位毫秒或微秒，见 §6 |
| 断流 | 音频时间轴上出现可感知的缺口或内容不连续；分为“网络断流”（丢包导致）与“采集断流”（手机侧内容不连续） |
| 丢包 | 手机已产出但 PC 未收到的 AUDIO 包，由 seq 缺口判定 |
| 待机 | PC 已就绪、尚无会话的状态；Wi-Fi 模式下仍保持端口监听与 OFFER 广播 |
| 端点 | 一次会话对应的手机 UDP 源地址与源端口，按本规范固定为“手机 IP:45678” |

## 1. 网络、端口与套接字

| 用途 | 协议 | 端口 | 谁绑定 | 方向 |
| --- | --- | --- | --- | --- |
| 发现 / 控制 / 保活 | UDP | **45678** | PC 控制 socket；手机唯一 socket | 双向 |
| 音频 | UDP | **45679**（可由 OFFER 覆盖） | PC 音频 socket | 手机 → PC |

**强制规则（两端逐条实现）：**

1. 仅使用 **IPv4 + UDP**，v1 不支持 IPv6。
2. PC 在 `0.0.0.0:45678` 绑定控制 socket，在 `0.0.0.0:45679` 绑定音频 socket。两端口均需允许入站。
3. 手机只创建一个 UDP socket，绑定 `0.0.0.0:45678`，**所有收发都走它**：接收 OFFER/PING，发送 HELLO/START/STOP/PONG/AUDIO。绑定失败必须提示“端口 45678 被占用”并拒绝开始会话。
4. 手机把 HELLO/START/STOP/PONG 发往 `PC:45678`；把 AUDIO 发往 OFFER 携带的音频端口（手动输入 IP 时缺省 `45679`）。
5. PC 的 OFFER 与 PING 都从自己的 45678 socket 发出。PING 的目的地址：**有会话时取当前会话端点；待机时取最近一次有效手机报文的源 IP:源端口**。按本规范即 `手机IP:45678`。
6. PC 只接受**当前会话端点**的 AUDIO。其他源地址/端口的 AUDIO 丢弃并计入 `foreign`，不得混入播放。
7. 两端必须处在同一子网、无 NAT；Wi-Fi AP 必须关闭“客户端隔离”，PC 防火墙必须放行 UDP 45678/45679。
8. PC 收到自己广播回来的 OFFER 时直接忽略（type=6 不是入站控制报文）。

> 说明：手机固定本地端口 45678 是**澄清**而非新增字段。它让 PC 不必猜测手机源端口，也让 OFFER/PING/PONG 的往返路径唯一。

## 2. 统一包头（16 字节）

| 偏移 | 长度 | 字段 | 类型 | 说明 |
| --- | --- | --- | --- | --- |
| 0 | 4 | magic | bytes | ASCII `"BAS1"`（0x42 0x41 0x53 0x31） |
| 4 | 1 | type | u8 | 见下表 |
| 5 | 1 | flags | u8 | bit0 = 发送端内容断流标记（DISCONTINUITY）；其余位 v1 必须为 0 |
| 6 | 2 | reserved | u16 | 发送端必须为 0；接收端必须忽略 |
| 8 | 4 | seq | u32 | 仅 AUDIO 有意义；其他 type 必须为 0 |
| 12 | 4 | timestampUs | u32 | 微秒，低 32 位；语义随 type 而定，见下表 |
| 16 | N | payload | bytes | 依 type 而定 |

**type 定义（v1.1 修订后）**

| 值 | 名称 | 方向 | payload | 总长 |
| --- | --- | --- | --- | --- |
| 1 | AUDIO | 手机→PC | PCM，**恰好 960 字节** | 976 |
| 2 | START | 手机→PC | 空（v1 必须为空；接收端忽略多余尾随字节） | 16 |
| 3 | STOP | 手机→PC | 空（同 START） | 16 |
| 4 | PING | **PC→手机** | 空 | 16 |
| 5 | PONG | **手机→PC** | 4 字节：**原样回填**对应 PING 的 timestampUs | 20 |
| 6 | OFFER | PC→手机广播 | UTF-8 设备名 + `0x00` + 4 字节音频端口（u32） | 16+len+5 |
| 7 | HELLO | 手机→PC | UTF-8 设备名，无 `0x00` 结尾 | 16+len |

> **勘误 1**：初稿 §2 表把 PING 写成“手机→PC”、PONG 写成“PC→手机”，与 §4 时序图、§5 公式矛盾。
> 本版按 §4/§5 及“PC 端展示 RTT/延迟”的需求裁定为 **PC 发 PING、手机回 PONG**。字段长度与 type 编号不变。
>
> **勘误 2**：初稿把 PONG 载荷写作“PC 接收时刻”，但 PONG 由手机发出，手机不可能知道 PC 的接收时刻。
> 本版把 4 字节载荷定义为 **PING.timestampUs 的原样回填**；手机自己的发送时刻放在 PONG 的 `timestampUs` 头字段。

**各 type 的 seq / timestampUs 取值**

| type | seq | timestampUs |
| --- | --- | --- |
| AUDIO | 会话内从 0 开始逐包 +1（见 §3） | 该包**首帧的采样时刻** |
| START | 0 | 手机发送 START 的时刻 `T_start` |
| STOP | 0 | 手机发送 STOP 的时刻 `T_stop` |
| PING | 0 | PC 发送 PING 的时刻 `T1` |
| PONG | 0 | 手机发送 PONG 的时刻 `T2` |
| OFFER | 0 | PC 发送时刻（接收端可忽略） |
| HELLO | 0 | 手机发送时刻（接收端仅记录） |

**通用规则：**

1. 所有多字节整数为**小端**；seq 与 timestampUs 的运算一律按 **u32 模 2³²** 进行。
2. 发送端把 `reserved` 置 0、`flags` 未定义位清 0；接收端必须忽略 `reserved` 与未知 flags 位，不得因非 0 而丢包。
3. 接收端校验顺序：`长度 ≥ 16` → `magic` → `type` 已知 → 该 type 的 payload 长度。任一不符即丢弃并计数（`malformed`），禁止把残缺包送进播放链路。
4. 未知 type：丢弃并计数（`unknown`），不得崩溃、不得当作 AUDIO。
5. AUDIO 必须总长 976；PONG 必须总长 20；HELLO 的 payload 长度 1~64；OFFER 的 payload 至少 5 字节且必须能解析出 NUL 与 4 字节端口。
6. 同一 UDP 报文不得包含多个包；不做粘包/拆包。
7. `0` 是合法的 u32 值，不得把 seq=0、timestampUs=0 或 PONG 回填值 0 当作“未设置”的哨兵；唯一允许的“未设置”是 PC 内部的 `expectedSeq = null`。

## 3. 音频格式与分包

| 项 | 值 |
| --- | --- |
| 编码 | PCM 有符号 16-bit 小端 |
| 采样率 | **48000 Hz** |
| 声道 | **2（立体声，交错 L/R）** |
| 每包时长 | **5 ms** = 240 帧 = **960 字节** payload |
| 包总长 | 16 + 960 = **976 字节** |
| 包速率 | **200 包/秒** |
| 有效载荷带宽 | 约 **1.55 Mbps** |

**分包规则（手机端必须遵守）：**

1. 每次只发送 **240 帧**；采集读取不足 240 帧时必须攒够再发，不得发送半包。
2. 会话最后一包不足 240 帧时，用 **0 采样补齐到 240 帧**后发送；其 timestampUs 仍取该包首帧的真实采样时刻。
3. 正常音频为 **200 包/秒**。真实采集的静音照常发送；采集线程暂时没交出完整包时等待，不合成静音包、不伪造采样时间。PC 以缓冲吸收等待，真正持续 5 秒无包才超时。
4. seq 在**每个 AUDIO 包产出时**加 1。若因手机发送队列溢出而丢弃某个未发送包，该包的 seq **仍然被消耗**（下一包继续 +1），让 PC 能按缺口补静音；禁止“只给已发送包编号”导致时间轴被压缩。
5. 手机发送队列上限 **30 ms（6 包）**；溢出时丢最旧的未发送包并计数，不得阻塞采集线程。
6. 若手机采集格式不是 48 kHz/16-bit/立体声，手机负责重采样、混音/下混与格式转换；v1 不做格式协商。转换必须保持声道顺序 L/R 与不削波。
7. 协议层不做增益、降噪、AGC、AEC；手机发送采集到的原始 PCM（仅做必要的格式转换）。

**timestampUs 的精确语义：**

1. 等于该 AUDIO 包**第 0 帧被采集到的时刻**，单位微秒，取手机**单调时钟**（`SystemClock.elapsedRealtimeNanos() / 1000`）的低 32 位。
2. **不是**发送时刻、**不是** `AudioRecord.read()` 返回时刻、**不是** `System.currentTimeMillis()`（墙上时钟会被 NTP 调整）。
3. 应从采集帧位置推导：会话首帧建立基准后，第 k 包首帧的时刻 = 基准 + 该帧在采集流中的偏移。相邻包在时间轴上必须严格相差 **5000 µs**（32 位回绕除外），误差 ≤ 1 ms。
4. 会话首包的 timestampUs **没有固定值**，不得把 0 当作“未设置”的特殊值。
5. 32 位微秒每 **4294.967 秒（约 71.6 分钟）** 回绕一次。两端计算差值时必须用 u32 模运算，**不得把回绕判为断流或 seq 跳变**。

**线格式约束（强制）：** 单包 IP 层长度必须 **< 1500 字节**，禁止 IP 分片。v1 的 5 ms 包为 976 字节 UDP 载荷 + 8 字节 UDP 头 + 20 字节 IPv4 头 = **1004 字节**，不分片。两端**不得**擅自改变每包帧数或包长；任何包长变更都必须先改本文件并同步两端。

> 初稿的 10 ms / 1920 字节包在 IP 层为 1964 字节，会分片，v1.1 已废弃。

## 4. 会话状态与时序

### 4.1 开始会话

手机端顺序（必须严格按此顺序）：

1. 用户点击“开始” → 取得 MediaProjection 授权 → 创建 AudioRecord 成功。
2. 发送 `HELLO`（可选，每个会话最多一次；用于 PC 提前显示设备名并开始 PING）。
3. 发送 `START`：`seq=0`，`timestampUs=T_start`（手机单调时钟，发送 START 的时刻）。
4. 开始采集并按 §3 发送 AUDIO，首包 `seq=0`。

约束：

- START 必须在首个 AUDIO 之前发出；正常情况下 `T_start ≤ 首包采样时刻`，两者相差超过 100 ms 视为实现错误。
- START **只发一次，不重传**。START 丢失不会导致会话失败：PC 在待机态收到首个合法 AUDIO 时会自动建立会话（见 4.2）。
- HELLO 丢失无影响；PC 也可以从 START/AUDIO 的源地址学习端点。

PC 收到合法 START：

1. 把会话端点绑定为 START 的源 IP:源端口；若已有会话，立即丢弃旧会话队列与播放器状态。
2. 记录 `T_start`，清空抖动缓冲、seq 基线、抖动窗口与会话统计。
3. 进入“接收中”，开始预缓冲（阈值见 §6）。
4. 之后只接受该端点、且 timestampUs 不早于 `T_start − 100 ms` 的 AUDIO。

### 4.2 会话中

- 手机按 §3 连续发送 AUDIO，包括静音。
- PC 每收到一个合法 AUDIO，重置 5 秒无包定时器。
- **START 丢失时的自动建会话**：PC 处于待机（无会话）时收到首个合法 AUDIO，即建立会话；此时 `expectedSeq` 取该包的 seq（不要求为 0），不做 `T_start` 过滤。这样 START 丢失只损失最多若干首包，不会导致整场无声。
- PC 处于“排空中”或“故障”时收到 AUDIO：丢弃，不自动建会话（必须等 START）。
- 非当前会话端点的 AUDIO：丢弃并计 `foreign`。

### 4.3 结束会话

手机端：

1. 停止采集；若最后一包不足 240 帧，按 §3 补 0 后发送。
2. 发送 `STOP`：`seq=0`，`timestampUs=T_stop`，**只发一次，不重传**。
3. 不再发送任何 AUDIO。socket 保持打开，继续应答 PING（用于 PC 判断“手机还在线”）。

PC 收到合法 STOP：

1. 进入“排空中”：停止接受新 AUDIO（之后到达的 AUDIO 一律丢弃并计 `late`）。
2. 继续播放队列中剩余音频，直到抖动缓冲深度 D = 0；再等待**最多 50 ms**（WasapiOut 输出缓冲）或播放器空闲信号，确保最后一段真正播出；总排空时间仍不超过 500 ms。
3. 然后释放播放器，回到**待机**；Wi-Fi 模式下不关闭 socket、不停止 OFFER（仍可被下一次 START 唤醒）。
4. 排空最长 **500 ms**；超时则丢弃剩余数据并强制回待机。

STOP 丢失由 5 秒无 AUDIO 超时兜底（见 4.4），不要求重传或 ACK。

### 4.4 超时与异常

| 情况 | PC 行为 |
| --- | --- |
| 接收中连续 **5 秒** 未收到合法 AUDIO（PING/PONG 不算） | 丢弃队列、释放播放器、回待机；记录 `Wifi.Timeout` |
| 收到 START 后 **5 秒** 仍无任何 AUDIO | 同上（定时器从 START 起算） |
| 排空中超过 500 ms 仍未排空 | 强制丢弃剩余、回待机 |
| 手机应用被杀 / 断网 / 离开 Wi-Fi | 由上述 5 秒超时兜底 |
| 手机采集失败且已发 START | 手机立即发 STOP；发不出去则由 PC 超时兜底 |

### 4.5 快速重启与陈旧包

- **任何合法 START 都立即开启新会话**，无论 PC 当前处于待机、接收中、排空中还是故障态；旧队列、旧 seq 基线、旧抖动窗口全部丢弃。
- PC 必须丢弃 timestampUs 早于 `T_start − 100 ms` 的 AUDIO（u32 有符号差判断），计 `stale`。这防止上一场会话的延迟包污染新会话。
- STOP 之后、下一个 START 之前到达的 AUDIO 一律丢弃。
- 新会话的 seq 重新从 0 开始；PC 不要求首包一定收到，基线取实际收到的首个 AUDIO。

### 4.6 多手机

- 新手机的 START **可以抢占**当前会话：PC 切换端点到新源地址，旧会话队列立即丢弃；旧手机之后的 AUDIO 计 `foreign` 并丢弃。
- 非 START 的 AUDIO/HELLO **不能**抢占当前会话。
- PC 同一时刻只播放一个端点；不支持混音。

### 4.7 PC 会话状态机（实现必须等价）

| PC 状态 | 进入条件 | 收到合法 AUDIO | 收到 START | 收到 STOP | 无包超时 |
| --- | --- | --- | --- | --- | --- |
| 待机（Armed） | 模式就绪、无会话 | 自动建会话 → 接收中 | 建会话 → 接收中 | 忽略 | 不适用 |
| 接收中（Receiving） | START 或自动建会话 | 接受、入缓冲 | 重置为新会话 | 转排空中 | 5 s → 待机 |
| 排空中（Draining） | 收到 STOP | 丢弃（late） | 重置为新会话 | 忽略 | 500 ms → 待机 |
| 故障（Faulted） | 端口/播放器不可用 | 丢弃 | 重试建会话 | 忽略 | 不适用 |

> Faulted 分两类：**端口/监听故障**（收不到包，只能由用户重选模式恢复）与**播放器故障**（仍能收包，下一次 START 可重试）。两类都必须停止 PING，让手机在 6 秒内看到“PC 未响应”。

```
手机                                        PC
 |---- HELLO（可选） --------------------->|
 |---- START(ts=T_start) ----------------->|  新会话：清缓冲、重置 seq 基线
 |---- AUDIO(seq=0, ts) ------------------>|  预缓冲
 |---- AUDIO(seq=1, ts) ------------------>|  入抖动缓冲，按序播放
 |         ...                             |
 |<--------------------- PING -------------|  每 2 秒（PC 发起）
 |---- PONG(payload=T1, ts=T2) ----------->|  原样回填 + 手机发送时刻
 |---- STOP(ts=T_stop) ------------------->|  排空剩余后回待机
```

## 5. 时钟、PING/PONG 与延迟估算

### 5.1 时钟源

| 端 | 时钟 | 要求 |
| --- | --- | --- |
| 手机 | `SystemClock.elapsedRealtimeNanos() / 1000` | 单调、不受 NTP/时区影响；取低 32 位 |
| PC | `Stopwatch` 或 `Environment.TickCount64` 换算微秒 | 单调、不受系统时间调整影响；取低 32 位 |

所有时间差用 u32 模 2³² 计算，再把结果按 `int32` 解释为有符号微秒差（适用于 < ±35 分钟的差值）。**禁止**用 `DateTime.Now` / `System.currentTimeMillis()` 参与协议计时。

### 5.2 PING / PONG

- PC 在 **Wi-Fi 模式已启用、未处于故障态、且最近 10 秒内收到过该手机任意合法报文**时，每 **2 秒**向该端点发送一次 PING。
- PING：`seq=0`，`timestampUs=T1`（PC 发送时刻），payload 空。
- 手机收到 PING 后，必须在 **5 毫秒内**（这是响应时限，与音频包时长无关）向该 PING 的源 IP:源端口回 PONG（不得排队到音频线程之后）：
  - PONG 的 4 字节 payload = **原样回填** PING 的 `timestampUs`（T1），不得改写、不得填自己的时间；
  - PONG 的 `timestampUs` 头字段 = `T2`（手机发送 PONG 的时刻）；
  - PONG 的 `seq=0`。
- 手机即使处于待机（已 STOP、未开始新会话），只要 App 在运行且已选定该 PC，也应应答 PING；未选定 PC 时忽略来源不明的 PING。
- 手机仅在“已选定 PC”时回 PONG；其他 PC 的 PING 忽略。

PC 收到 PONG 后：

1. 校验源端点等于当前端点；否则丢弃。
2. 校验 payload 等于某个 **2 秒内发出的** PING 的 T1；找不到匹配则丢弃（计 `pongOrphan`）。
3. `T3` = PC 收到 PONG 的单调时刻；`RTT = u32(T3 − T1)`。
4. 仅当 `0 < RTT ≤ 2,000,000 µs` 时采纳本样本；否则丢弃（异常/过期）。
5. 时钟偏移：`offset(手机 − PC) = (int32)(T2 − T1 − RTT/2)`。保留**最近一个有效样本**；端点切换或会话重置时清空。
6. PONG 丢失**不影响播放**，只影响 RTT/延迟显示。

### 5.3 延迟显示（仅展示，不参与调度）

定义：

- `tailTs`：队列尾部对应的采样时刻，单位 µs。每接受一个 AUDIO 包，`tailTs = 该包 timestampUs + 5000`；每向队尾插入 Δ 微秒静音，`tailTs += Δ`；丢弃最旧数据不改变 `tailTs`。
- `D`：当前抖动缓冲深度（µs），见 §6。
- `playheadTs = tailTs − D`：当前播放头对应的采样时刻（手机时钟）。
- `age = u32(now_pc − (playheadTs − offset))`：该采样从被采集到“现在”的年龄。
- `显示延迟 = age + 50 ms`。50 ms 是 WasapiOut 共享模式缓冲的固定估算值，不随设备变化。

显示规则：

- 仅当“正在播放（Flowing）”且 `offset` 有效（10 秒内有有效 PONG 样本）时显示延迟；否则显示 `--`。
- `age` 必须落在 `0 ~ 2,000,000 µs`；越界视为无效，显示 `--`，不得出现负数或离谱大数。
- RTT 显示最近一个有效样本，四舍五入到毫秒；无有效样本显示 `--`。
- 连续 **3 次 PING（6 秒）**未收到 PONG：界面显示“控制链路异常”，但**继续播放**，不得因此断音频。

> `offset` 的符号约定：`offset = 手机时钟 − PC 时钟`；把手机时间换算到 PC 时间用 `t_pc = t_phone − offset`。
> 该值只用于展示；抖动缓冲的调度只依赖 `D` 与 §6 的规则，绝不依赖 `offset`。

## 6. 抖动缓冲（PC，v1.2）

- 唯一音频消费时钟是 WasapiOut 的 IWaveProvider.Read。网络接收/重排后尽快把连续 PCM 送入 PlayoutBuffer，不再按另一个 5 ms 系统定时器搬运。
- 格式仍为 48000 Hz / 16-bit / stereo。内部 BufferedWaveProvider 使用 ReadFully=false、DiscardOnBufferOverflow=false，由 PlayoutBuffer 负责准确计数与恢复。
- target 每 500 ms 更新：`clamp(ceil((40000 + 4*J)/1000), BufferBaseMs, BufferMaxMs)`，单位 ms；默认最小 80 ms、最大恢复阈值 200 ms。J 定义仍为最近 2 秒相邻连续序号包的 mean(|到达间隔−采样间隔|)，单位 µs。
- target 只影响首次启动和真实欠载恢复；健康播放**禁止为了追 target 插静音或删除音频**。200 ms 是恢复阈值上限，不再是每包裁剪上限。
- 预缓冲达到 target 后启动。Read 缺少真实数据时记录一次 underruns，缺数帧计入 outputPaddingFrames；已经剩下的真实数据正常输出，缺口边沿淡出。
- 进入恢复后不消费新攒的 PCM，直到达到 `max(target, 本次声卡读取长度)` 再恢复，以真实首帧的双声道等增益 5 ms 淡入。恢复期间实际输出的零帧也计 outputPaddingFrames。
- 物理容量 1000 ms。达到容量时明确报错并进入 Faulted，记录 outputOverflows，不能静默丢样冒充正常播放。长期采样时钟漂移仍需长时间验证，本版不宣称已实现异步采样率校正。
- STOP 标记输入结束，排空不计欠载；播放缓冲耗尽后额外允许 100 ms 声卡尾部排空，总排空上限仍为 500 ms。新 START 清空旧会话与计数。

## 7. 丢包、乱序、断流与统计（v1.2）

- expectedSeq 表示下一包**待释放**到连续音频队列的序号，首包建立基线。按 `(int32)(seq - expectedSeq)` 处理 u32 回绕。
- 已释放的旧包全部计 late 并丢弃，绝不因严重迟到而重置。仍在重排表中的相同序号计 dup。
- 到达包先进入重排表。连续包立即释放；序号有缺口时，从缺口之后最早到达包的接收时刻开始等待 **20 ms**，期间允许缺包补到。
- 到截止仍缺失，缺口 1~10 包才以 5 ms/包补零并记 missing；真实帧恢复时淡入。补零只用于不可恢复的真实缺包，不用于普通缓冲调节。
- 截止时缺口 >10 包则跳至最近可用序号，记 discontinuities，不制造长静音。单次到达前向距离 >200 包时清理接收暂存，重新建立接收序号，记 resets/discontinuities；已交付声卡的数据不回退。旧会话区分仍依赖 START 与端点。
- flags.bit0 跟随具体 AudioFrame，一直保留到该帧送入输出，不用全局布尔量提前处理队列中的其他帧。
- missing/(received+missing) 仍按最近 10 秒计；underruns 现在代表实际 IWaveProvider.Read 缺数事件，outputPaddingFrames 代表实际补零帧，outputOverflows 代表容量故障。
- Android 环空等待真实采集，不合成带连续序号的伪静音；AudioRecord 采集到的真实静音照常发出。短读继续攒满 960 B，不填零。发送失败消耗源序号并记录 sendFailures，以便 PC 看见内容缺失。
- AudioTimestamp 与发送/控制时间戳统一为 BOOTTIME/elapsedRealtimeNanos 时基。START 必须填写发送时刻；PC 对旧版 START.timestampUs=0 兼容为“没有陈旧包时间基线”。
- Android Capture.Stats 每 1000 包记录 sourceWaits、sourceOverflows、shortReads、ringPackets。sourceWaits 是等待采集的调度次数，**不是音频丢失次数**；真实内容丢失以溢出、发送失败、PC 缺包与输出补零共同判断。

## 8. 发现（OFFER）与 HELLO

### 8.1 OFFER

- PC **仅在 Wi-Fi 模式启用时**，每 **2 秒**向 `255.255.255.255:45678` 广播一次 OFFER；从 PC 的 45678 socket 发出（源端口 45678）。切到蓝牙模式后 2 秒内停止。
- payload：`设备名 UTF-8` + `0x00` + `音频端口 u32 小端`。
- 设备名最长 **63 字节 UTF-8**（不含 NUL）；PC 必须按 UTF-8 码点边界截断，不得截断半个字符。无名字时用 `BlueAudioSink`。
- 音频端口必须是 1~65535；v1 PC 填 45679。
- 手机在 45678 socket 上监听 OFFER，按“源 IP”去重维护设备列表；展示设备名与 IP。
- 手机超过 **6 秒**（连续 3 次广播）未收到某 PC 的 OFFER，就把该设备标记为离线/移除；手动输入的 IP 不参与老化。
- 手机选择设备后，向该 IP 的 45678 发 HELLO/START，向 OFFER 携带的音频端口发 AUDIO；手动输入 IP 时音频端口用 45679。

### 8.2 HELLO

- 手机在**每个会话开始时、START 之前**发送一次 HELLO；不重传。
- payload：`设备名 UTF-8`，**无 NUL 结尾**，长度 1~64 字节；无名字时发 `Android`。超长时按 UTF-8 码点边界截断到 64 字节，不得截断半个字符；PC 解析时遇到 NUL 即截断。
- PC 收到 HELLO：记录设备名；**待机（无会话）时**把源端点作为 PING 候选；**接收中/排空中**只更新显示名，不改变当前会话端点与 PING 目标。**不回复、不改变会话状态**。HELLO 不能抢占已有会话。

### 8.3 其他

- PC 收到 OFFER（自己广播回来的）直接忽略。
- 手机收到非 OFFER 报文（PING 除外）按类型处理；OFFER 只用于发现。
- 多台 PC 同时广播时，手机展示列表，由用户选择；切换 PC 前必须先 STOP 当前会话。
- 广播被防火墙/AP 隔离阻断时，手机必须支持手动输入 IP（MVP 必需）。

## 9. 兼容与演进

1. magic `BAS1` 同时承担版本标识；v1 不做版本协商。
2. `reserved`、`flags` 未定义位、未知 type 都必须被接收端**忽略**而不是拒绝，以便前向兼容。
3. v1 的 START payload 必须为空；接收端忽略 START 的尾随字节，为未来“START payload 协商编码”留路。
4. 未来若引入 Opus：新增 `type=8 AUDIO_OPUS`，通过 START payload 协商；PCM 保持默认。该变更必须提升协议版本并同步两端。
5. 任何改变线格式（字段、长度、type、端口）的修改都必须先改本文、再改两端代码；禁止单端“先实现后补文档”。

## 10. 实现检查清单（两端逐项核对）

- [ ] 仅 IPv4/UDP；PC 绑定 45678/45679；手机单 socket 绑定 45678
- [ ] magic 为 `BAS1`，包头严格 16 字节、小端
- [ ] AUDIO 每包 240 帧 / 960 字节 / 总长 976，绝不发半包
- [ ] 会话内保持 200 包/秒，静音也发包
- [ ] 单包 IP 层长度 < 1500，禁止 IP 分片
- [ ] 采样率 48000、立体声、16-bit 小端，L/R 交错
- [ ] seq 每包 +1，会话从 0 开始；本地丢包也消耗 seq
- [ ] timestampUs = 首帧采样时刻（单调时钟低 32 位），不是发送/读取时刻
- [ ] 32 位回绕不判为断流/重置
- [ ] START 只发一次；PC 在 START 丢失时可由首个 AUDIO 自动建会话
- [ ] STOP 只发一次；PC 排空 ≤500 ms 后回待机
- [ ] 5 秒无 AUDIO 自动待机并释放播放器
- [ ] PING 由 PC 每 2 秒发起；PONG 原样回填 T1，头字段填 T2
- [ ] PONG 丢失不影响播放，只影响 RTT/延迟显示
- [ ] 抖动缓冲：初始/最小 40 ms、最大 200 ms、500 ms 评估、−5/+10 ms
- [ ] 缺口 1~10 包补静音；d>5 记断流；d>10 重置（不补静音）
- [ ] flags.bit0 只由手机置位；PC 只记录与淡入
- [ ] 非当前端点/迟到/重复/陈旧包都被丢弃并计数
- [ ] 丢包率按最近 10 秒窗口计算
- [ ] OFFER 每 2 秒广播、名字 ≤63 字节、音频端口字段生效
- [ ] 手动输入 IP 可用
- [ ] 两端日志事件名与自检表一致

## 11. 裁决记录（v1.1，供两端对照）

| 编号 | 初稿歧义/矛盾 | 本版裁决 |
| --- | --- | --- |
| D1 | §2 类型表 PING/PONG 方向与 §4/§5 相反 | PC→手机 PING，手机→PC PONG；PING/PONG 字段长度不变（AUDIO 包长另见 D15） |
| D2 | PONG 的 4 字节载荷语义不明 | 载荷 = 原样回填 PING.timestampUs；PONG 头 timestampUs = 手机发送时刻 T2 |
| D3 | 非 AUDIO 包的 seq/timestampUs 取值未定义 | seq 一律 0；timestampUs 填发送端发送时刻（START/STOP/PING/PONG/HELLO），接收端按 §2 解释 |
| D4 | PING 的目的端口/手机源端口未定义 | 手机单 socket 绑定 45678；PC 有会话时向当前会话端点、待机时向最近手机包的源 IP:45678 发 PING |
| D5 | START/STOP 是否重传、丢失怎么办 | 均只发一次、不重传；START 丢失由首个 AUDIO 自动建会话；STOP 丢失由 5 秒超时兜底 |
| D6 | 静音期间手机是否继续发包 | 必须保持 200 包/秒，采集不到数据补全 0 静音包 |
| D7 | flags.bit0 由谁置位 | 只能由手机置位，表示内容不连续；PC 只记录、淡入，不置位 |
| D8 | timestampUs 是采样/发送/读取时刻 | 首帧采样时刻，手机单调时钟低 32 位，相邻包差 5000 µs |
| D9 | seq 跳变、回绕、乱序的判定 | 按 §7.1 的 u32 距离 d 分五类处理（正常/缺口/重复/迟到/重置） |
| D10 | 抖动缓冲里 buffer 指当前深度还是目标值 | 指当前深度 D；target 每次评估重算；初始/最小 40 ms，最大 200 ms |
| D11 | 缓冲不足/超出上限的具体动作 | 欠载→补静音并重缓冲到 lastTarget；容量 1000 ms 溢出丢最旧；负向调整丢最旧 |
| D12 | 丢包率统计窗口未定义 | 最近 10 秒滑动窗口，missing/(received+missing) |
| D13 | START 与陈旧 AUDIO 的区分 | START 记录 T_start；丢弃早于 T_start−100 ms 的 AUDIO |
| D14 | OFFER 音频端口字段是否生效、名字长度 | 以 OFFER 字段为准（缺省 45679）；名字 ≤63 字节；6 秒老化 |
| D15 | 10 ms 包超过 MTU | 改为 5 ms / 960 字节；单包 IP 层 1004 字节，不分片；包长变更必须两端同步 |

## 12. 端到端联调自检表

> 前置：两端在同一子网；PC 防火墙放行 UDP 45678/45679；手机端前台服务已启动；PC 已构建可执行文件。
> 公共命令：
>
> ```powershell
> # PC 日志（JSONL，事件名见 01-architecture.md §11）
> $log = Get-ChildItem "$env:LOCALAPPDATA\BlueAudioSink\logs\sink-*.jsonl" |
>        Sort-Object LastWriteTime -Descending | Select-Object -First 1
> Get-Content $log.FullName -Tail 50
>
> # PC 端口占用（应看到 45678 与 45679，LocalAddress=0.0.0.0）
> Get-NetUDPEndpoint -LocalPort 45678,45679 | Format-Table LocalAddress,LocalPort,OwningProcess
>
> # PC 默认输出峰值（播放时 > 0）
> .\tools\SinkProbe\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\SinkProbe.exe meter 10
>
> # 手机日志（tag 固定为 BlueAudioSink）
> adb logcat -v time -s BlueAudioSink:V
> ```

| # | 场景 / 操作 | PC 端应观察到 | 手机端应观察到 |
| --- | --- | --- | --- |
| 1 | 构建并启动 PC，切到 Wi-Fi 模式 | 日志 `Wifi.ModeOn`、`Wifi.Bind`；45678/45679 已绑定；状态“待机（监听中）”；日志 `Wifi.OfferSent`（首次） | — |
| 2 | 手机打开 App，停在发现页 | — | 6 秒内看到 PC 名称（OFFER）；日志 `Wifi.Discover`；列表按 IP 去重 |
| 3 | 手机手动输入 PC IP（不依赖广播） | 收到 HELLO/START 后日志 `Wifi.Hello`/`Wifi.Start` | 能正常发起；不因无 OFFER 报错 |
| 4 | 手机点“开始”，授权 MediaProjection | 日志 `Wifi.Hello`（可选）、`Wifi.Start`、`Wifi.FirstAudio`、`Wifi.Playing`；状态 接收中→播放中；≤200 ms（冷启动，热启动更快） | 授权后开始采集；日志 `Capture.Start`、`Wifi.StartSent`；无异常 |
| 5 | 手机播放音乐 | 状态“播放中”；`Wifi.Stats` 中 `bufferMs` 40~200、`lossPct` < 0.5、`underruns` 0；`SinkProbe meter` 峰值 > 0 | 日志 `Wifi.Stats` 的 `sent` 约每秒 +200；`dropped` 为 0 |
| 6 | 观察 10 秒 PING/PONG | 日志 `Pong.Recv` 约每 2 秒一次；`rttMs` 同网段通常 1~20；`Wifi.Stats` 的 `offsetMs` 稳定 | 日志 `Ping.Recv`/`Pong.Sent` 成对；界面显示“PC 在线/已连接” |
| 7 | 手机按“停止” | 日志 `Wifi.Stop`、`Wifi.DrainDone`；≤500 ms 回到“待机（监听中）”；播放器释放；不再接受 AUDIO | 日志 `Capture.Stop`、`Wifi.StopSent`；界面回待机；socket 仍应答 PING |
| 8 | 播放中 `adb shell am force-stop <包名>` | 5~6 秒内 `Wifi.Timeout`；回“待机”；`Player.Released` | 进程被杀，无日志；PC 不应崩溃 |
| 9 | 播放中临时关闭手机 Wi-Fi 2 秒再开 | 丢包/断流计数上升；若超 5 秒则 `Wifi.Timeout`；恢复后下一次 START 正常 | 发送失败/重建 socket；日志 `Wifi.Fault` 或 `Capture.Fault`；需用户重试 |
| 10 | 无手机，按 01-architecture.md 附录 A 注入 START + 带缺口 AUDIO | `Wifi.Start`、`Audio.Gap`；D 临时增加后回落；无崩溃；日志 `Wifi.Stats` 的 `missing` 增加 | —（PC 单端测试） |
| 11 | 注入 magic 错误/长度错误的报文 | `Packet.Malformed` 计数 +1；播放不受影响；不崩溃 | — |
| 12 | 发送中快速 STOP→START（<1 秒） | 旧队列立即丢弃；新 `Wifi.Start` 清统计；不出现两路混音；无旧的迟到包 | 新会话 seq 从 0 开始；日志两段 START |
| 13 | 蓝牙模式播放中切到 Wi-Fi | 先绑定 Wi-Fi（失败则保持蓝牙），再释放蓝牙；日志 `Wifi.ModeOn`；无双重出声；蓝牙不回归 | 发现列表出现 PC；可开始新会话 |
| 14 | Wi-Fi 播放中切到蓝牙 | Wi-Fi 音频先停、socket/OFFER/PING 停止；蓝牙按原流程连接；日志 `Wifi.ModeOff`；无双重出声 | 停止发送；PC 不再回 PONG（6 秒后告警） |
| 15 | 播放中拔掉默认输出设备 | 播放器异常被捕获；日志 `Player.Fault`；回“待机”；下一次 AUDIO/START 可重建播放器 | 短暂断音；PC 恢复后继续；无崩溃 |
| 16 | 防火墙阻断 45679 | 有 OFFER/START/PONG，但无 AUDIO；5 秒后 `Wifi.Timeout`；状态回待机 | 能发现 PC、能开始，PONG 仍正常；**手机无法感知 AUDIO 被挡**（v1 无音频 ACK），仍可能显示“PC 在线” |
| 17 | 30 分钟连续播放 | 丢包率 < 0.5%；≥100 ms 断流 0~1 次；PC 工作集稳定；`Wifi.Stats` 无异常增长 | 内存 ≤100 MB（`adb shell dumpsys meminfo <包名>`）；无后台被杀；前台服务通知常驻 |
| 18 | 两台手机先后 START | 后 START 抢占；旧端点 AUDIO 计 `foreign`；同一时刻只有一路声音 | 后启动者正常；先启动者需停止，不能同时混音 |
| 19 | （可选）Wireshark 过滤 `udp.port==45679` | 每个 AUDIO 报文字节数 **976**，IP 层无分片（More fragments=0），包间隔约 **5 ms** | 日志 `Wifi.Stats` 的 `sent` 速率约 **200/s** |

**判读口径：**

- 有 `Wifi.Bind` 但手机看不到 OFFER → 先查 PC 防火墙/AP 隔离，再查是否绑定到错误网卡。
- 有 `Wifi.Start` 但无 `Wifi.FirstAudio` → 手机没有真正发包，查 `Capture.Start` 与发送线程。
- 有 AUDIO 但状态停在“接收中” → D 未达到 40 ms 或播放器创建失败，查 `Player.Created`/`Wifi.Fault`。
- 有声音但 `rttMs` 为 `--` → PING/PONG 未闭环，查手机是否回 PONG、源端口是否 45678。
- `lossPct` 持续 > 0.5% → 查 2.4G 干扰、AP 省电、手机发送队列；包长已按 §3 固定为 5 ms，任何一端都不得再改。
