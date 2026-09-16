-- SeedancePrompts: L5 逐镜状态机状态
--
-- 背景：阶段 9 的逐镜清单原先只有「视频生成状态」（Status: pending/processing/completed/failed），
-- 无法表达「这一镜为什么还不能生成」「这一镜是否通过人工验收」。
-- 新增 ShotStatus 表达逐镜状态机（对应架构方案 §5.2 的 L5 新增 A）：
--   ready                   提示词就绪，且本镜引用的所有资产都已有参考图
--   blocked_by_missing_asset 被缺失资产阻塞：本镜在 FrameAssetBindings 里有 HasImage=0 的资产
--   accepted                人工验收通过
--   rejected                人工打回（需重做）
--
-- 说明：
--   1. ShotStatus 与 Status 是两个维度，Status 仍表示视频生成状态，二者不要混用。
--   2. ready / blocked_by_missing_asset 由服务端按当前资产出图情况自动重算（accepted / rejected 是人工结论，不会被自动覆盖）。
--   3. 本列不影响任何现有查询（GetProcessingPrompts 只按 Status 过滤）。
--
-- 代码依赖：Models/SeedancePrompt.cs（ShotStatus）、DbService.UpdatePromptShotStatus / SyncPromptShotStatusFromAssets、
--          PromptController.GET（自动重算）与 PUT {promptId}/shot-status（人工置位）、
--          DatabaseSchemaValidator 的 SeedancePrompts 校验。
-- 未执行本脚本时应用会启动失败并提示缺少 dbo.SeedancePrompts.ShotStatus。

IF COL_LENGTH(N'dbo.SeedancePrompts', N'ShotStatus') IS NULL
BEGIN
    ALTER TABLE dbo.SeedancePrompts ADD [ShotStatus] NVARCHAR(30) NOT NULL CONSTRAINT [DF_SeedancePrompts_ShotStatus] DEFAULT N'ready';
END
GO
