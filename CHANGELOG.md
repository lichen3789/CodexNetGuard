# 更新记录

## v1.0.0（2026-09-29）

首个对外版本。核心能力：把「梯子 → cc-switch → Codex」三层链路修到真正可用。

- **不绑定任何梯子客户端**：枚举本机监听端口，逐个用 HTTP CONNECT 与 SOCKS5 真实探测，谁通就用谁；SOCKS5-only 会写成 `socks=127.0.0.1:端口`。
- **自动拉起 / 自动重启**：梯子没起来会自动启动；客户端在跑但端口实探不通（内核卡死）会自动重启一次再试。
- **TUN / 全局模式识别**：没有可用本地端口但检测到全局隧道时，关掉系统代理避免双重代理。
- **系统层**：清理会覆盖系统代理的 PAC、把 `ProxyOverride` 去重规范化、修正用户级 `NO_PROXY` / `HTTPS_PROXY` / `HTTP_PROXY` / `ALL_PROXY`。
- **直连被挡自动改走代理**：`api.deepseek.com` 直连不通但经代理可达时，自动改走代理并同步调整 `NO_PROXY`。
- **cc-switch 层（只关不启）**：本地代理开关开着但 15721 没监听时强制关闭；Codex `config.toml` 残留 `127.0.0.1:15721` 时按 cc-switch 自己的备份还原。
- **Codex 层**：解析 `config.toml` 的 provider / base_url 并实测可达性，作为最终验收项；报告只显示主机名，不输出 token。
- **安全网**：每次修复前写快照，复检不达标自动回滚；界面可「还原上次修改」，CLI 提供 `--restore`。
- **便携**：免安装、零第三方依赖（.NET Framework 4.8 自带即可），GUI 与 CLI 两个 exe；目录不可写时自动改用 `%LOCALAPPDATA%\CodexNetGuard\`。
- **可选常驻**：托盘自愈默认关闭，开启后每 60 秒轻量巡检，异常自动修、状态变化才提醒。
- **可验证**：`tests\run-selftest.ps1` 一键注入 13 类故障并验证修复（16 项断言），`tools\build-release.ps1` 编译 + 自测 + 打包。
