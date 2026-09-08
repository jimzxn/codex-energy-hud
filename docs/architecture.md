# 架构与本地验证

项目采用 C#、.NET 10 与 WPF，面向 Windows x64。数据采集集中在 `src/CodexHud.Core`，窗口、控件、设置与诊断入口位于 `src/CodexHud`；UI 根据采样结果刷新，不直接访问数据库或硬件接口。

## 数据层

| 组件 | 职责 |
| --- | --- |
| `QuotaProvider` | 管理专属隐藏 app-server，通过只读账户接口采集额度与校准所需身份信息 |
| `ActivityProvider` | 读取本地任务元数据和增量日志，提供活动证据、轮次时长与任务自身 Token |
| `DesktopActivityProvider` | 只读订阅桌面实时状态，校验连续修订号，补充审批、输入等待与轮次状态 |
| `TaskNoticeTracker` | 按任务、轮次、请求去重，识别监控期间的新完成并抑制历史提醒 |
| `UsageLedgerProvider` | 独立采集本机新增响应 Token，包含归档任务及独立子任务，避免 UI 筛选影响估算 |
| `QuotaTokenEstimator` | 按账户、额度池与周期对齐 Token 和额度变化，管理校准、经验范围及历史分段 |
| `HardwareProvider` | 采集整机及 Codex 进程组 CPU/GPU、内存和整机物理磁盘吞吐 |

采样结果携带时间、来源及有效性。缺失值保持未知，失败保留最后有效读数并标记过期；任务状态只表达本地记录支持的活动迹象。Token 快照不重复累加，估算也不代表套餐固定 Token 上限。完整数据口径见[使用说明](../README.md#数据含义)。

## 任务与进程归属

当前实现提供整机和 Codex 进程组资源，不提供逐任务资源。进程组通过进程身份与父子关系识别，并排除挂件及额度采集子进程。

逐任务监控需要受支持的来源提供“任务 ID → Windows PID”，并结合进程创建时间防止 PID 重用。共享宿主、工作目录、执行会话标识或相近活动时间都不足以证明归属；独立 app-server 也不能假定拥有桌面应用已加载的任务。

未来即使取得映射，也只能统计已确认关联的本地工具进程。远程模型计算、共享桌面开销与未关联进程应分别处理。进程 I/O 字节数还可能包括网络及设备操作，不能直接等同物理磁盘读写。

## 本地验证

在仓库根目录运行以下命令。首次构建会通过 Node.js 下载并校验独立 SDK；不需要替换系统 .NET。

```powershell
./scripts/build.ps1
```

该入口构建应用并运行确定性测试，覆盖额度解析与协议、活动/时长、Token 读取、估算与硬件算法。实际硬件、账户和界面检查须显式运行，命令见[使用说明](../README.md#从源码构建)。这些结果依赖执行机器，不能由单元测试替代。

构建自包含应用及运行隔离界面检查：

```powershell
./scripts/build.ps1 -Publish
./artifacts/release/CodexHud/CodexHud.exe --ui-check artifacts/validation/ui-local
```

界面检查会读取真实本地数据、生成截图和 JSON 回执并退出；它使用隔离设置。截图可能包含任务标题、额度和目录，验证产物应保留本地，分享前检查内容。

需要持续诊断时，先正常退出已有挂件，再运行：

```powershell
./artifacts/release/CodexHud/CodexHud.exe --exit
./artifacts/release/CodexHud/CodexHud.exe --diagnostics artifacts/validation/soak-local --soak-seconds 7200
```

确认原实例已退出后再启动诊断。诊断完成不会关闭挂件；有效采样时长应以本次 JSON 回执为准。原始资源采样和本机验收报告不随源码提交。
