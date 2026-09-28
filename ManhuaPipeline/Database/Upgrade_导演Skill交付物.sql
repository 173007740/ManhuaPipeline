-- =============================================================
-- 交付物表：给「不落到业务表」的那几步一个正式落点
--
-- 为什么需要它：P0 立项 / P1 剧本 / P2b 资产清单 / P2d 资产图册 / P5 质检
-- 这五步的产出是整篇文本，系统里没有对应的表——分镜有 StoryboardFrames、
-- 资产有 CharacterAssets、提示词有 SeedancePrompts，唯独它们无处可去，
-- 只能躺在 DirectorSkillRunSteps.OutputText 里，前台看不到、下游也用不上。
--
-- 为什么不塞进 StageData：那是项目阶段 1~11 的容器，阶段号各有各的语义
-- （1 创意构思 / 2 故事分析 / 10 衔接检查），硬塞等于靠约定猜，改一次乱一次。
-- 独立一张表，产出自带 RunId/StepId/StageKey，能追溯到是哪次跑出来的。
--
-- Version 是同一项目同一阶段的第几次产出：重跑不会冲掉上一版，
-- 旧版自然留在表里可回看——这也是剧本覆盖 Projects.ScriptContent 的底气。
-- =============================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'DirectorSkillDeliverables')
BEGIN
    CREATE TABLE DirectorSkillDeliverables(
        DeliverableId INT IDENTITY(1,1) PRIMARY KEY,
        RunId         INT            NOT NULL,   -- 哪次运行产出的
        StepId        INT            NULL,       -- 对应哪一步
        StageKey      NVARCHAR(20)   NOT NULL,   -- P0 / P1 / P2b / P2d / P5
        ProjectId     INT            NULL,       -- 没绑项目时为 NULL（只留文本不入库）
        EpisodeId     INT            NULL,
        Title         NVARCHAR(200)  NULL,
        Content       NVARCHAR(MAX)  NULL,       -- 正本（md 全文）
        PayloadJson   NVARCHAR(MAX)  NULL,       -- 结构化块（有的话）
        Version       INT            NOT NULL DEFAULT 1,
        CreatedAt     DATETIME2      NOT NULL DEFAULT SYSDATETIME()
    );
    CREATE INDEX IX_DSD_Project_Stage ON DirectorSkillDeliverables(ProjectId, StageKey, DeliverableId DESC);
END
GO

-- 这五步的产出改落交付物表（引擎只认 OutputTarget 这个字符串，加一种形态不用改引擎）
UPDATE DirectorSkillStages SET OutputTarget = N'deliverable'
WHERE StageKey IN (N'P0', N'P1', N'P2b', N'P2d', N'P5');
GO

SELECT StageKey, Name, ISNULL(OutputTarget, N'（不入库）') AS OutputTarget, HumanConfirm
FROM DirectorSkillStages ORDER BY SortOrder;
