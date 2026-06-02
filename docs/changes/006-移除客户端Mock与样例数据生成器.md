# 006 移除客户端 Mock 与样例数据生成器（链路收敛为单条 HTTP）

- 状态：已完成
- 日期：2026-06-02
- 关联提交：（待回填）

## 需求 / 改动

客户端原有两套数据来源，由 `appsettings.json` 的 `UseMock` 二选一：

- `UseMock=true`：客户端自带的 Mock 网关本地造数（离线、不连后端）；
- `UseMock=false`：经 HTTP 连后端网关（后端 `FakeDataSource` 占位造数）。

客户端那套 Mock 是静态造数、上线必然删除、对当前开发无价值。决定**彻底移除客户端 Mock 这条路**，
客户端只保留唯一的真 HTTP 链路；连带删除仅服务于"离线铺数据"的 `TZHJ.SampleData` 控制台工具。

> 注意：这不改变"数据仍是假的"——后端 `FakeDataSource` 仍是占位，真实数据要等 EBS/PLM/SRM 接口（路线图 B1/B2）。
> 本次只是把**客户端**的双链路收敛成单链路。

## 方案

- 删除客户端 6 个 Mock 网关 + `MockOptions` + 整个 `TZHJ.SampleData` 工程。
- **关键保留**：`DefaultFieldProvider` 虽在 `Gateways/Mock/` 文件夹下，但真链路也依赖它（字段 schema 提供者）——
  **移出**到 `Infrastructure/Fields/`、改命名空间，**不删**。
- DI 删掉 `AddTzhjMockInfrastructure`，只留 `AddTzhjHttpInfrastructure`；`App.xaml.cs` 去掉 `UseMock` 分支，恒走 HTTP。
- `appsettings.json` 删 `UseMock` 与 `Mock` 节，只留 `Http`。

## 实现

**删除**
- `src/TZHJ.Infrastructure/Gateways/Mock/`（MockAuth/Config/Data/Submit/Audit/OperationLog 共 6 个）
- `src/TZHJ.Infrastructure/Options/MockOptions.cs`
- `src/TZHJ.SampleData/`（整个工程）+ `TZHJ.sln` 中的工程/配置/嵌套条目

**移动**
- `Gateways/Mock/DefaultFieldProvider.cs` → `Fields/DefaultFieldProvider.cs`，命名空间 `TZHJ.Infrastructure.Mock... → TZHJ.Infrastructure.Fields`

**修改**
- `src/TZHJ.Infrastructure/DependencyInjection.cs`：删 `AddTzhjMockInfrastructure` 方法 + `using ...Gateways.Mock`，加 `using ...Fields`
- `src/TZHJ.App/App.xaml.cs`：去掉 `UseMock`/`MockOptions` 分支，恒调 `AddTzhjHttpInfrastructure`
- `src/TZHJ.App/appsettings.json`：删 `UseMock` + `Mock` 节
- `src/TZHJ.App/ViewModels/LoginViewModel.cs`、`tests/TZHJ.Tests/Storage/LocalBatchStoreTests.cs`：`DefaultFieldProvider` 的 using 改到 `Fields`
- 文档注释清理：`HttpOptions`（删对已删 `MockOptions` 的 `<see cref>`）、`IAuditGateway`/`IOperationLogGateway`/`FriendlyError`/`FakeOptions` 中"离线用 Mock…/UseMock"等失效描述
- `README.md`：工程结构、三类工作、构建运行、"真接口切换"章节全部改为单 HTTP 链路；删"样例数据生成器"章节

> 历史性 dated 文档（`docs/路线图.md`、`docs/无接口期开发清单.md`、`docs/开发文档-①…`）保留原样作为时点记录，不回改。

## 影响 / 取舍

- 客户端**不起后端就跑不了**（不再有离线模式）；开发/真机验收需先 `dotnet run --project src/TZHJ.Gateway`。
- 失去 `TZHJ.SampleData` 的"一键铺多状态本地数据"便利；看数据改为起后端→登录→补拉（补拉只产出"待处理"批次，
  不再有预置的"处理中/已处理/异常池"样例）。

## 验证

`dotnet build TZHJ.sln -p:EnableWindowsTargeting=true` 0 错 0 警；`dotnet test tests/TZHJ.Tests` 25/25 通过。
代码层已无 `Gateways.Mock` / `MockOptions` / `AddTzhjMockInfrastructure` / `UseMock` 残留引用。
WPF 真机待验：起后端后客户端登录、补拉、回传整链路正常。
