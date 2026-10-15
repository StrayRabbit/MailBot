# 开发 / 构建 / 测试

本节面向接手维护 MailBot 的开发者。

---

## 1. 环境要求

- **.NET 10 SDK**
- **Windows 10/11** 或 **Windows Server**（WPF + WinForms 依赖）
- 推荐 IDE：**Visual Studio 2022 17.12+** 或 **JetBrains Rider 2024.3+**
- 可选：**DB Browser for SQLite**（用于检查 `mailbot.db`）

---

## 2. 构建与运行

```bash
# 进入源代码目录
cd src

# 还原依赖
dotnet restore

# 构建整个解决方案
dotnet build MailBot.sln

# 仅构建启动项目
dotnet build MailBot.App/MailBot.App.csproj

# 运行 WPF 应用（启动项目）
dotnet run --project MailBot.App/MailBot.App.csproj

# 指定数据库路径
dotnet run --project MailBot.App/MailBot.App.csproj -- --db D:\dev\mailbot.db
```

构建产物在 `src/MailBot.App/bin/Debug/net10.0-windows/`。

> 💡 MailBot.App 是 WinExe 输出类型，可以直接双击 `MailBot.App.exe` 启动。

---

## 3. 测试

```bash
cd src
dotnet test MailBot.Tests/MailBot.Tests.csproj
```

xUnit，约 100+ 用例，覆盖：

- **App/**：`MonitorViewModel` 状态刷新
- **Logging/**：`LogBus` 单例 + 事件订阅
- **Mail/**：`MailTemplate` 校验（控制字符、NUL、空主题）
- **Recipients/**：`RecipientImporter` CSV/Excel 导入（有/无表头、编码、列识别、格式校验）
- **Scheduling/**：`TimeWindow`（跨天窗口）+ `QuotaReset`（跨日判定）
- **Senders/**：`SenderPool` 账号选择算法 + 状态机；`SendersViewModel` CRUD
- **Sending/**：`BatchAssembler` 批次切片；`MailBuilder` BCC/verifier；`SendEngine` 日志；`QuotaRefundService` 退额
- **Storage/**：所有 `*Repository` CRUD + 业务查询；`Database` schema + 迁移；`ResolveDbPath` 5 级优先级；`AttachmentStorage`；`CredentialProtector` DPAPI 往返
- **Verification/**：`VerificationWorker` 核心流程（冷启动/主循环/IMAP 命中/超时/退额/日志）；`ImapPoller` fake 验证；`BatchIdGenerator/Formatter`、`BatchTracker`、`VerifierConfig` 全部覆盖

### 测试临时数据库

测试运行时会创建临时 SQLite 文件（位于 `%TEMP%` 或当前目录），开启外键约束但**不开启 WAL**（避免文件锁）。

---

## 4. 项目结构与模块职责

```
src/
├── MailBot.sln
├── NuGet.Config                  ← NuGet 源（只配 nuget.org）
├── MailBot.App/                  ← WPF UI 层（启动项目，net10.0-windows）
│   ├── App.xaml(.cs)             ← 应用入口、启动流程、单实例、托盘
│   ├── MainWindow.xaml(.cs)      ← 主窗口（顶部工具栏 + 侧栏 + 状态栏）
│   ├── AssemblyInfo.cs
│   ├── Controls/                 ← 自定义 WPF 控件
│   ├── Converters/               ← XAML 值转换器
│   ├── Logging/                  ← UI 日志桥接（LogBus）
│   ├── Views/
│   │   ├── Pages/                ← 5 个页面（Monitor/Accounts/Recipients/Compose/Schedule）
│   │   ├── SenderEditDialog.xaml(.cs)
│   │   └── VerifierEditDialog.xaml(.cs)
│   └── ViewModels/               ← MVVM（MainVM + 4 子 VM + RecipientRow）
├── MailBot.Core/                 ← 业务逻辑（net10.0，可独立引用）
│   ├── Logging/                  ← ILogger + LogLevel
│   ├── Mail/                     ← MailTemplate + MailBuilder
│   ├── Models/                   ← Sender / Recipient / VerifierAccount / QuotaRefund / AppConfig
│   ├── Recipients/               ← RecipientStatus + Importer + Exporter
│   ├── Scheduling/               ← TimeWindow + QuotaReset
│   ├── Security/                 ← CredentialProtector (DPAPI)
│   ├── Senders/                  ← SenderStatus + SenderPool
│   ├── Sending/                  ← SendEngine + BatchAssembler + SmtpDispatcher + QuotaRefundService + RefundReason
│   ├── Storage/                  ← Database + ConfigRepository + *Repository + AttachmentStorage + LogFileWriter + mailbot.config.json
│   └── Verification/             ← VerifierConfig + BatchTracker + BatchIdGenerator + BatchIdFormatter + IImapPoller + ImapPoller + VerificationWorker + BatchState
└── MailBot.Tests/                ← xUnit（net10.0）
```

### 模块依赖关系

```
MailBot.App ──→ MailBot.Core ──→ (MailKit, MimeKit, Microsoft.Data.Sqlite, CsvHelper, EPPlus, System.Security.Cryptography.ProtectedData)
MailBot.Tests ──→ MailBot.Core
MailBot.Tests ──→ MailBot.App (少量 ViewModel 测试)
```

`MailBot.Core` 是纯 .NET 10，**不依赖 WPF**，可以在控制台、测试、Server 端代码里复用。

---

## 5. 数据目录

```
%AppData%\MailBot\
├── mailbot.db              ← SQLite 主库
├── mailbot.db-wal          ← WAL 文件（生产路径）
├── mailbot.db-shm          ← 共享内存文件
├── logs/
│   └── mailbot-yyyy-MM-dd.log
└── attachments/
    └── <hash-prefix>-<filename>
```

数据库路径可被覆盖（见 [README.md](../README.md) "运维与配置"）。

---

## 6. SQLite Schema

核心表（详见 `src/MailBot.Core/Storage/Database.cs`）：

| 表 | 用途 |
|---|---|
| `app_config` | KV 强类型配置（窗口/批间隔/验证参数/邮件模板等） |
| `senders` | 发件账号（密码 DPAPI 加密、日配额、状态、冷却时间） |
| `recipients` | 收件人（邮箱、状态、batch_id、verified_at、retry_at、unconfirmed_count） |
| `verifiers` | 验证邮箱（IMAP 凭据 DPAPI 加密、LastPolledAt、LastSeenUid、Enabled） |
| `quota_refunds` | 配额退额审计（sender_id、batch_id、count、reason） |

> 历史表 `send_logs` 已移除：审计完全由物理日志（`<db 父目录>/logs/mailbot-yyyy-MM-dd.log`）承担。

### 自动迁移

首次启动如果检测到旧库（缺 `recipients.batch_id`、`recipients.verified_at`、`recipients.pending_retry_at`、`recipients.unconfirmed_count`、`senders.trust_invalid_certificate`）会自动 `ALTER TABLE` 补字段。

> ⚠️ 修改 schema 后请在 `Database.cs` 的 `EnsureSchema()` 里同步更新建表 + 迁移逻辑。

---

## 7. IconGen 工具

`scripts/IconGen/` 是独立的 .NET 控制台工具，用于生成 `app.ico` 应用图标。

```bash
cd scripts/IconGen
dotnet run                              # 输出到 AppContext.BaseDirectory/app.ico
dotnet run -- "D:\path\app.ico"         # 指定输出路径
```

实现细节：

- GDI+ 内存绘制 6 个尺寸（16/24/32/48/64/128）的 ARGB 位图：紫色圆角矩形 + 白色信封 + 紫色 V 形封口。
- 每个位图编码成 BMP-in-ICO 帧（不是 PNG）。
- 按 ICO 文件格式拼接：`reserved=0`、`type=1`、`16 * N` 字节目录 + N 个 frame。

> 注释里提到 "PNG frame 在 WPF 转 HICON 时部分尺寸会抛 E_HANDLE"，所以选 BMP-in-ICO。如改用 PNG 嵌入，需要先实测 WPF 的 `Icon.FromHandle` 兼容性。

---

## 8. 主要类与职责速查

| 类 | 文件 | 职责 |
|---|---|---|
| `App` | `MailBot.App/App.xaml.cs` | 应用入口、启动流程编排、单实例、托盘 |
| `MainWindow` | `MailBot.App/MainWindow.xaml.cs` | 主窗口、关闭时弹窗 |
| `MainViewModel` | `MailBot.App/ViewModels/MainViewModel.cs` | 持有子 VM、模板编辑、Start/Pause/Stop |
| `SendEngine` | `MailBot.Core/Sending/SendEngine.cs` | 主循环：窗口→选账号→取批→SMTP→间隔 |
| `BatchAssembler` | `MailBot.Core/Sending/BatchAssembler.cs` | `NextBatch` = `RecipientRepo.PickPending(batchSize)` |
| `SmtpDispatcher` | `MailBot.Core/Sending/SmtpDispatcher.cs` | 单次 SMTP 发送 + 错误分类 |
| `SenderPool` | `MailBot.Core/Senders/SenderPool.cs` | 选最佳账号 + 状态管理 |
| `VerificationWorker` | `MailBot.Core/Verification/VerificationWorker.cs` | 验证主循环 + 事件 |
| `ImapPoller` | `MailBot.Core/Verification/ImapPoller.cs` | IMAP 按时间窗扫描 |
| `BatchTracker` | `MailBot.Core/Verification/BatchTracker.cs` | 单批次状态机 |
| `MailBuilder` | `MailBot.Core/Mail/MailBuilder.cs` | To/Bcc/Verifier 装配 |
| `MailTemplate` | `MailBot.Core/Mail/MailTemplate.cs` | 主题/HTML/附件 + 校验 |
| `CredentialProtector` | `MailBot.Core/Security/CredentialProtector.cs` | DPAPI 加密/解密 |
| `Database` | `MailBot.Core/Storage/Database.cs` | SQLite 连接 + Schema + 迁移 + 路径解析 |
| `AttachmentStorage` | `MailBot.Core/Storage/AttachmentStorage.cs` | 附件复制到 `<db>/attachments/` |
| `LogFileWriter` | `MailBot.Core/Storage/LogFileWriter.cs` | 日志写文件 |
| `LogBus` | `MailBot.App/Logging/LogBus.cs` | UI 日志总线（VM ↔ View） |

---

## 9. 调试技巧

### 跑测试时打印详细 SQL

```csharp
// Database.cs EnsureSchema() 里临时加
Console.WriteLine(command.CommandText);
```

### 看 SMTP 原始日志

MailKit 内部日志开关：

```csharp
// SmtpDispatcher.cs SendAsync 临时加
client.ProtocolLogger = new ProtocolLogger(Console.OpenStandardOutput());
```

### 看 IMAP 原始日志

同上，在 `ImapPoller.cs`。

### 临时禁用 Schema 迁移

```csharp
// Database.cs EnsureSchema() 开头 return;
// 注意：禁用后老库会缺字段，UI 报错
```

### 模拟送达验证"已送达"

```csharp
// 临时在 VerificationWorker.cs LoopAsync 里加：
tracker.MarkVerified(batchId, "manual-test", DateTimeOffset.UtcNow);
```

### 单步跑一批不发

```csharp
// SendEngine.cs RunOnce() 注释掉 SmtpDispatcher.SendAsync 那段
```

---

## 10. 截图替换

文档截图都放在 `docs/images/`。改 UI 后请同步更新：

| 文件 | 来源 |
|---|---|
| `main-window.png` | 启动后首屏（监控页默认显示在主窗口） |
| `monitor.png` | 监控页 |
| `accounts.png` | 账号页 |
| `recipients.png` | 收件人页（任意状态有数据的状态更直观） |
| `compose.png` | 邮件页 |
| `schedule.png` | 调度页 |

> 建议 1440×900 以上分辨率抓取；Windows 用 `Win+Shift+S` 截窗口。

---

## 11. 编码约定

- **C# 风格**：现代 C#（records、pattern matching、`async/await`、`Span<T>`）。参考 `modern-csharp-coding-standards`。
- **MVVM**：用 `CommunityToolkit.Mvvm` 的 `[ObservableProperty]` / `[RelayCommand]` source generator，不要手写 INotifyPropertyChanged。
- **注释密度**：高（多数 public 类型都有 XML doc），便于后续人理解。改代码时保持同样密度。
- **命名**：`XxxRepository` / `XxxService` / `XxxWorker` / `XxxViewModel` 清晰分层。

---

## 下一步

- [README.md](../README.md) 项目总览
- [accounts.md](accounts.md) 发件账号/验证邮箱字段
- [recipients.md](recipients.md) 收件人状态机
- [schedule.md](schedule.md) 调度与验证参数