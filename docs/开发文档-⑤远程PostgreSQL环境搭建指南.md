# 开发文档-⑤ 远程 PostgreSQL 环境搭建指南

## 1. 概述
由于开发环境（Windows 笔记本）与生产/测试数据库环境（Linux 服务器）分离，且服务器 5432 端口常被 Docker 占用，本文档记录了在远程 Linux 服务器上从零搭建并配置 PostgreSQL 的全过程。

---

## 2. Linux 服务器端操作 (Ubuntu/Debian)

### 2.1 安装 PostgreSQL
```bash
sudo apt update
sudo apt install postgresql postgresql-contrib
```

### 2.2 修改监听端口与远程访问
1.  **修改主配置文件**：
    找到 `/etc/postgresql/版本号/main/postgresql.conf`（通常版本为 14 或 16）。
    ```bash
    sudo nano /etc/postgresql/*/main/postgresql.conf
    ```
    *   **修改端口**：找到 `#port = 5432`，去掉 `#` 并改为 `port = 5433`。
    *   **开启监听**：找到 `#listen_addresses = 'localhost'`，去掉 `#` 并改为 `listen_addresses = '*'`。

2.  **修改授权配置文件**：
    编辑 `/etc/postgresql/*/main/pg_hba.conf`。
    ```bash
    sudo nano /etc/postgresql/*/main/pg_hba.conf
    ```
    在文件末尾添加以下行，允许所有外部 IP 通过密码访问：
    ```text
    host    all             all             0.0.0.0/0               md5
    ```

3.  **重启服务并开放防火墙**：
    ```bash
    sudo systemctl restart postgresql
    sudo ufw allow 5433/tcp
    ```

### 2.3 设置数据库密码与创建业务库
PostgreSQL 默认使用 Peer 认证，远程连接必须先设置数据库密码：
```bash
# 切换到数据库管理账号
sudo -i -u postgres

# 连接数据库（指定新端口 5433）
psql -p 5433

# 执行 SQL 语句
ALTER USER postgres WITH PASSWORD '你的强密码';
CREATE DATABASE tzhj_db;

# 退出
\q
exit
```

---

## 3. 开发端 (Windows 笔记本) 配置

### 3.1 修改连接字符串
在 `src/TZHJ.Gateway/appsettings.json` 中配置远程连接。**注意必须显式指定 Port 端口**：
```json
"ConnectionStrings": {
  "DefaultConnection": "Host=服务器真实IP;Port=5433;Database=tzhj_db;Username=postgres;Password=你的强密码"
}
```

### 3.2 同步表结构 (EF Core Migrations)
在笔记本终端的项目根目录下执行，跨网络创建表：
```powershell
dotnet dotnet-ef database update -p src/TZHJ.Gateway/TZHJ.Gateway.csproj
```

---

## 4. 验证与排查

### 4.1 端口连通性验证
在笔记本（PowerShell）执行：
```powershell
Test-NetConnection -ComputerName 服务器IP -Port 5433
```
*   若 `TcpTestSucceeded` 为 `True`，说明网络和防火墙已打通。

### 4.2 本地连接验证
在 Linux 服务器上验证：
```bash
psql -h localhost -p 5433 -U postgres -d tzhj_db
```
*   若能进入提示符，说明本地监听正常。

### 4.3 查看已创建的表
```bash
# 在 Linux 上执行
sudo -u postgres psql -p 5433 -d tzhj_db -c "\dt"
```
*   应当看到 `audit_records` 和 `__EFMigrationsHistory` 两张表。

---

**文档维护**：Gemini CLI
**最后更新**：2026-06-09
