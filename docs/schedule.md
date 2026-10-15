# 调度与验证参数

**调度** 页分两个 GroupBox：**发送调度**（控制主循环节奏）和 **送达验证时间**（控制验证 Worker 节奏）。

![调度页](images/schedule.png)

点右下 **保存配置** 才会持久化到 `app_config` 表。

> 💡 大多数情况下默认值就够用。如果你不确定该调什么，就用默认值。

---

## 1. 发送调度

### 窗口（WindowStart / WindowEnd）

格式 `HH:MM`，支持跨天（如 `22:00 - 06:00`）。

主循环每次醒来都会先问"现在在不在窗口内"：

- **在窗口内**：取一批发送。
- **不在窗口内**：睡到 `TimeWindow.NextStart(now)`，最多睡到次日 `WindowStart`。

`WindowStart == WindowEnd` 视为"全天允许"。

> 例：`22:00 - 06:00` 表示每天 22:00 到次日 06:00 允许发送；06:00 到 22:00 期间主循环一直睡。

### 批间隔（SendIntervalSeconds）

单位秒。上一批 SMTP 收到 200 OK 后等多久再发下一批。

- **调大** → 节奏更慢，不易被反垃圾检测命中；适合小批量账号或严格的反垃圾环境。
- **调小** → 节奏更快；适合多账号 + 低日配额。

**默认 600 秒（10 分钟）**。截图里的实际值就是 600。

### 批大小（BatchSize）

每批 BCC 收件人数。

- 1 个收件人 → 放 To 字段。
- N 个收件人（N ≥ 2）→ 第 1 个 To，其余 BCC。

**默认 20**。如果你的日配额很高（如 500/天），可以把批大小调高（如 50），降低 SMTP 连接次数。

### 冷却阈值（CoolingFailures）

连续失败多少次进入 cooling。

**默认 3**。注意 "连续失败" 是按发件账号维度，不是按收件人 —— 同一账号连续 3 批 SMTP 失败 → 该账号进入冷却。

### 冷却时长（CoolingMinutes）

cooling 状态持续分钟数。

**默认 60**。到期自动恢复 "正常"。

### 最大重试（MaxRetries）

单封邮件发送失败后的重试次数。

**默认 1**。注意还有 `AppConfig.MaxSendAttempts`（默认 3）控制"未送达"收件人重新进入批次的最多次数，**当前 UI 未暴露**编辑入口，需改 `app_config` 表的 `max_send_attempts` 字段。

---

## 2. 送达验证时间

> 这一组控制 `VerificationWorker` 节奏，验证 Worker 是独立后台线程，每 `IMAP 轮询间隔` 醒一次。

### IMAP 轮询间隔（PollIntervalSeconds）

单位秒。验证 Worker 主循环醒的频率。

**默认 180 秒**。截图里改成了 60 秒（演示场景）。

- **调小** → 验证更快发现已送达 / 未送达，状态推进更及时。
- **调大** → 减少 IMAP 连接次数。

### 重查间隔（RecheckIntervalSeconds）

单位秒。批次发送后，等多久才进入"首查"。

**默认 180 秒**。截图里改成 120。

> ⚠️ 校验约束：`LookbackMinutes * 60 ≥ RecheckIntervalSeconds` 否则首查都没机会查就 Unconfirmed 了。

### 轮询次数（MaxPollAttempts）

达到此次数仍未命中 → 判定未确认（同时回退 pending + 账号冷却）。

**默认 2**。截图里改成 3。

> 注：tooltip 文字"达到此次数仍未命中则未确认" 是 UI 上的提示，实际语义是 `attempts=2` 表示"首查 + 重查两次机会"。

### 时间窗（LookbackHours）

每次 IMAP 查询过去此范围内的邮件。

**默认 1 小时**。MailBot 不再使用 UID 水位线（避免水位漂移漏算），改按时间窗扫描；重复扫描由 `BatchTracker` 状态机去重（已 Verified / Unconfirmed 的批次不再参与判定）。

### 未确认冷却（UnconfirmedCooldownMinutes）

单位分钟。批次被判定"未确认"后，对**发件账号**施加的冷却时长。同时该批次的收件人会被回退为 `pending`，冷却到期后随主循环重新发送。

**默认 60**。

> 翻倍机制：`attempts` 每 +1，冷却时长翻倍（如 attempts=1→60min, attempts=2→120min, attempts=3→240min...），最高封顶 `CooldownDoublingCapMinutes`（默认 1440 = 24h）。
>
> 公式：`ComputeCooldownMinutes(attempts) = min(UnconfirmedCooldownMinutes * 2^(attempts-1), CooldownDoublingCapMinutes)`

### 上一批验证等待（SendHoldOnUnverifiedSeconds）

单位秒。SendEngine 主循环在准备发下一批时，如果上一批还处于 pending 且距离 `SentAt` 已超过此秒数，会进入"等待验证"分支（`SendEngine.cs:172-185`）。

**默认 300（5 分钟）**。`WaitForBatchAsync` 最多等这么久，期间 `VerificationWorker` 把状态推进到 Verified / Unconfirmed 就提前放行；超时仍未推进则按当前状态继续选下一账号。

> 设计意图：避免在"上一批还没确认送达"时就连发下一批，让收件人先看到第一封邮件再收到第二封。

---

## 3. 校验规则汇总

保存时 `VerifierConfig.Validate()` 会检查：

| 规则 | 出错信息 |
|---|---|
| `MaxPollAttempts ≥ 1` | 否则批次永远无法被判定 Unconfirmed |
| `LookbackHours ≥ 0` | |
| `LookbackMinutes ≥ 1` | 否则批次永远无法被强制 Unconfirmed |
| `LookbackMinutes * 60 ≥ RecheckIntervalSeconds` | 否则首查都没机会查就 Unconfirmed |
| `UnconfirmedCooldownMinutes ≥ 0` | |
| `MaxVerifierAttemptsPerBatch ≥ 1` | 每个批次至少要有一次 verifier 试错机会 |
| `CooldownDoublingCapMinutes ≥ UnconfirmedCooldownMinutes` | 否则永远会立即封顶 |

校验失败 → 抛 `InvalidOperationException` → 弹窗显示具体字段。

> 📌 截图里 "时间窗(小时)" 字段对应代码中的 `LookbackHours`（不是 `LookbackMinutes`）。`LookbackMinutes` 是另一个字段（默认 5 分钟，作为兜底），**当前 UI 未暴露** —— 如需修改请改 `app_config` 表的 `verification_lookback_minutes` 字段。

---

## 4. 推荐调参起点

> 这些是经验值，不是硬性建议。

### 保守（不容易被反垃圾）

```
窗口: 08:00 - 22:00
批间隔: 600 秒
批大小: 20
冷却阈值: 3
冷却时长: 60
IMAP 轮询间隔: 180 秒
重查间隔: 180 秒
轮询次数: 3
时间窗: 1 小时
未确认冷却: 60
```

### 激进（日配额高 + 多账号）

```
窗口: 00:00 - 23:59
批间隔: 300 秒
批大小: 50
冷却阈值: 5
冷却时长: 30
IMAP 轮询间隔: 60 秒
重查间隔: 90 秒
轮询次数: 4
时间窗: 1 小时
未确认冷却: 30
```

### 调试/演示（截图里的值）

```
窗口: 00:00 - 23:59
批间隔: 600 秒
批大小: 20
冷却阈值: 3
冷却时长: 60
最大重试: 1
IMAP 轮询间隔: 60 秒
重查间隔: 120 秒
轮询次数: 3
时间窗: 1 小时
未确认冷却: 60 分钟
上一批验证等待: 300 秒
```

---

## 下一步

- 想了解监控页状态含义 → [monitoring.md](monitoring.md)
- 遇到配置保存报错 → [faq.md](faq.md)