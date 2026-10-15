# 常见问题与故障排查

## 凭据与加密

### Q: 把数据库从一台机器拷到另一台机器，所有账号都用不了了？

**A**: MailBot 用 Windows DPAPI（`CurrentUser` scope）加密 SMTP/IMAP 密码。DPAPI 密钥绑定当前 Windows 用户 + 当前机器：

- 跨机器拷贝 → 无法解密，会抛 `CryptographicException`。
- 同一机器换 Windows 用户登录 → 同样无法解密。
- 重装系统 → 无法解密。

**解决办法**：在目标机器上重新添加所有发件账号和验证邮箱（密码需要重新输入）。或者用源机器的 Windows 用户登录。

### Q: 启动时报 "无法解密凭据"，但密码我明明填对了？

**A**: 几乎一定是上面的情况。如果密码确实是新填的：

- 检查 `app_config` 表里的 `password_cipher` 字段是不是空的（说明保存时失败）。
- 用 DB Browser for SQLite 打开 `%AppData%\MailBot\mailbot.db`，查看 `senders` 表的 `password_cipher` 是否为 Base64 字符串。

### Q: 数据库存在哪里？能不能改路径？

**A**: 默认 `%AppData%\MailBot\mailbot.db`。覆盖方式（按优先级）：

1. 命令行 `MailBot.App.exe --db D:\my-data\mailbot.db`
2. 环境变量 `MAILBOT_DB_PATH=D:\my-data\mailbot.db`
3. exe 同目录的 `mailbot.config.json`：

```json
{ "DatabasePath": "D:\\my-data\\mailbot.db" }
```

支持 `%AppData%` 等环境变量展开。

---

## SMTP 认证

### Q: 添加发件账号后立刻进 "冷却中"，错误日志是 `535 Authentication failed`

**A**: 95% 的情况是**填了登录密码而不是授权码**。QQ 邮箱、163 邮箱、Gmail 都不接受登录密码走 SMTP，必须用授权码/应用密码。

| 服务商 | 怎么获取授权码 |
|---|---|
| QQ 邮箱 | 设置 → 账户 → POP3/IMAP/SMTP/Exchange/CardDAV/CardDAV服务 → 开启 → 生成授权码 |
| 163 邮箱 | 设置 → POP3/SMTP/IMAP → 开启 → 设置授权码 |
| Gmail | https://myaccount.google.com/apppasswords （需先开启两步验证） |

### Q: `SMTP 530 Must authenticate` / `SMTP 530 5.5.1 Authentication required`

**A**: 同上，认证失败。检查用户名/授权码拼写。

### Q: `SMTP connection timeout`

**A**: SMTP 主机名/端口错误或被防火墙挡住。

- 主机名是否带 `smtp.` 前缀？
- 端口是不是 465（隐式 SSL）或 587（STARTTLS）？
- 公司网络是否屏蔽了出站 465/587？尝试手机热点。

### Q: 提示 `The remote certificate is invalid`

**A**: 自建邮箱 / 证书过期 → 在账号编辑对话框里勾 **信任无效证书**。

公网邮箱（Gmail / QQ / 163 / Outlook）不应出现这个错误。如果出现了，检查：

- 是否被中间人攻击（公司网络劫持了 TLS）。
- 系统时间是否正确（证书链校验会看时间）。

### Q: 端口应该填 465 还是 587？

**A**: 两者都支持 TLS，但模式不同：

- **465** = 隐式 SSL，连接即 TLS（更老的服务商默认）
- **587** = STARTTLS，连接明文后用 `STARTTLS` 命令升级（现代推荐）

MailBot 按端口自动选 TLS 模式（465→SslOnConnect，587→StartTls），不需要额外勾选 SSL。

---

## IMAP / 送达验证

### Q: 没有配置验证邮箱，状态直接变成"已送达"了，对吗？

**A**: 对，这是有意设计。没配验证邮箱时系统走**无验证模式**：SMTP 返回 250 OK 就直接算送达，不走 IMAP 确认。队列正常推进到下一批，不会卡在等待验证上。

代价是失去了真实投递确认 —— 对方服务器拒收或 SMTP 谎报成功，你察觉不到。想要真实确认就去 [账号页](accounts.md) 配一个验证邮箱。

### Q: 验证邮箱已添加，但 "已送达" 永远是 0，"未送达" 持续增长

**A**: 验证邮箱本身有问题。检查：

1. **授权码是否正确**（QQ/163 邮箱）。
2. **IMAP 服务是否开启**（QQ 邮箱需要在网页设置里手动开）。
3. **日志里看 IMAP 连接错误**：监控页 → 日志区域，找 `IMAP` 关键字。
4. **临时改 `IMAP 轮询间隔` 为 60 秒**（见 [schedule.md](schedule.md)），观察是否变化。
5. **临时取消勾 "启用"** 另一个验证邮箱，看是不是某个特定邮箱域名的问题。

### Q: "未送达" 一直涨，但收件人其实收到了邮件

**A**: 验证邮箱被识别为"未送达"通常意味着：

- **验证邮箱域名 ≠ 发件账号域名**：很多邮件服务商对"自己域名发给自己"的邮件有特殊规则，可能不进收件箱（直接归档到垃圾或合并到"已发送"）。建议用一个**不同域名**的验证邮箱（如发件用 QQ，验证用 163）。
- **验证邮箱密码/IMAP 服务有问题**：见上一条。
- **IMAP 时间窗太短**：截图里默认 1 小时；如果是首次跑可临时调到 24 小时试试（`LookbackHours = 24`，改 `app_config` 表或代码常量）。

### Q: 同一个收件人状态在 "待发送" → "未送达" → "待发送" 反复

**A**: 这是正常行为。未送达回退后等下次主循环重试；只要 `max_send_attempts`（默认 3）还没用完，就会一直重试。

---

## 界面

### Q: 关掉主窗口后找不到 MailBot 了

**A**: 默认行为是**最小化到托盘**而不是退出。在系统托盘（屏幕右下角）找紫色信封图标：

- 双击 → 显示主窗口
- 右键 → 显示主窗口 / 退出

如果托盘图标也没了，可能是：

- Windows 资源管理器没在运行（重启它）。
- 通知区域被折叠，点任务栏向上的小箭头展开。
- MailBot 进程已被杀（任务管理器确认后重启）。

### Q: 启动第二份 MailBot 没反应

**A**: 正常 —— 第二实例会把"显示主窗口"指令通过命名管道发给第一实例，然后自己退出。

### Q: 富文本编辑器输入中文乱码

**A**: WPF `WebBrowser` 控件在某些 Windows 版本下对中文 IME 支持有问题。临时绕过：

- 把 HTML 直接粘到 `app_config.mail_body`（用 SQLite 工具）。
- 或在另一个编辑器（如 VS Code）写好 HTML 后整段粘贴进富文本编辑器。

### Q: 主题保存时弹 "主题不能包含 \\r\\n"

**A**: 这是 SMTP 头注入防护。检查主题里有没有手动换行或粘贴了多行文本。**主题必须是单行**。

---

## 数据

### Q: 收件人导入时 "跳过 N 条"

**A**: 通常意味着那 N 行的邮箱格式不合法。检查：

- 是否含空格？
- 是否多个邮箱挤在一行（用换行/逗号/分号分隔）？
- 是否缺 `@`？
- 是否含中文括号 `（）` 而非英文 `()`？

用 DB Browser for SQLite 打开 `recipients` 表看 `error` 字段，里面有具体原因。

### Q: 误删了账号或收件人，能恢复吗？

**A**:

- **收件人**：删除前若导出了 CSV，可重新导入。
- **发件账号**：删除后**密码无法恢复**（DPAPI 不可逆），`quota_refunds` 里的审计记录保留（不存密码）。
- **日志文件**：`<db 父目录>/logs/mailbot-yyyy-MM-dd.log` 删除后无法恢复。

### Q: 想清空所有数据重新开始

**A**:

1. 关闭 MailBot。
2. 删 `%AppData%\MailBot\mailbot.db`（或你自定义路径下的 db）。
3. 重启 MailBot，自动重建空库。

> ⚠️ 同时会清空发件账号、收件人、配置、发送日志 —— **不可恢复**。建议先导出收件人 CSV 备份。

---

## 性能

### Q: 几千个收件人，引擎一直显示 "等待批次" 不动

**A**: 检查：

1. **窗口**：调度页 → 发送窗口。如果现在不在窗口内，引擎会一直睡到 `WindowStart`。
2. **发件账号**：账号页 → 发件账号。如果全是"冷却中"或"已封禁"或配额用尽，引擎也走不动。监控页会显示"等待账号"。
4. **收件人状态**：如果导入时全部是"已送达"或"已屏蔽"，引擎会判定 Completed。

### Q: 验证 Worker 一直空闲，状态推进很慢

**A**:

- 检查 `IMAP 轮询间隔` —— 默认 180 秒，每 3 分钟才醒一次。
- 检查 `轮询次数` —— 默认 2 次就判定未确认，比较激进。
- 临时调小 `IMAP 轮询间隔` 到 60 秒、`轮询次数` 到 4 次观察。

---

## 开发

### Q: 我改了代码，怎么编译运行？

```bash
cd src
dotnet build MailBot.sln
dotnet run --project MailBot.App/MailBot.App.csproj
```

### Q: 测试怎么跑？

```bash
cd src
dotnet test MailBot.Tests/MailBot.Tests.csproj
```

### Q: 应用图标怎么重新生成？

```bash
cd scripts/IconGen
dotnet run
# 或指定输出路径
dotnet run -- "D:\path\to\app.ico"
```

### Q: 我改了 UI，截图怎么办？

参见 [development.md](development.md) "截图替换" 一节。

---

## 仍然没解决？

1. 看 `<db 父目录>/logs/mailbot-yyyy-MM-dd.log` 完整日志。
2. 用 DB Browser for SQLite 打开 `mailbot.db`，检查 `quota_refunds` / `recipients.error` 字段。
3. 启动调试版（`dotnet run`），观察控制台输出。
4. 在 [README.md](../README.md) 找开发相关章节或直接看代码（注释密度高）。