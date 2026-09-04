# CopyProject

CopyProject 用于在 **WinCC Runtime 保持运行** 的情况下备份 WinCC Classic 项目。

目标很简单：不停止 WinCC，不修改源项目目录，不复制历史归档数据库，最终只生成一个普通 ZIP。ZIP 可以复制到另一台安装了兼容 WinCC 版本的电脑上解压使用。

> 本项目不是 Siemens 官方工具，请在生产环境使用前先在测试项目上验证恢复流程。

## 主要特性

- WinCC Runtime 无需停止
- 不 detach、不 offline 原始 WinCC 数据库
- 源项目目录保持只读，不创建临时文件
- 在线复制项目组态数据库与 Runtime 数据库
- 不备份 Alarm Logging / Tag Logging 等历史归档数据库
- 保留报警、变量归档、画面、脚本等工程组态文件
- 最终仅生成普通 `.zip`
- 临时文件只创建在用户选择的备份目录中
- 任务完成后自动删除临时工作目录
- ZIP 使用 `.partial` 临时文件，完成后才改名为正式 `.zip`
- 普通工程文件遇到短暂占用时自动重试
- SQL 备份/恢复操作不使用 30 秒命令超时
- 基于 .NET Framework 4.6.2，面向 Windows 10 / Windows 11

## 工作原理

运行中的 WinCC 项目数据库通常由本机 SQL Server `WINCC` 实例占用，不能直接复制 MDF/LDF。

CopyProject 不绕过 SQL Server 文件锁，而是使用 SQL Server 自身的在线备份机制：

```text
WinCC Runtime
      │
      │ 保持运行
      │
      ├─ 普通工程文件 ────────────────┐
      │                               │
      ├─ Project.mdf/ldf              │
      │      └─ COPY_ONLY BACKUP      │
      │          └─ RESTORE 临时库     │
      │              └─ DETACH        │
      │                               │
      └─ ProjectRT.mdf/ldf            │
             └─ COPY_ONLY BACKUP      │
                 └─ RESTORE 临时库     │
                     └─ DETACH        │
                                      ▼
                              临时完整工程副本
                                      │
                                      ▼
                                     ZIP
```

CopyProject 只会 detach 自己创建的临时数据库，不会 detach 正在运行的 WinCC 数据库。

## 备份内容

| 内容 | 是否备份 |
|---|---|
| `.mcp` 项目文件 | ✅ |
| 画面 / 脚本 / 工程配置 | ✅ |
| 项目组态数据库 `Project.mdf/.ldf` | ✅ |
| Runtime 数据库 `ProjectRT.mdf/.ldf` | ✅ |
| Alarm Logging / Tag Logging 的工程组态 | ✅ |
| 历史报警数据库 | ❌ |
| 历史变量归档数据库 | ❌ |
| `.lck` 临时锁文件 | ❌ |

CopyProject 通过数据库白名单方式只处理项目主数据库与 Runtime 主数据库。其他 MDF/LDF 不进入备份。

## 临时文件策略

CopyProject 不在源项目、系统 TEMP、ProgramData 或 SQL Data 目录中创建自己的工作文件。

假设用户选择：

```text
D:\Backup
```

备份过程中只会临时出现：

```text
D:\Backup\
├─ .CopyProject_<GUID>\
│  ├─ ProjectName\
│  └─ Sql\
└─ ProjectName_20260904_103000.zip.partial
```

完成后：

```text
D:\Backup\
└─ ProjectName_20260904_103000.zip
```

正常完成时只保留最终 ZIP。

## SQL Server 权限

SQL Server 服务账户需要能够读写 CopyProject 的临时工作目录。

CopyProject 会读取 `MSSQL$WINCC` 服务账户，并只对当前任务的 `.CopyProject_<GUID>` 目录临时授予 `Modify` 权限。

该权限不会添加到整个磁盘或整个备份目录。临时目录删除后，对应 ACL 也随目录一起消失。

CopyProject 默认仍以普通用户权限运行。如果当前用户没有修改临时目录 ACL 的权限，程序会提示选择其他目录或以管理员身份运行。

## ZIP 恢复

输出示例：

```text
ProjectA_20260904_103000.zip
└─ ProjectA\
   ├─ ProjectA.mcp
   ├─ ProjectA.mdf
   ├─ ProjectA.ldf
   ├─ ProjectART.mdf
   ├─ ProjectART.ldf
   ├─ GraCS\
   ├─ Scripts\
   └─ ...
```

将 ZIP 复制到目标电脑后直接解压即可获得普通 WinCC 项目目录。

如果项目在另一台电脑运行，请按照 WinCC 本身的要求确认：

- WinCC 版本兼容
- 项目计算机名称配置正确
- 所需 WinCC 组件、驱动和授权已安装

## 系统要求

- Windows 10 / Windows 11
- SIMATIC WinCC Classic
- 本机 WinCC SQL Server 实例 `\.\WINCC`
- .NET Framework 4.6.2 或更高的兼容 .NET Framework 4.x 运行环境

当前版本暂不支持直接将备份工作目录放到 UNC 网络路径或映射网络驱动器。

## 当前版本设计原则

CopyProject 的目标不是历史数据归档系统，而是：

> 在不停止 WinCC Runtime 的情况下，快速得到一个可以复制、解压和继续使用的 WinCC 项目备份。

因此当前版本不会加入 VSS、VDI、CHECKDB、历史归档备份等额外机制，尽量降低对正在运行系统的额外负载和程序复杂度。

## 开发状态

2.0 分支正在重构在线备份流程，主要改进：

- [x] .NET Framework 4.6.2
- [x] 独立 BackupEngine
- [x] 源项目零写入
- [x] 目标目录一次性隐藏工作区
- [x] SQL 临时 ACL
- [x] Config / Runtime 在线数据库复制
- [x] 普通文件重试
- [x] `.zip.partial` 防止半成品 ZIP
- [x] 自动清理工作目录
- [ ] WinCC SQL 实例自动发现
- [ ] 更完整的 WinCC 版本兼容测试
- [ ] 现场恢复测试记录

## License / Disclaimer

本项目与 Siemens 无隶属关系，也不是 Siemens 官方备份工具。

请在重要生产系统使用前自行完成备份与恢复测试。
