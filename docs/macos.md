# macOS 实现与接手说明

本仓库的 macOS 源码实现已更新至 **1.7.3**，位于 `src/CodexHud.Mac`，使用 .NET 10 与 Avalonia 11.3.12，并复用当前共享层的额度、Token 用量、任务费用与周期历史逻辑。**本次修订仅更新源码与交接说明，没有还原依赖、编译、运行、测试、签名或执行验收。** 下列命令和检查项交由接手者在 Mac 上执行；旧版本在 Windows 上的编译记录、截图、回归测试和持续运行回执均不能证明本修订或 macOS 版可用。

目标为 Apple Silicon（`osx-arm64`）与 Intel（`osx-x64`），分别生成独立应用包；没有生成通用二进制。应用包最低系统版本设为 macOS 14.0，依据 [.NET 10 官方支持系统列表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)，该列表当前列出 macOS 14、15、26。这里是实现目标，不是本项目已验证的兼容性承诺。

## 实现范围

| 能力 | macOS 实现与边界 |
| --- | --- |
| 悬浮窗口 | 默认缩放下收起时宽约 654 DIP，高度按内容适配；底部常显总体用量，额度、任务、费用与设置提供展开面板；置顶、锁定、缩放、不透明度；菜单栏图标可恢复隐藏窗口 |
| Codex 额度 | 复用共享层的专属 app-server，只读 `account/rateLimits/read`；支持额度池与实际返回的周期选择、剩余百分比与过期状态 |
| 重置与估算恢复 | 只读展示可用重置卡张数，过期读数降显并标注，未取得读数显示缺失；额度展示服务端重置时间与剩余时间，估算恢复进度沿用共享层；不把时间到期视为额度已经刷新，不提供兑换操作 |
| 本地任务 | 复用本地数据库和日志读取、当前或最近一轮时长、本轮及任务自身累计 Token；缺失和过期证据保留有效性标记 |
| 剩余 Token 估算 | 复用账户与周期隔离、独立用量账本、校准分段及经验范围；不是固定套餐 Token 上限 |
| 用量趋势 | 独立于额度校准的本机响应账本；每秒刷新，展示最近 60 秒的 20 秒后向平均速率；总体显示总量曲线及总量/输入/缓存命中/输出数值；各任务显示输入/缓存命中/输出三条曲线，共用纵轴，总量见悬停提示 |
| 任务 API 等价费用 | 按本地响应的模型、服务层和输入/缓存/输出用量计算美元等价成本；区分任务自身与可确认归属的子代理，未知价格与不完整日志保留缺失或部分状态 |
| 周期与历史报告 | 复用共享周期跟踪器，展示当前与历史周期的费用、Token、任务树、模型与轮次明细；可搜索、排序及补记本地重置时间或修正周期时间；这些操作只调整 HUD 本地统计边界 |
| 任务实时状态 | 尚未核实 macOS Codex 桌面实时端点，保留本地记录推断；不能据此确认实时审批或等待输入 |
| CPU / 内存 / 磁盘 | 接入 macOS 专用采集器；整机与 Codex 进程组按可读数据展示，具体口径见界面说明及下文验收项 |
| GPU / 显存 | 暂不可用，显示缺失状态；不将 Apple Silicon 统一内存转换为专用显存 |
| 登录自启 | 默认关闭；设置中明确开启后写入当前用户 LaunchAgent，下一次登录生效；启用前应先把应用放到固定路径 |

完成/中断提示在主窗口保留约 60 秒；独立桌面提醒可在设置中开启，约 6 秒自动关闭。桌面提醒与声音默认关闭，不补报启动前的历史完成。此处的提醒是 HUD 自己的窗口，尚未接入 macOS 通知中心。

主额度与 Token 定义沿用 [README 中的数据含义](../README.md#数据含义)。Windows 的 PDH、私有工作集、命名管道实时订阅等平台口径不适用于 Mac。macOS 当前也不提供可靠的逐任务 PID 对应关系，不能将进程组资源摊到单个任务。

用量图记录的是本机收到响应统计的时间，不是模型实际生成 Token 的时刻。每个图点使用此前 20 个完整秒桶的到达量除以 20，保留 79 秒原始桶以绘制最近 60 秒的完整窗口。缓存命中是输入的子集，总量为输入加输出，不能再加一次缓存。启动、睡眠恢复、未读完的日志和不可读记录显示缺口或可观察下界，不能补成确定的零；平滑仅用于显示速率，不改写额度校准、Token 原始累计或费用计算。

任务与周期费用是仓库价格表计算的 **API 等价成本**，不是 Codex 订阅账单或实际扣款。价格匹配、响应去重和子代理归属沿用共享层；未知模型、价格未收录、缺失日志、回填未完成或周期边界未确认时，界面保留对应说明。周期报告以所选额度周期的时间范围归集本机全部普通任务，包含可确认归属的历史与归档任务；这些数值既不是该额度池的专属用量，也不等于账户在所有设备上的总用量。手动补记只保存本地时间边界，不调用账户 reset 接口；时间输入需包含时区，格式为 `yyyy-MM-dd HH:mm:ss zzz`（例如 `2026-09-10 12:30:00 +09:00`）。报告支持按费用、Token、用时或标题排序，按任务标题或 ID 搜索；任务展开状态、选择、搜索与排序在同一报告窗口中按周期保留，目前没有导出功能。

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
artifacts/CodexHud-1.7.3-osx-arm64.zip
artifacts/CodexHud-1.7.3-osx-arm64.zip.sha256
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

设置、估算历史以及本地费用和周期索引保存在：

```text
~/Library/Application Support/CodexHud/settings.json
~/Library/Application Support/CodexHud/token-estimates-*.json
~/Library/Application Support/CodexHud/session-costs-*.json
~/Library/Application Support/CodexHud/billing-cycles-*.json
```

文件名中的目录哈希按 `CODEX_HOME` 原始大小写隔离，适配可能区分大小写的 Mac 卷。估算历史不保存任务正文或原始账户标识；费用索引保留计算所需的本机日志路径、线程/轮次身份、显示标题和数值元数据，不能视为匿名文件。它们仅保存在本机，不应随发布包或源码交接上传。

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
5. **估算与重置时间**：先检查“校准中”，再积累真实有效段；重启、周期重置及采样中断不能跨断档配对。检查恢复进度、重置倒计时过期后的等待刷新状态、可用重置卡张数的缺失/部分/过期状态，以及多额度池之间的隔离。估算历史不得包含任务正文或账户标识原值。
6. **用量趋势**：核对每秒 tick、20 秒后向平均与最近 60 秒显示；大响应到达后应分摊至 20 个连续速率点。确认缓存包含在输入中、总体与各任务口径一致，启动/日志缺口/睡眠恢复不补零，隐藏窗口后仍保持用量采样口径。
7. **费用与周期历史**：先等待历史回填结束，再核对已知模型的输入、缓存、输出计价及未知价格；检查父任务/子代理去重、归档任务、长任务跨周期拆分、模型/轮次明细、搜索和排序。确认历史周期能切换，本地补记重置或修正时间在重启后保留；更正不触发账户重置。核对账户或额度池切换后的周期边界和当前选择，并保持本机全部普通任务的汇总口径。
8. **硬件**：对照活动监视器与系统工具检查 CPU、物理内存、磁盘增量，记录双方统计口径；核实 Codex 子进程和 HUD 自身排除。首轮、进程退出或权限受限应保留缺失/部分状态，GPU/显存不得显示虚构的零。
9. **桌面与设置**：检查 Retina、缩放、外接屏、显示器移除、全屏、睡眠恢复、锁定、置顶与不透明度；重启后设置仍存在。核实完成提醒、通知选项及本地历史不会被重复提示；打开费用报告时确认可滚动、展开任务树及收起窗口。
10. **自启与分发**：安装到固定目录后主动开启并重新登录检查，再关闭并检查取消；解压 ZIP 后复测执行权限与启动。若做发布签名，另行检查签名、公证及目标机器启动。

可供接手者使用的检查命令示例：

```bash
plutil -lint artifacts/release/macos/osx-arm64/CodexHud.app/Contents/Info.plist
codesign --verify --deep --strict --verbose=2 artifacts/release/macos/osx-arm64/CodexHud.app
spctl --assess --type execute --verbose=4 artifacts/release/macos/osx-arm64/CodexHud.app
(cd artifacts && shasum -a 256 -c CodexHud-1.7.3-osx-arm64.zip.sha256)
```

签名/系统评估命令只适用于对应的签名分发验收；未签名构建被拒绝不等于应用功能结论。Windows 的 `--ui-check`、`--diagnostics`、`--soak-seconds` 与 `--exit` 诊断入口没有在 Mac 版提供等价实现；Mac 端需要单独记录真实测试证据。
