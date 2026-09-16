# 数据库基线脚本说明

## 当前状态

本目录目前提供：

- `Verify.sql`：检查当前数据库与代码所需结构之间的差异。
- `Baseline.sql`：在全新空业务数据库中创建完整结构。
- `Upgrade_Current.sql`：对已有历史数据库做保守、可审计的增量升级。
- `Export_Schema.sql`：**只读**导出当前库的最新结构（表 / 列 / 索引 / 外键 / 行数快照）。库结构变更后先跑它，再据此刷新上述脚本，避免人工回忆遗漏。

### 部署路径：以现有库为基准（2026-09-12 确认）

迁移到新服务器时，**以现有正式库的备份还原为准**，不从 `Baseline.sql` 从零建库。因此：

- `Baseline.sql` / `Upgrade_Current.sql` 属于**备用路径**，只在确实需要全新空库或修复历史库时才动用；它们是否与最新代码同步，不构成换机阻塞项。
- 换机流程：旧机备份 → 新机还原 → **在还原库上跑一次 `Verify.sql` 做结构体检** → 用 `Export_Schema.sql` 留一份结构快照备查。
- 库外还有两件事必须做：在新实例上重建应用登录账号并授予该库权限；更新 `appsettings*.json` 里的连接串。

### 结构变更后的刷新流程（2026-09-12 起）

1. 跑 `Export_Schema.sql` 拉取最新结构：

   ```text
   sqlcmd -S . -d ManhuaPipeline -U <账号> -P <密码> -f 65001 -W -s"|" -i Export_Schema.sql -o _schema_dump.txt
   ```

2. 用导出的列 / 索引 / 外键清单补齐 `Baseline.sql`（建表段）与 `Upgrade_Current.sql`（幂等增量段）。
3. 同步 `Verify.sql` 的必需表 / 必需字段 / 索引 / 外键清单。
4. 对正式库执行一次 `Verify.sql` 做闭环确认：必需表应全 `OK`，必需字段结果集应为空。

`ManhuaPipeline/Database/` 下的 `Upgrade_*.sql` 仍是各功能的权威来源，基线脚本只是它们的汇总快照。

### 未纳入基线的对象

正式库中存在 4 张历史备份表（`bak_Proj38_H3_20260827`、`bak_Proj38_RefImages_20260828`、`bak_SeedancePrompts_4907_20260904`、`bak_SeedancePrompts_5591_20260911`），属于一次性数据备份，**不进入基线**。刷新结构时按 `bak_` 前缀排除。

战斗段落骨架（Director V5 数据模板）由 `ManhuaPipeline/Database/Upgrade_FightArcTemplates.sql` 负责：新建 `FightArcTemplates` 表、给 `DirectorPlans` 增加 `FightArcType/FightSequenceJson`，并种入 6 套骨架。该脚本幂等，历史库升级时在 `Upgrade_Current.sql` 之后执行；新建空库时在 `Baseline.sql` 之后执行。

`CameraAtom` 的出厂原子数据同理，由 `ManhuaPipeline/Database/Upgrade_CameraAtom.sql` 种入，基线只建表。

`ProjectContinuityTables`（L2 连续性层六类表）由 `ManhuaPipeline/Database/Upgrade_连续性表.sql` 负责建表，已同步进 `Baseline.sql` 与 `Upgrade_Current.sql`。该表初始为空，由阶段 3 之后的抽取流程写入，基线不种数据。

脚本包含中文种子，使用 sqlcmd 时请加 UTF-8 参数：`sqlcmd -f 65001 -i Upgrade_FightArcTemplates.sql`。

`Baseline.sql` 的 2026-08-17 版本已通过 SQL Server 2016 语法解析、非空库安全拒绝测试、真实临时空库建表、`Verify.sql` 全量验证和应用层冒烟测试。

2026-09-12 按正式库结构补齐 7 张表与 20 余个字段后，**仅通过 `PARSEONLY` 语法解析（退出码 0）**，尚未在空库重新实测。下次建新库时需完整走一遍「Baseline.sql 执行方法」并更新文末实测表。

`Upgrade_Current.sql` 已通过 SQL Server 2016 `PARSEONLY` 语法解析，但尚未在当前历史库的还原副本上执行。因此：

- 不得直接在正式 `ManhuaPipeline` 数据库执行。
- 必须先还原一份包含真实历史数据的测试副本并完成升级前后对比。
- 副本验证通过且用户再次确认后，才能安排正式库维护窗口。

## Baseline.sql 安全属性

`Baseline.sql` 只能用于新建的空业务数据库：

- 在系统数据库中执行会立即拒绝。
- 检测到任意现有 `dbo` 用户表时会立即拒绝。
- 所有建表和种子数据位于同一事务中，失败时自动回滚。
- 不创建 SQL 登录账号，不包含数据库密码、API Key 或业务用户。
- 不读取、修改或迁移现有 ManhuaPipeline 数据库。

基线固定兼容 SQL Server 2016 和数据库兼容级别 130。

## Upgrade_Current.sql 安全属性

`Upgrade_Current.sql` 只面向已有历史数据的数据库或其还原副本：

- 拒绝在系统数据库以及缺少核心业务表的数据库执行。
- 使用单一事务和 `XACT_ABORT`；未处理异常会回滚本轮变更。
- 只创建缺失表/字段/索引/外键，或安全扩宽已知字段。
- 不删除表、字段、索引、约束或任何业务记录。
- 不包含旧 `Setup.sql` 中针对特定项目的 `EffectAssets` 数据搬移。
- 只对当前代码已有相同回退语义的空值做归一化：批次为 `1`、提示词集数为 `0`、提示词时长为 `11`。
- 建立唯一索引前检查重复数据；有冲突时跳过并输出 P1 警告。
- 建立外键前检查孤儿记录；有冲突时保留历史数据、跳过外键并输出 P1 警告。
- 已存在外键的删除规则与目标不同时，只输出 P2 警告，不自动删除重建约束。

2026-09-12 复查：当前历史库有 **227 条** `VideoGenerationTasks.PromptId` 孤儿记录（2026-08-17 记录为 1 条，其后持续增长）。预期升级结果是保留这些记录并跳过 `FK_VideoTasks_Prompts`，而不是静默删除数据。因此 `Verify.sql` 第 4 节中该项长期显示 `MISSING` 属于预期，不代表升级脚本有缺陷。

### Upgrade_Current.sql 验证方法

1. 使用已验证备份还原一个独立数据库，例如 `ManhuaPipeline_UpgradeTest_20260817_Codex`。
2. 先在副本执行 `Verify.sql`，保存全部结果集和各表行数。
3. 在副本执行 `Upgrade_Current.sql`，保存警告结果集。
4. 再执行 `Verify.sql`，对比表行数、缺失字段、索引、外键、重复键、孤儿记录和 Stage 数据。
5. 预期业务表行数不减少；允许的内容变化仅限文档列出的空值归一化及缺失基础种子。
6. 在明确处理孤儿数据前，`FK_VideoTasks_Prompts` 继续显示 `MISSING` 是预期结果。

历史副本含有视频任务时，不直接启动应用做冒烟测试。应用的后台视频轮询服务可能更新任务或调用外部视频 API；升级副本优先使用 SQL 验收，应用冒烟已由独立空库基线测试覆盖。

### Baseline.sql 执行方法

1. 使用 SQL Server 管理员创建一个新的空数据库。
   当前应用数据库账号没有 `CREATE DATABASE` 权限，这是正确的生产权限设置，不应为测试永久提升权限。
2. 在 SSMS 数据库下拉框中选择这个空数据库。
3. 整段执行 `Baseline.sql`。
4. 执行 `Verify.sql`。
5. 确认必需表全部为 `OK`、必需字段缺失结果为空、目标索引和外键全部为 `OK`。
6. 再使用指向该数据库的临时连接配置启动应用进行冒烟测试。

严禁在现有历史数据库上执行 `Baseline.sql`。即使误选，脚本也会因检测到已有业务表而拒绝执行。

## Verify.sql 安全属性

`Verify.sql` 只执行：

- SQL Server 版本和兼容级别查询。
- 表、字段、索引和外键元数据查询。
- 重复键和孤儿记录数量统计。
- Stage 编号与 Stage 11 状态统计。
- 表行数统计。

脚本不包含以下语句：

- `CREATE`
- `ALTER`
- `DROP`
- `TRUNCATE`
- `INSERT`
- `UPDATE`
- `DELETE`

脚本会拒绝在 `master`、`model`、`msdb`、`tempdb` 上执行。

### Verify.sql 已知问题（已修复）

2026-09-12 前，`Verify.sql` 的必需表 `VALUES` 列表里残留了一行重复内容，且上一行行尾缺少逗号（`(N'Works')` 后直接接 `(N'FightTemplate')`）。这属于 `Msg 102` 级语法错误，会导致脚本**整体无法执行**，因此在此之前不存在任何一次真正跑完的全量验证结果。该问题已修复，并已在正式库完整执行通过。

## 执行方法

1. 打开 SQL Server Management Studio。
2. 连接目标 SQL Server。
3. 在数据库下拉框中选择要检查的 ManhuaPipeline 数据库。
4. 打开 `Verify.sql`。
5. 整段执行。
6. 保存所有结果集，用于升级前后对比。

不要在查询窗口中粘贴数据库密码、API Key 或连接字符串。

## 结果解释

### 必需表

- 全部应为 `OK`。
- 任一 `MISSING` 都属于新环境 P0。

### 代码必需字段

- 正常结果应为空。
- 返回任何记录表示对应字段缺失。

### 索引与外键

- `MISSING` 不一定阻塞当前运行，但表示目标基线尚未完整。
- `DELETE_RULE_MISMATCH` 表示外键已经存在，但删除规则与目标基线不同；历史升级脚本只报告，不自动重建。
- 历史库不能在未检查重复键和孤儿数据前直接增加约束。

### 重复键

- `IssueCount` 应为 0。
- 非 0 时不能直接建立对应唯一索引。

### 孤儿数据

- `IssueCount` 应为 0。
- 非 0 时需要人工决定删除、归档或保留，脚本不会自动处理。

### Stage 11

固定顺序是：

```text
1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 11 → 9 → 10
```

Stage 11 是可选阶段。记录数为 0 或内容为空均不代表数据库异常，也不能阻塞 Stage 9。

## 当前数据库的已知基准结果

2026-09-12 的只读盘点结果（`Export_Schema.sql` + `Verify.sql`）：

- 正式库有 34 张业务表，另有 4 张 `bak_*` 历史备份表（不计入基线）。
- 34 张必需表全部存在；补齐 `dbo.Projects.LibraryCategory`、`dbo.ReferenceAssets.SourceKey` 后，必需字段检查结果集为空。
- 索引检查全部通过。
- 外键检查仅 `FK_VideoTasks_Prompts`（`VideoGenerationTasks.PromptId → SeedancePrompts.PromptId`）为 `MISSING`，原因是 227 条孤儿记录，见上文。
- 其余重复键与孤儿检查均为 0；Stage 11 只有 1 条历史记录，符合可选阶段规则。
- 当前 SQL Server 是 13.0，数据库兼容级别为 130。

2026-08-17 的旧结论（22 张必需表）已被本轮刷新覆盖。

2026-09-16 追加：新增 L2 连续性层表 `ProjectContinuityTables`（9 列 + 2 索引），业务表总数 34 → **35**。已同步进 `Baseline.sql` / `Upgrade_Current.sql` / `Verify.sql`。正式库补执行 `ManhuaPipeline/Database/Upgrade_连续性表.sql` 后恢复启动；该版本三个脚本同样**只做过 `PARSEONLY`，空库与还原副本实测仍待补**。

2026-09-16 追加（第二批，同日晚）：新增 L3 关键帧层表 `ProjectKeyframes`（19 列 + 1 索引），业务表总数 35 → **36**。已同步进 `Baseline.sql` / `Upgrade_Current.sql` / `Verify.sql`（必需表 / 必需字段 / 目标索引三处）。权威脚本为 `ManhuaPipeline/Database/Upgrade_关键帧层.sql`，**需在正式库手工执行一次，否则 `DatabaseSchemaValidator` 会拒绝启动**。该表初始为空，由项目页「关键帧」页的人工触发按钮写入，基线不种数据；未生成关键帧的项目，阶段 9 提示词不受任何影响。

## 后续顺序

1. 将当前历史库备份还原为独立测试副本。
2. 在副本执行升级前 `Verify.sql` 并保存结果。
3. 在副本执行 `Upgrade_Current.sql`。
4. 再执行 `Verify.sql` 并对比业务表行数。
5. 人工确认孤儿视频任务和缺少 `DramaId` 的历史项目如何处理。
6. 用户确认验证结果后，再制定正式库执行和回滚窗口。

## Baseline.sql 实测结果

> 下表是 **2026-08-17**（22 张表的旧版本）的实测记录，已不代表当前脚本。
> 当前版本（36 张表）只做过 `PARSEONLY`，空库实测待补。

测试数据库：`ManhuaPipeline_BaselineTest_20260817_Codex`（测试后已删除）。

| 检查项 | 结果 |
|---|---:|
| 创建业务表 | 22 |
| 必需表缺失 | 0 |
| 必需字段缺失 | 0 |
| 目标索引缺失 | 0 |
| 目标外键缺失 | 0 |
| 重复键问题 | 0 |
| 孤儿数据 | 0 |
| 非法 Stage 编号 | 0 |
| 默认视频风格 | 6 条，其中 1 条默认 |
| Token 配额初始记录 | 1 |

应用层已验证：

- 应用使用临时连接串正常启动。
- 用户注册成功。
- 漫剧创建成功。
- 项目创建成功。
- Stage 顺序为 `1–8 → 11 → 9 → 10`。
- 新项目 Stage 11 和特效资产为空时接口正常。
- 项目删除和漫剧删除成功。

测试应用进程已停止，临时数据库已删除；正式 `ManhuaPipeline` 数据库仍然存在且未被基线修改。
