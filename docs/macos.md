# macOS 实现与接手说明

本仓库新增与 Windows 1.4.1 对应的 macOS 源码实现，位于 `src/CodexHud.Mac`，使用 .NET 10 与 Avalonia 11.3.12。macOS 项目已在 Windows 主机完成 Release 编译兼容检查；**尚未在 Mac 构建发布包、运行、测试、签名或执行验收**。下列命令和检查项交由接手者在 Mac 上执行。现有 Windows 截图、回归测试和持续运行回执不能证明 macOS 版可用。

目标为 Apple Silicon（`osx-arm64`）与 Intel（`osx-x64`），分别生成独立应用包；没有生成通用二进制。应用包最低系统版本设为 macOS 14.0，依据 [.NET 10 官方支持系统列表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)，该列表当前列出 macOS 14、15、26。这里是实现目标，不是本项目已验证的兼容性承诺。

## 实现范围

| 能力 | macOS 实现与边界 |
| --- | --- |
| 悬浮窗口 | 收起时宽约 582 DIP，高度按内容适配，额度、任务与设置展开面板；置顶、锁定、缩放、不透明度；菜单栏图标可恢复隐藏窗口 |
| Codex 额度 | 复用共享层的专属 app-server，只读 `account/rateLimits/read`；支持额度池与实际返回的周期选择、剩余百分比与过期状态 |
| 本地任务 | 复用本地数据库和日志读取、当前或最近一轮时长、本轮及任务自身累计 Token；缺失和过期证据保留有效性标记 |
| 剩余 Token 估算 | 复用账户与周期隔离、独立用量账本、校准分段及经验范围；不是固定套餐 Token 上限 |
| 任务实时状态 | 尚未核实 macOS Codex 桌面实时端点，保留本地记录推断；不能据此确认实时审批或等待输入 |
| CPU / 内存 / 磁盘 | 接入 macOS 专用采集器；整机与 Codex 进程组按可读数据展示，具体口径见界面说明及下文验收项 |
| GPU / 显存 | 暂不可用，显示缺失状态；不将 Apple Silicon 统一内存转换为专用显存 |
| 登录自启 | 默认关闭；设置中明确开启后写入当前用户 LaunchAgent，下一次登录生效；启用前应先把应用放到固定路径 |

完成/中断提示在主窗口保留约 60 秒；独立桌面提醒可在设置中开启，约 6 秒自动关闭。桌面提醒与声音默认关闭，不补报启动前的历史完成。此处的提醒是 HUD 自己的窗口，尚未接入 macOS 通知中心。

主额度与 Token 定义沿用 [README 中的数据含义](../README.md#数据含义)。Windows 的 PDH、私有工作集、命名管道实时订阅等平台口径不适用于 Mac。macOS 当前也不提供可靠的逐任务 PID 对应关系，不能将进程组资源摊到单个任务。

macOS 系统内存按物理总量减空闲页计算，包含文件缓存，不等同于活动监视器的“内存使用量”或内存压力。Codex 内存为进程组 RSS 合计，共享页可能重复；磁盘按 IOKit 中可确认物理连接的设备计数差分，覆盖不完整时标记部分数据。

## 构建与打包

在 Mac 安装 .NET SDK。仓库 `global.json` 当前选择 **10.0.400，`rollForward=latestPatch`**，因此需要与其匹配的 10.0.4xx SDK；其他 .NET 10 功能带不会自动替代。安装方式见 [.NET macOS 官方安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/macos)。已有独立 SDK 时可设置 `DOTNET_BIN` 为 `dotnet` 可执行文件的绝对路径。Windows 专用的 `install-sdk.mjs` 和 `build.ps1` 不用于本流程。

在仓库根目录执行，默认按当前终端进程的架构选择 RID；Apple Silicon 上使用原生终端，或明确指定 `osx-arm64`：

```bash
bash scripts/build-macos.sh osx-arm64
bash scripts/package-macos.sh osx-arm64
```

Intel 版本将两条命令的参数改为 `osx-x64`。构建脚本只发布 `CodexHud.Mac.csproj`，不运行测试，不构建 WPF 项目。它下载所选 RID 的 .NET 运行时和 Avalonia 依赖，生成自包含的多文件应用，最终使用者不需要另装 .NET。请完整保留 `.app`，不要只复制主可执行文件。

输出位置：

```text
artifacts/release/macos/osx-arm64/CodexHud.app
artifacts/CodexHud-1.4.1-osx-arm64.zip
artifacts/CodexHud-1.4.1-osx-arm64.zip.sha256
```

同 RID 重新构建时，旧应用会保留到同目录下的 `previous.*` 文件夹。打包使用 macOS `ditto` 保留执行权限和符号链接，仅收录应用与明确列出的说明文件；不收集源码、设置、任务日志或本机验收资料。打包完成和校验和仅证明生成了文件，不代表功能验收通过。

## 配置与启动

构建后可从 Finder 打开 `.app`，也可在终端启动以保留错误输出：

```bash
open artifacts/release/macos/osx-arm64/CodexHud.app
# 调查启动错误时，先从菜单栏退出已有实例，再执行：
artifacts/release/macos/osx-arm64/CodexHud.app/Contents/MacOS/CodexHud.Mac
```

`Info.plist` 使用 `LSUIElement=true`，定位为菜单栏辅助应用。关闭窗口会隐藏它，使用菜单栏的“显示”恢复，使用“退出”结束采集。Finder、菜单栏、多显示器、全屏和睡眠恢复行为都需要在真实 Mac 上确认。

设置与匿名估算历史保存在：

```text
~/Library/Application Support/CodexHud/settings.json
~/Library/Application Support/CodexHud/token-estimates-*.json
```

数据目录优先使用 `CODEX_HOME`，其次为已保存的 `CodexHome`，最后为 `~/.codex`。Finder 或登录启动通常不会继承交互式 shell 的环境变量，首次使用自定义目录时应在设置中保存路径。CLI 可在设置中显式填写 `CodexExecutable` 的绝对路径；自动定位先检查 `/Applications/Codex.app/Contents/Resources/codex` 和 `~/Applications/Codex.app/Contents/Resources/codex`，再检查 PATH、Homebrew、npm 全局、Volta 和 NVM 的常见目录；发现不到时应保存真实 CLI 路径。目录与 CLI 修改下次启动生效。

应先在 Codex 中完成正常登录；HUD 不提供 API key 输入，不导出登录凭据。额度进程仅查询账户与额度，不启动任务或代办审批。进程级环境变化、Codex 安装路径和本地日志格式都属于 Mac 接手验收内容。

登录自启仅为当前用户创建 `~/Library/LaunchAgents/local.codexhud.macos.plist`，不需要管理员权限；不调用 `launchctl` 立即启动副本。先将应用放到固定位置（例如 `~/Applications/CodexHud.app`），从该位置启动并开启“登录自启”。应用被移动后需要在新位置重新配置；关闭自启会撤销本应用的登记。开发时的 `dotnet run` 不应登记为自启目标。

## 可选签名与分发

默认构建不做发布签名或公证。接手者准备好自己的证书后，可显式设置身份再构建：

```bash
CODEX_HUD_SIGN_IDENTITY='Developer ID Application: Your Name (TEAMID)' \
  bash scripts/build-macos.sh osx-arm64
bash scripts/package-macos.sh osx-arm64
```

脚本对发布目录中的 Mach-O 文件及最终应用包签名，并按 [.NET macOS 发布说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/macos)为应用入口添加 `com.apple.security.cs.allow-jit` 权限。身份设为 `-` 时使用本地临时签名且不请求时间戳；这不提供对外分发信任。签名路径也没有在本次执行或验证，接手者应核查 .NET/Avalonia 本机依赖的加载以及实际签名要求。脚本不会上传应用或发起公证。

需要公开分发时，由发布者另行完成 Developer ID 签名、公证与签名验证，遵循 [Apple 的 macOS 软件公证流程](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)。公证及票据附加完成后应重新打包，再检查最终 ZIP。不要把本地未验收构建标成已签名或可公开分发的发行版。

## 接手验收清单

以下清单均为**待执行**，本次没有执行结果。建议为每种发布 RID 记录 macOS、CPU 架构、SDK、Codex 版本、源码提交、构建日志、包 SHA-256 与实际结果，并将资料保留在 `artifacts/validation/macos/`。

1. **静态与构建**：执行 `bash -n scripts/build-macos.sh scripts/package-macos.sh`，再按上文构建目标 RID。确认没有把 `net10.0-windows` 或 Windows 原生库带进 Mac 依赖链。已有 Windows 测试命令不视为 Mac 测试入口。
2. **应用包**：用 `plutil -lint` 检查 `Contents/Info.plist`，核对最低系统、版本与可执行文件；使用 Finder 和终端分别打开，确认菜单栏显示/隐藏/退出、重复打开、退出后采集进程结束。
3. **真实额度**：使用已登录 Codex 读取实际额度；检查多额度池、周期切换、过期与 CLI 不可用。Finder 启动及登录启动都要验证 CLI 与 `CODEX_HOME` 的解析，不只检查终端启动。
4. **任务与 Token**：对照一个真实本地任务的运行、结束、再次运行，以及具有输出 Token 的日志；核实轮次时长与累计快照。长时间无证据应显示待确认/过期。未接入已验证的桌面端点前，不接受实时审批/输入等待确认这一验收项。
5. **估算恢复**：先检查“校准中”，再积累真实有效段；重启、周期重置及采样中断不能跨断档配对。保存历史文件不得包含任务正文或账户标识原值。
6. **硬件**：对照活动监视器与系统工具检查 CPU、物理内存、磁盘增量，记录双方统计口径；核实 Codex 子进程和 HUD 自身排除。首轮、进程退出或权限受限应保留缺失/部分状态，GPU/显存不得显示虚构的零。
7. **桌面与设置**：检查 Retina、缩放、外接屏、显示器移除、全屏、睡眠恢复、锁定、置顶与不透明度；重启后设置仍存在。核实完成提醒、通知选项及本地历史不会被重复提示。
8. **自启与分发**：安装到固定目录后主动开启并重新登录检查，再关闭并检查取消；解压 ZIP 后复测执行权限与启动。若做发布签名，另行检查签名、公证及目标机器启动。

可供接手者使用的检查命令示例：

```bash
plutil -lint artifacts/release/macos/osx-arm64/CodexHud.app/Contents/Info.plist
codesign --verify --deep --strict --verbose=2 artifacts/release/macos/osx-arm64/CodexHud.app
spctl --assess --type execute --verbose=4 artifacts/release/macos/osx-arm64/CodexHud.app
(cd artifacts && shasum -a 256 -c CodexHud-1.4.1-osx-arm64.zip.sha256)
```

签名/系统评估命令只适用于对应的签名分发验收；未签名构建被拒绝不等于应用功能结论。Windows 的 `--ui-check`、`--diagnostics`、`--soak-seconds` 与 `--exit` 诊断入口没有在 Mac 版提供等价实现；Mac 端需要单独记录真实测试证据。
