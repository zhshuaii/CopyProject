# CopyProject

WinCC Classic 项目的在线备份验证版。保留 WinForms、.NET Framework 4.6.2 和 SQL 原生备份路线，无第三方运行时包。

**当前工作分支：`fix/online-backup-safety`。旧 `feature/net462-online-backup-v2` / PR #1 已被本修复工作取代，不再作为交付候选。`master` 不因本次修复自动改变。**

这不是 Siemens 官方工具。构建和隔离 SQL 测试不等于真实 WinCC 恢复验收。生产使用前必须在相同 WinCC/SQL 版本的测试环境验证解压、打开、激活和功能。

## 支持范围

本机固定实例 `.\WINCC`，Windows 集成认证；源工程根目录包含同名 `.mcp`、`Project.mdf/.ldf` 和 `ProjectRT.mdf/.ldf`。两个数据库必须能明确识别为 ONLINE 用户数据库，且各自恰好有一个数据文件和一个日志文件。未知身份、离线、额外数据文件、FILESTREAM 等结构均拒绝；不回退到共享复制 MDF/LDF。

源和目标仅接受已存在的本地 NTFS/ReFS 目录。拒绝 UNC、映射网络盘、SUBST/短文件名别名、Junction、符号链接、挂载点、备用数据流，以及超过 240 字符的任一工作路径。目标不得位于源目录内。这里是保守支持范围，不是通用文件系统同步器，也不承诺抵御有权限实时篡改目录结构的恶意进程。

Runtime 可以继续运行，但备份期间不要编辑、保存或切换/激活工程。普通文件在复制前后核对长度和时间，并在 SQL 处理及 ZIP 生成后复核文件清单和 SHA-256。两个 SQL 数据库分别取得一致备份；这仍不是工程文件与两个数据库的全局同一时刻快照。

## 备份内容

普通工程文件和空目录，以及两个经过在线备份、还原、分离的数据库副本进入 ZIP。源目录中的 `.mdf/.ldf/.ndf/.lck` 不作为普通文件复制。发现 `.bak/.trn` 时明确停止，避免猜测它们究竟是历史数据库备份还是需要保留的工程副本；不会删除源文件，也不会静默忽略不明文件。

扩展名策略不识别任意自定义历史数据格式。尚未针对所有 WinCC 工程布局形成兼容矩阵，不应把它解释为“任意项目里的所有历史数据都能准确识别并排除”。

ZIP 中有一个以项目名命名的工程目录，以及根目录的 `CopyProject-backup-manifest.txt`（任务编号、时间、文件 SHA-256）。恢复时解压并使用工程目录；不要把生成的 ZIP 本身当作数据库备份 BAK。

## 工作过程与权限

先固定 UI 输入、核实路径和 SQL 权限、枚举工程并估算空间，再创建任务级工作区。SQL 操作是：

```text
COPY_ONLY BACKUP + CHECKSUM
  -> FILELISTONLY 严格核对完整 D/L 清单
  -> RESTORE 新的任务临时库（所有文件显式 MOVE，不使用 REPLACE）
  -> 核对临时库名称、所有者、物理路径
  -> DETACH 临时库
  -> 生成独占 partial ZIP，并重新读取校验
  -> 确认 SQL 临时库已回收、删除工作区
  -> 不覆盖地发布 ZIP
  -> 删除任务记录
```

应用只读取源文件，不 detach/offline 原始数据库，不改原始数据库内容或权限。SQL 自身的正常备份历史和日志不会被删除；运行中的 WinCC/SQL 仍会写自己的源文件。

SQL 登录需要完整元数据可见性（`VIEW ANY DEFINITION` 和 `VIEW ANY DATABASE`，或 sysadmin），每个源数据库的 `BACKUP DATABASE` 权限，以及创建/恢复数据库权限。新临时数据库所有者必须是当前登录，才允许本工具分离或删除。Windows 管理员身份不自动提供这些 SQL 权限，程序不会自动授予 SQL 管理权限。

程序从 `MSSQL$WINCC` 服务配置读取实际 Windows 账户并解析 SID；无法确认就失败，不猜测虚拟账户。仅新建工作目录设置私有 ACL，保留调用用户/SYSTEM 的 FullControl，给所需 SQL 服务账户 Modify；不修改目标父目录或整个磁盘的权限。默认保持 `asInvoker`。

短 SQL 查询 30 秒，分离/清理 60 秒，BACKUP/RESTORE 最多 3600 秒；取消会请求停止当前 SQL 命令，然后通过新连接核实并清理。空间预算包含普通副本、SQL BAK、还原文件、ZIP 和余量，但运行中数据库增长仍可能导致磁盘不足。

## 取消、关窗和残留

取消按钮和运行时关窗都先请求取消，再等待工作线程结束清理；不会直接关闭主窗体并遗弃任务。操作系统强制终止、断电和服务异常不能依赖 finally 保证无残留。

所有工具工作文件均在用户选择的目标目录，不使用系统 TEMP/ProgramData 存放应用备份工作文件：

```text
.CopyProject_<任务GUID>/          工作副本和 SQL BAK
.CopyProject_<任务GUID>.task      持续刷盘的资源关联和阶段记录
.CopyProject_<任务GUID>.zip.partial
Project_日期_时间_<任务GUID>.zip   发布后的文件
```

正常成功仅剩 ZIP。失败或取消时尝试回收本任务资源；SQL 状态无法确认就保留工作目录，避免删除仍附加的数据库文件。原始错误、清理错误和待核实资源分开显示，可复制详情。

下一次选择同一目标目录时，会发现活动或遗留的 `.CopyProject_*` 并拒绝开始新任务。**当前版本仅识别和报告，不自动接管崩溃遗留任务，也不扫描其他目标目录。** 人工处理必须核对 task 记录、数据库实际物理路径和是否仍有活动进程；不得仅凭前缀批量删库/目录。旧实现没有 task 记录的残留也只报告，不猜测归属。

ZIP 发布后删除 task 记录失败时，结果会明确显示“ZIP 已发布，但清理未完成”，保留输出位置；不会谎称完整成功，也不会删除已验证的 ZIP 来掩盖残留。

## 构建和测试

使用具备 .NET Framework 4.6.2 Targeting Pack 的 Visual Studio/MSBuild。没有 NuGet 依赖，不需要安装 .NET 3.5 或 ClickOnce。便携交付为 exe 和 exe.config。

```powershell
msbuild .\CopyProject.sln /m /t:Rebuild /p:Configuration=Release '/p:Platform=Any CPU'
.\CopyProject.Tests\bin\Release\CopyProject.Tests.exe
```

独立测试工程是控制台测试执行器，使用实际产品 C# 代码和受限替身，不是 Python 等价推断。集成测试只能连接以 `CopyProjectCI` 开头的专用 LocalDB 实例，拒绝生产实例：

```powershell
SqlLocalDB create CopyProjectCI -s
$env:COPYPROJECT_TEST_INSTANCE = '(localdb)\CopyProjectCI'
.\CopyProject.Tests\bin\Release\CopyProject.Tests.exe --integration
SqlLocalDB stop CopyProjectCI -k
SqlLocalDB delete CopyProjectCI
```

GitHub Actions 在 Windows 上以 warnings-as-errors 构建 Debug/Release，运行两种配置的回归测试及独立 SQL BACKUP/RESTORE/DETACH/ZIP 解压再附加测试。测试失败不打包验证版；保留构建日志、测试日志、源码快照及提交信息。见 `docs/VALIDATION.md`。
