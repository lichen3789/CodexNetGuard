# CodexNetGuard

一键修复「**梯子 → cc-switch → Codex**」三层链路：任意梯子客户端通用（v2rayN / Clash / mihomo / sing-box / Nekoray / Shadowsocks / Hiddify / TUN 全局模式…），免安装单文件，自动探测 → 自动修复 → 自动复检，改前一律备份、随时一键还原。

> One-click fixer for the "proxy client → cc-switch → Codex" chain on Windows. Any proxy client works: every loopback port is probed for real (HTTP CONNECT + SOCKS5), and the one that actually carries traffic is what gets used. Portable single exe, no install, no dependencies.

Windows 10 / 11，自带 .NET Framework 4.8 即可，**不需要安装任何运行库**。

## 下载

- 便携版：见 [Releases](../../releases) 里的 `CodexNetGuard-v1.0.0-portable.zip`，解压后双击 `CodexNetGuard.exe`。
- 不想用二进制？源码就在本仓库，编译方式见文末，用的是 Windows 自带的 `csc`，不需要 SDK。

首次运行 Windows 可能弹出「Windows 已保护你的电脑」蓝色提示框，因为 exe 没有代码签名：点「更多信息」→「仍要运行」即可。想避免这一步只能用买的代码签名证书签名。

## 快速开始

1. 解压 `CodexNetGuard-v1.0.0-portable.zip`；
2. 双击 `CodexNetGuard.exe`；
3. 它会自动检测 → 自动修复 → 显示结果（绿色 = 链路正常），关掉窗口就行。整个过程约 6～20 秒，取决于节点当时的响应速度。

放到别的电脑：**只拷 `CodexNetGuard.exe` 这一个文件**就能用。什么梯子都没装、也完全不知道端口在哪时，它也能靠实探找到可用端口。

## 它到底修什么

### 1. 梯子层（不绑定任何客户端）

- 枚举本机监听端口并读出属主进程，候选优先级：已知代理进程 > 客户端配置里写明的端口 > 其它本机监听端口（这一类必须实探通过才采纳）。
- 每个候选都真的连一次 `generate_204`：先按 HTTP 代理试，再按 SOCKS5 试；只有通了才写进系统代理。
- 写系统代理时按实测协议落格式：HTTP 用 `127.0.0.1:端口`，SOCKS5 用 `socks=127.0.0.1:端口`。
- 梯子没起来会按常见客户端（v2rayN / Clash Verge / Clash for Windows / mihomo / sing-box / Nekoray / Shadowsocks / Hiddify）自动拉起；也可以点「选择梯子…」手工指定任意客户端主程序（会记住）。
- 客户端在跑但端口实探不通（内核卡死）时，会自动重启一次客户端再试。
- 一个端口都不通时检测 TUN 全局隧道：确实在全局模式就关掉系统代理（避免双重代理），并在报告里说明。
- 报告里会写清死端口是谁占用的（进程名 + PID）。

### 2. cc-switch 层（只关不启）

- `%USERPROFILE%\.cc-switch\settings.json` 的 `enableLocalProxy` 是 true 但 15721 没人监听时，强制改回 false（改前备份、改后重启 cc-switch 生效），避免 Codex 指向死端口。
- Codex 的 `config.toml` 里 `base_url` 还残留 `127.0.0.1:15721` 时，从 cc-switch 自己生成的 `config.toml.bak-*` 里取回正常地址（只改这一行，改前备份）。
- **绝不主动打开** cc-switch 的本地代理。

### 3. Codex 层

- 解析 `%USERPROFILE%\.codex\config.toml` 的 `model_provider` 与对应 `base_url`，对真实地址做一次轻量探测（`/v1/models`，200/401/403/404 都算链路可达），作为「Codex 真的能用」的最终验收项。
- 报告只显示主机名，**日志 / 报告 / 备份里不出现任何 token**。

### 系统层

- 系统代理指向实探可用的端口，并把 `ProxyOverride` 去重规范化。
- `AutoConfigURL`（PAC）存在就备份后清空 —— 它会完全覆盖系统代理，是「改了没用」的头号原因。
- 用户级 `NO_PROXY` 规范化为 `localhost,127.0.0.1,::1,api.deepseek.com`；`HTTPS_PROXY` / `HTTP_PROXY` / `ALL_PROXY` 指向死端口时改写到当前可用端口，实在没有可用代理才清空。
- **直连 DeepSeek 被挡时自动改走代理**：先直连探一次，若直连不通、经代理能通，就自动把 `api.deepseek.com` 从 `NO_PROXY` 里去掉，并让 Codex 的链路探测也走代理。
- 每次修复前写快照，修复后复检，没达到目标就自动回滚。

## 界面

| 控件 | 作用 |
| --- | --- |
| 一键修复 | 重新检测 + 按三层修复（打开软件时已自动跑一次） |
| 仅检测 | 只看不改，用来看当前状态 |
| 选择梯子… | 手工指定任意梯子客户端的主程序（会记住） |
| 复制报告 | 把体检报告复制到剪贴板（贴给排错最方便） |
| 打开日志 | 打开 `CodexNetGuard-app.log` |
| 还原上次修改 | 回到上一次修复前的系统代理与环境变量；文件类改动可用同目录的 `.bak-CodexNetGuard-*` 备份还原 |
| 移除后台守护 | 结束托盘常驻进程、清理旧版守护（计划任务 + 开机脚本），不影响已修好的配置 |
| 登录时自动修复一次 | 每次登录跑一次修复就退出（一次性，不常驻） |
| 常驻托盘自愈 | 可选：托盘常驻，每 60 秒轻量巡检，异常才自动修、状态变化才提醒（默认关闭） |
| 修复后打开 Codex | 修复成功后顺手把 Codex 桌面版拉起来（默认关闭） |

## 命令行

```bat
CodexNetGuard-cli.exe --check            :: 只检测，有失败项返回退出码 1
CodexNetGuard-cli.exe --auto             :: 检测 + 自动修复，全绿返回 0
CodexNetGuard-cli.exe --auto --quiet     :: 静默修复（用于批处理/开机脚本）
CodexNetGuard-cli.exe --restore          :: 还原到上次修复前的状态
CodexNetGuard-cli.exe --remove-legacy    :: 清理旧版常驻守护 / 结束托盘常驻
CodexNetGuard.exe --tray                 :: 以托盘常驻方式运行
```

`CodexNetGuard.exe` 是图形界面版（也接受同样参数，只是不往控制台打印）；`CodexNetGuard-cli.exe` 是控制台版，输出完整报告并返回退出码，适合写进脚本。两个文件是同一个程序，只是子系统不同。

## 会改哪些文件（都会先备份）

| 文件 | 改动 | 备份 |
| --- | --- | --- |
| 系统代理（注册表 HKCU） | ProxyServer / ProxyEnable / ProxyOverride / AutoConfigURL | `CodexNetGuard-restore.json` |
| 用户级环境变量 | NO_PROXY / HTTPS_PROXY / HTTP_PROXY / ALL_PROXY | 同上 |
| `<梯子目录>\guiConfigs\guiNConfig.json` | 仅把 `SysProxyType` 改为 2（不改变系统代理） | `同名.bak-CodexNetGuard-<时间戳>` |
| Clash Verge `verge.yaml` / CFW `cfw-settings.yaml` | 仅关掉它自带的系统代理开关 | 同上 |
| `~/.cc-switch/settings.json` | 仅把 `enableLocalProxy` 改为 false | 同上 |
| `~/.codex/config.toml` | 仅当 base_url 指向 127.0.0.1:15721 时还原这一行 | 同上 |

改客户端配置时会**先退出该客户端 → 改文件 → 立刻拉回来**，因为客户端退出时会用内存里的旧配置覆盖文件。

## 生成的文件

默认写在 exe 同目录；若该目录不可写（例如放在 `C:\Program Files` 下），自动改用 `%LOCALAPPDATA%\CodexNetGuard\`。

| 文件 | 说明 |
| --- | --- |
| `CodexNetGuard.ini` | 记住梯子路径、自启开关等 |
| `CodexNetGuard-app.log` | 运行日志（超 1MB 轮转） |
| `CodexNetGuard-report.txt` | 最近一次体检报告 |
| `CodexNetGuard-restore.json` | 上一次修复前的快照，供「还原上次修改」用 |

## 常见问题

- **会不会把我的设置改坏？** 任何改动前都会备份：系统层写进 `CodexNetGuard-restore.json`（界面「还原上次修改」/ `--restore` 一键回退），文件层保留 `*.bak-CodexNetGuard-*`；修复后还会复检，没达标自动回滚。
- **需要管理员权限吗？** 不需要。只有机器级环境变量这类需要管理员的项，它会只报告不改。
- **会上报我的数据吗？** 不会。全部在本地完成，没有遥测；它对外只做连通性探测（`generate_204`、各 AI 接口的 `/v1/models` 等）。
- **提示"节点可能失效"是什么意思？** 说明端口在监听但连不出去，通常是节点被封或订阅过期，工具修不了，请换节点或更新订阅后重跑。
- **装了 v2rayN 要注意什么？** 托盘菜单里的系统代理保持「不改变系统代理」就行，工具会自动把它设成这一项，避免两边互相覆盖。
- **报告里说我的是"全局隧道模式"？** 表示检测到 TUN 网卡，这时系统代理应当关闭，工具会保持关闭。

## 自己编译

用 Windows 自带的 csc（C# 5 语法），不需要 SDK 或任何第三方库：

```bat
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
%CSC% /nologo /target:winexe /out:CodexNetGuard.exe      /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll src\CodexNetGuard.cs
%CSC% /nologo /target:exe    /out:CodexNetGuard-cli.exe  /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll src\CodexNetGuard.cs
```

## 自测与打包

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-selftest.ps1                        :: 完整自测（16 项断言）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-selftest.ps1 -SkipLadderStop        :: 不动正在用的梯子
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-release.ps1                       :: 编译 + 自测门禁 + 打包
```

自测会真刀真枪注入故障再验证修复：系统代理指死端口、PAC 残留、`ProxyOverride` 脏数据、`NO_PROXY`/`HTTPS_PROXY` 脏值、cc-switch 开关开着但端口没监听、只读配置（应回滚）、多客户端优先级、SOCKS5-only、诱饵端口不被误选、完全无可用代理（应关闭系统代理且退出码 1）、连续修复幂等，开始前记录环境、结束后自动还原。不带 `-SkipLadderStop` 时会临时停一次梯子客户端。

## 许可

[MIT](LICENSE)
