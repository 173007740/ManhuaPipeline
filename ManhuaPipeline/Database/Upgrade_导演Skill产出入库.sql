-- =============================================================
-- 产出自动入库需要的三个字段
--
-- 原则不变：引擎仍然「不懂业务」。它不认识分镜、资产、提示词，
-- 只认 Stages.OutputTarget 这个字符串——哪个阶段往哪张表落，是数据说了算。
-- 真正认识格式的是 SkillOutputImporter（解析器），但「用哪个解析器」由数据选。
-- 所以加一种产出形态 = 加一个解析器 + 在数据里填一个名字，引擎不动。
--
-- DirectorSkillRuns.EpisodeId  分镜挂在剧集下，入库必须知道是哪一集
-- DirectorSkillStages.OutputTarget  本阶段产出落到哪：assets / asset_prompts / frames / prompts，NULL=不入库
-- DirectorSkillRunSteps.ImportedCount / ImportError  入库结果，解析失败也要说清，不能默默丢
-- =============================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('DirectorSkillRuns') AND name='EpisodeId')
    ALTER TABLE DirectorSkillRuns ADD EpisodeId INT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('DirectorSkillStages') AND name='OutputTarget')
    ALTER TABLE DirectorSkillStages ADD OutputTarget NVARCHAR(50) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('DirectorSkillRunSteps') AND name='ImportedCount')
BEGIN
    ALTER TABLE DirectorSkillRunSteps ADD ImportedCount INT NULL;
    ALTER TABLE DirectorSkillRunSteps ADD ImportError NVARCHAR(MAX) NULL;
END

GO

-- 哪个阶段的产出往哪落（改这里就能改落库行为，不用改代码）
UPDATE DirectorSkillStages SET OutputTarget = N'assets'        WHERE StageKey = N'P2a';
UPDATE DirectorSkillStages SET OutputTarget = N'asset_prompts' WHERE StageKey IN (N'P2c1', N'P2c2', N'P2c3');
UPDATE DirectorSkillStages SET OutputTarget = N'frames'        WHERE StageKey = N'P3';
UPDATE DirectorSkillStages SET OutputTarget = N'prompts'       WHERE StageKey = N'P4';

SELECT StageKey, Name, ISNULL(OutputTarget,'（不入库）') AS OutputTarget, HumanConfirm
FROM DirectorSkillStages ORDER BY SortOrder;
