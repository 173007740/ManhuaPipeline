-- =============================================================
-- 流水线「跑起来」需要的两张表
--
-- DirectorSkillStages 只定义了「怎么跑」，真正跑一次还得留下痕迹：
--   DirectorSkillRuns   一次运行（对应一部剧/一集）
--   DirectorSkillRunSteps  这一次的每一步：喂了什么、用了哪版规则、产出了什么、卡在哪
--
-- 设计上刻意保留了两处「可审计」字段，因为规则是会改的：
--   Steps.DocsSummary  本次实际拼进 prompt 的文档清单（id + 标题 + 字数）
--   Steps.OutputText   LLM 原始返回，不做任何加工
-- 理由：三个月后回看一份产出，必须能说清它是按哪一版规则、哪一份输入跑出来的，
-- 否则规则一改，历史产出就变成没根的东西，出了问题也复现不了。
-- =============================================================

IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='DirectorSkillRuns' AND xtype='U')
BEGIN
    CREATE TABLE DirectorSkillRuns (
        RunId         INT           IDENTITY PRIMARY KEY,
        PackId        INT           NOT NULL,
        ProjectId     INT           NULL,
        Title         NVARCHAR(200) NULL,
        InputsJson    NVARCHAR(MAX) NULL,      -- P0 立项时锁定的前置参数
        CurrentStage  NVARCHAR(32)  NULL,
        Status        NVARCHAR(20)  NOT NULL CONSTRAINT DF_DSR_Status DEFAULT 'running',  -- running/await_confirm/done/error
        CreatedAt     DATETIME2     NOT NULL CONSTRAINT DF_DSR_Created DEFAULT SYSDATETIME(),
        UpdatedAt     DATETIME2     NOT NULL CONSTRAINT DF_DSR_Updated DEFAULT SYSDATETIME()
    );
    CREATE INDEX IX_DirectorSkillRuns_Pack ON DirectorSkillRuns(PackId, RunId);
END

IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='DirectorSkillRunSteps' AND xtype='U')
BEGIN
    CREATE TABLE DirectorSkillRunSteps (
        StepId        INT           IDENTITY PRIMARY KEY,
        RunId         INT           NOT NULL,
        StageKey      NVARCHAR(32)  NOT NULL,
        Name          NVARCHAR(100) NULL,
        SortOrder     INT           NOT NULL,
        InputText     NVARCHAR(MAX) NULL,      -- 本步喂进去的用户输入
        DocsSummary   NVARCHAR(MAX) NULL,      -- 本次实际加载了哪些规则（审计用）
        PromptChars   INT           NULL,      -- 拼出来的 prompt 总字符数
        OutputText    NVARCHAR(MAX) NULL,      -- LLM 原始返回
        OutputJson    NVARCHAR(MAX) NULL,      -- 解析后的结构化结果（解析失败则为 NULL）
        Gates         NVARCHAR(MAX) NULL,      -- 本步门禁（放行前给人看）
        Status        NVARCHAR(20)  NOT NULL CONSTRAINT DF_DSRS_Status DEFAULT 'pending', -- pending/done/await_confirm/error
        Error         NVARCHAR(MAX) NULL,
        ConfirmedAt   DATETIME2     NULL,
        CreatedAt     DATETIME2     NOT NULL CONSTRAINT DF_DSRS_Created DEFAULT SYSDATETIME(),
        UpdatedAt     DATETIME2     NOT NULL CONSTRAINT DF_DSRS_Updated DEFAULT SYSDATETIME()
    );
    CREATE INDEX IX_DirectorSkillRunSteps_Run ON DirectorSkillRunSteps(RunId, SortOrder);
END
