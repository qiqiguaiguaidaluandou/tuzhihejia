# 图纸核价 / 挑图纸系统 — PC 客户端骨架

WPF 桌面客户端 + 无状态后端网关。当前**外部接口（EBS / PLM / SRM / DHR）尚未提供**，故后端网关
内部用占位防腐层 `FakeDataSource` 造数顶上；客户端**只走一条真 HTTP 链路**连后端，外部接口到位后
**只换后端防腐层实现、客户端 UI 不动**。（早期客户端 Mock 网关 / 样例数据生成器已移除，链路收敛为一条。）

> 字段口径以 `docs/方案设计.md` 为准（核价待填列 = **目标价**；挑图待填列 = **是否机加中心可以做**）。
> 完整设计、上线路线图、界面预览均在 `docs/` 下（`方案设计.md` / `路线图.md` / `WPF界面预览.html`）。
> 早期 HTML 原型 `界面原型_v2.html` 已废弃，不作参考。

## 工程结构

```
TZHJ.sln
src/
  TZHJ.Core/            领域模型 + 网关契约 + 字段 schema + 本地路径/批次窗口规则（跨平台，无 WPF）
    Enums/              FlowType / RowStatus / BatchLocation / FieldSource / FieldEditor
    Models/             Batch / MaterialRow / FieldDefinition / CollectionWindow / ClientConfig / LocalPaths ...
    Contracts/          IAuthGateway / IConfigGateway / IDataGateway / ISubmitGateway / ILocalBatchStore / DTOs
    Schemas/            FieldSchemas（核价6列/挑图16列首批字段）、CollectionSchedules（核价2窗/挑图3窗）
  TZHJ.Infrastructure/  契约实现（跨平台，无 WPF）
    Gateways/Http/      HttpAuth / HttpConfig / HttpData / HttpSubmit / HttpAudit / HttpOperationLog（连后端）
    Fields/             DefaultFieldProvider（内置字段 schema，可被下发 ClientConfig 覆盖）
    Storage/            LocalBatchStore（落本地/读写xlsx/移目录/异常池/完整性校验）、ExcelGridIO、BatchManifest
    DependencyInjection.cs   AddTzhjHttpInfrastructure(...)  ← 客户端唯一链路注册入口
  TZHJ.Gateway/         无状态后端网关（net8.0，ASP.NET Core）：端点 + 防腐层(FakeDataSource 占位) + 存储
  TZHJ.App/             WPF 客户端（net8.0-windows，仅此工程需 Windows）：MVVM + DI
    Services/  ViewModels/  Views/  Converters/
    Styles/FluentTheme.xaml   克制 Fluent 商务风主题（蓝 #2563EB；按钮/输入/DataGrid/卡片/导航样式）
    依赖 FluentIcons.Wpf（矢量 Fluent 图标，随程序打包，跨 Win10/11 一致）
```

### 三类工作（为什么 UI 大部分不依赖接口）

因"本地即状态、软件视图 = 本地文件夹视图"，依赖外部的只有 3 个边界点：

- **A 完全不依赖接口**：批次列表（映射文件夹）、可编辑网格、行/批次状态机、提交闸门、暂存、
  异常池、图纸有无标识与完整性校验、"在资源管理器中打开"。→ 已实现。
- **B 依赖接口但后端占位顶住**：`IAuthGateway`（登录）、`IDataGateway`（取数）、`ISubmitGateway`（回传）、
  `IConfigGateway`（配置/时间窗/字段下发）。→ 客户端经 HTTP 调后端；后端 `FakeDataSource` 占位造数，
  含可配缺图率/回传失败率用于调边界态（`appsettings.json` 的 `Fake` 节）。

## 构建与运行

**Windows（开发/运行）：客户端只走 HTTP 链路，需先起后端网关。**

```powershell
dotnet run --project src/TZHJ.Gateway     # 1) 起后端网关（默认 http://localhost:8080）
dotnet run --project src/TZHJ.App         # 2) 起客户端（连 appsettings.json 的 Http:BaseUrl）
```

登录：工号任意非空（如 `10086`）、密码任意（填 `fail` 可演示登录失败）。本地根目录见
`src/TZHJ.App/appsettings.json` 的 `Http:LocalRoot`。登录后会自动补拉，待处理批次即从后端取下。

**Linux / macOS / CI（仅验证编译，不能运行 WPF）：**

```bash
dotnet build TZHJ.sln -p:EnableWindowsTargeting=true
```

## 真接口到位后怎么切换

客户端链路已经唯一且固定（HTTP → 后端网关），切换只发生在**后端防腐层**：

1. 把 `TZHJ.Gateway/AntiCorruption/FakeDataSource`（占位造数）替换为真正调 EBS / PLM / SRM 的实现。
2. 认证 `FakeAuthService` / `FakeTokenService` 接 DHR；配置/审计存储按需落 PostgreSQL。

客户端 UI、ViewModel、本地存储、HTTP 网关**均不改动**。

## 字段配置化

表单列、xlsx 列、网格列都由 `FieldDefinition` 列表驱动（`Schemas/FieldSchemas.cs`）；
登录后 `IConfigGateway` 下发的字段集会覆盖默认 schema（`DefaultFieldProvider.Apply`）。**加字段不改代码。**

## 自动更新

按方案选型走 **ClickOnce**。发布配置在 `src/TZHJ.App/Properties/PublishProfiles/ClickOnceProfile.pubxml`
（普通 `dotnet build` 不读它，只在显式发布时生效，故不影响日常编译 / CI）。

**在 Windows 上发布**（需 .NET 8 SDK）：

```powershell
dotnet publish src/TZHJ.App/TZHJ.App.csproj -p:PublishProfile=ClickOnceProfile
```

或在 Visual Studio 右键 `TZHJ.App` → 发布 → 选 `ClickOnceProfile`。

上线前要改 `.pubxml` 里两处占位：`PublishUrl` / `InstallUrl` 指向真实发布目标（文件共享
`\\server\share\TZHJ` 或 https 站点）。更新策略已设为 **启动前同步检查**（`UpdateMode=Foreground`）——
管理员发一次新版，所有客户端下次启动即拉到最新（呼应"改一处、不逐台重打包"）。**每次发布请递增
`ApplicationVersion` 末位**，客户端才能检测到新版本。框架依赖发布默认需目标机有 .NET 8 桌面运行时
（Bootstrapper 可代装）；想免预装可在 `.pubxml` 把 `SelfContained` 改 `true`（体积更大）。

## 与外部系统待确认项（来自 `docs/方案设计.md` §9，影响最终回传/取数实现）

- 取数携带标识（是否工号）及源系统据此返回本人数据的方式；两流程 EBS 取数如何区分。
- 挑图回传 EBS 的关联键（是否即 EBS-ID）与回传字段映射；核价回传 SRM 字段映射。
- PLM "是否存在变更" 字段的取值/含义与接口形态（是否与图纸同接口返回）、图纸版本策略。
- DHR 认证协议与可提供字段；回传幂等 / 部分失败处理。
