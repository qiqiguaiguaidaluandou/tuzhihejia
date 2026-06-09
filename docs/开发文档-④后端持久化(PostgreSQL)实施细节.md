# 开发文档-④ 后端持久化 (PostgreSQL) 实施细节

## 1. 概述
为了满足生产环境下的数据追溯、审计以及客户端“补拉”逻辑的准确性，后端网关 (TZHJ.Gateway) 已从内存存储切换为基于 **PostgreSQL** 的持久化方案。本文档详细记录了已完成的审计存储实现及后续配置持久化的计划。

---

## 2. 已完成：审计存储 (Audit Store) 持久化

### 2.1 技术实现
*   **ORM 框架**：Entity Framework Core 8.0
*   **驱动程序**：Npgsql.EntityFrameworkCore.PostgreSQL
*   **生命周期**：`IAuditStore` 现由 `Scoped` 作用域的 `PgAuditStore` 实现，确保每个请求拥有独立的数据库上下文。

### 2.2 核心代码变更
*   **实体模型** (`Stores/AuditRecord.cs`)：定义了映射到数据库 `audit_records` 表的结构，包括主键、字段长度限制及列名映射。
*   **数据库上下文** (`Stores/TzhjDbContext.cs`)：
    *   管理 `AuditRecords` 集合。
    *   **性能优化**：在 `OnModelCreating` 中为 `(Flow, EmployeeId, WindowStart, WindowEnd)` 建立了复合索引 `idx_audit_lookup`，确保客户端登录补拉时的查询速度。
*   **持久化逻辑** (`Stores/PgAuditStore.cs`)：
    *   **时区处理**：强制将所有 `DateTime` 转换为 **UTC** 时间存储，符合 PostgreSQL 的最佳实践。
    *   **Find 逻辑**：支持基于流程、工号和窗口起止时间的精确匹配。

### 2.3 数据库表结构 (`audit_records`)
| 列名 | 类型 | 说明 |
| :--- | :--- | :--- |
| audit_id | VARCHAR(36) | 主键，格式：AUDIT-日期-GUID |
| flow | INTEGER | 流程类型 (0: Pricing, 1: DrawingSelection) |
| employee_id | VARCHAR(50) | 操作员工号 |
| batch_key | VARCHAR(100) | 批次唯一键 |
| window_start | TIMESTAMPTZ | 窗口开始时间 (UTC) |
| window_end | TIMESTAMPTZ | 窗口结束时间 (UTC) |
| target | VARCHAR(20) | 目标系统 (SRM/EBS) |
| row_count | INTEGER | 本次提交行数 |
| submitted_at | TIMESTAMPTZ | 提交记录时间 (UTC) |

---

## 3. 部署与配置指南

### 3.1 运行环境要求
*   需安装 **PostgreSQL 13+** 服务器。
*   需安装 `.NET 8 SDK`。

### 3.2 配置步骤
1.  **修改连接字符串**：在 `src/TZHJ.Gateway/appsettings.json` 中，修改 `DefaultConnection` 节（注意：若连接远程 Linux 且 5432 端口被占用，请指定 5433）：
    ```json
    "ConnectionStrings": {
      "DefaultConnection": "Host=服务器IP;Port=5433;Database=tzhj_db;Username=postgres;Password=你的密码"
    }
    ```

### 3.3 远程 Linux PostgreSQL 环境快照
若在 Linux 裸机安装且需规避 Docker 端口冲突，请确保：
*   **端口**：`postgresql.conf` 中 `port = 5433`。
*   **监听**：`postgresql.conf` 中 `listen_addresses = '*'`。
*   **授权**：`pg_hba.conf` 末尾添加 `host all all 0.0.0.0/0 md5`。
*   **防火墙**：`sudo ufw allow 5433/tcp`。
2.  **安装本地工具**（如尚未安装）：
    ```powershell
    dotnet tool restore
    ```
3.  **应用数据库迁移**：
    在项目根目录下运行：
    ```powershell
    dotnet dotnet-ef database update -p src/TZHJ.Gateway
    ```

---

## 4. 后续计划：配置中心持久化 (Task 3.5)

### 4.1 目标
将目前在 `InMemoryConfigStore` 中硬编码的 `ClientConfig`（包括字段定义、时间窗规则、保留天数等）搬入数据库。

### 4.2 预期收益
*   **动态调整**：管理员可以在不重启网关的情况下，通过修改数据库即时调整客户端的作业网格列（加字段）或调整业务时间窗。
*   **按需下发**：支持针对不同部门、不同工号下发不同的配置模板。

### 4.3 实施方案预告
1.  **表设计**：创建 `client_configs` 表，使用 JSONB 字段存储动态的字段 Schema。
2.  **实现 `PgConfigStore`**：替换目前的内存版本。
3.  **初始化脚本**：将目前的默认 Schema 作为初始种子数据导入数据库。

---

**文档维护**：Gemini CLI
**最后更新**：2026-06-09
