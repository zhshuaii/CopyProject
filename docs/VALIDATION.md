# 修复范围与验收记录

## 来源与分支

基线：master `072cd715c4b219a48de127bdb2806d798bd49c65`。
参考旧实现：feature/net462-online-backup-v2 `bac1d4d8d759208963b4e6e5793f2718665fa173`。
新实现：fix/online-backup-safety，从 master 独立建立，不合并旧 PR #1。

两个版本通过 Git bundle 和 git archive 获取，在本地创建独立工作树，对照后实施。当前编辑容器是 Linux，不提供 Windows/.NET Framework 运行环境；实际编译、C# 执行和 SQL 集成测试由 Windows Actions 节点完成，不能称为 Linux 本地构建成功。

## 审计项处理

| 项目 | 本次处理 | 证据 |
|---|---|---|
| CP-01 跨线程控件读取 | UI 线程固定字符串参数；进度回到 UI | WinForms C# 回归 |
| CP-02 退出生命周期 | 取消、关窗等待回收；SQL 超时分级 | 分阶段取消、UI 关闭等待 |
| CP-03 清理吞错 | 原始错误、清理错误、残留分别返回；task 记录刷盘 | 锁文件、SQL 清理故障替身、进程强杀 |
| CP-04 路径别名和链接 | 本地路径、真实路径校验、祖先句柄、遍历拒绝重解析点 | 边界、穿越、Junction/循环链接 |
| CP-05 未知附加状态回退 | 仅明确 ONLINE 身份，取消原始 MDF/LDF 回退 | 替身预检失败、真实 SQL 未附加 MDF |
| CP-06 RESTORE 漏类型 | 严格总数为二、一个 D 一个 L、逻辑名不重复；无 REPLACE | D/L 及额外/未知类型测试 |
| CP-07 工程一致性 | 清单、长度、时间、两次源内容复核；明确非全局快照 | 源修改测试；WinCC 现场待验 |
| CP-08 文件范围 | mdf/ldf/ndf/lck 排除；bak/trn 不猜测而停止 | 文件策略、ZIP 内容测试 |
| CP-09 命名竞争 | 同源互斥、GUID 文件名、CreateNew、发布不覆盖 | 输出碰撞测试；跨会话压力待验 |
| CP-10 预检查 | 路径/文件/库身份/元数据与备份恢复权限/空间/服务账户先核实 | 预检查回归和 SQL 集成 |
| CP-11 诊断 | 可复制详情、阶段进度、完整重试次数 | 错误聚合、重试次数测试 |
| CP-12 模板清理 | 删除空 Settings/Resources、ClickOnce、3.5 Bootstrapper；整理程序集/manifest | warnings-as-errors Debug/Release 构建 |
| CP-13 验证 | push/PR 构建与 C#、Windows、LocalDB 测试 | 对应提交的 Actions 记录和 TestResults artifact |
| CP-14 干净定义 | 不删 SQL 历史；区分已发布但 task 清理失败 | README 和结果模型 |

## 自动验证边界

普通 C# 测试中的 FakeDatabase 文件仅为故障注入和 ZIP/清理测试数据，不是有效 SQL 数据库。仅 `--integration` 执行真实 SQL 操作：建立两个隔离数据库、保持源库并发写入、在线备份/恢复/分离、校验无临时库、解压 ZIP、再次附加并读取已知表；另测元数据权限拒绝和未知 MDF 拒绝。该测试不运行 WinCC。

每次构建的实际结果以该提交对应的 Actions conclusion、TestResults 日志为准。本文不以计划测试替代执行结果，也不将一轮 CI 当作所有 Windows/SQL 版本兼容证明。

## 仍需现场验证，不能提前勾选

- [ ] 在实际 WinCC Classic、SQL、Windows 版本组合上完成备份和独立机器解压、打开、激活。
- [ ] 确认 Runtime 持续工作，报警/变量归档工程组态、画面、脚本和驱动可用。
- [ ] 在实际 SQL 服务账户/ACL/安全软件环境下验证失败回收。
- [ ] 大项目、磁盘耗尽、USB 拔出、SQL 服务中断和操作系统关机故障验证。
- [ ] 跨用户会话并发、目录实时变更、非标准 WinCC 工程布局兼容性。

崩溃遗留目前只检测并报告，不自动接管或自动恢复清理。普通文件的哈希复核不是 SQL 两库与文件的原子快照。不增加 VSS、VDI、CHECKDB、历史归档系统、驱动插件或新的 UI 框架来掩盖这些边界。
