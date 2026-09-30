-- =============================================================
-- 剧本独立成表（ScriptDrafts）
--
-- 立项先拆出去了（ProjectBriefs），剧本是第二个。
-- 同一个理由：模块要独立，就要有自己的表——加字段、删字段、加索引都在自己那一块里做，
-- 改动不会波及别的模块，也不会出现「五个模块共用一张表，给它加一列于是五家都多一列」。
--
-- 代价要记下来：SkillOutputImporter 顶部原本写着「拆表的依据是结构差异，不是模块数量」，
-- 五个文本模块结构一样时拆五张表，会得到五份表定义和五套 CRUD，公共能力（导出、搜索、
-- 按版本回看）要改五处。现在按「模块独立优先」拆，代价认了——后续加公共能力时
-- 用视图或基类收口，别在五处复制粘贴。
--
-- Version：同一项目同一集的第几版。重跑不冲掉上一版，旧版自然留在表里可回看。
-- 交付物表那条线继续保留：它是阶段编排的通用兜底，用户新加的阶段也得有地方落。
-- =============================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ScriptDrafts')
BEGIN
    CREATE TABLE ScriptDrafts(
        DraftId      INT IDENTITY(1,1) PRIMARY KEY,

        ProjectId    INT            NOT NULL,
        EpisodeId    INT            NULL,       -- 整部剧本为 NULL，分集剧本填集
        RunId        INT            NULL,       -- 哪次运行产出的（手写的没有）
        StepId       INT            NULL,

        Version      INT            NOT NULL DEFAULT 1,
        Status       NVARCHAR(16)   NOT NULL DEFAULT 'draft',   -- draft / locked

        Title        NVARCHAR(200)  NULL,
        Premise      NVARCHAR(MAX)  NULL,       -- 一句话前提
        Content      NVARCHAR(MAX)  NULL,       -- 剧本正本（md 全文）
        PayloadJson  NVARCHAR(MAX)  NULL,       -- 结构化块（五阶门控 / 分集表，有的话）

        CreatedAt    DATETIME2      NOT NULL DEFAULT SYSDATETIME()
    );

    CREATE INDEX IX_ScriptDrafts_Project ON ScriptDrafts(ProjectId, EpisodeId, DraftId DESC);
END
GO

-- 老剧本回填：P1 跑出来的正本还在交付物表里，先搬过来，
-- 免得切表那天剧本页一片空白。Content 就是当时的产出全文。
IF NOT EXISTS (SELECT 1 FROM ScriptDrafts)
BEGIN
    INSERT INTO ScriptDrafts(ProjectId, EpisodeId, RunId, StepId, Version, Status, Title, Content, PayloadJson)
    SELECT d.ProjectId, d.EpisodeId, d.RunId, d.StepId, ISNULL(d.Version, 1), 'draft', d.Title, d.Content, d.PayloadJson
    FROM DirectorSkillDeliverables d
    WHERE d.StageKey = 'P1' AND d.ProjectId IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM ScriptDrafts s
                      WHERE s.ProjectId = d.ProjectId AND s.RunId = d.RunId AND s.StepId = d.StepId);
END
GO

SELECT DraftId, ProjectId, EpisodeId, Version, Title, LEN(Content) AS CLen FROM ScriptDrafts;
GO
