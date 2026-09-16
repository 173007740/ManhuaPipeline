-- ============================================================
-- L2 连续性层：六类连续性表（对标 short-drama-agent 第 6-11 节）
-- 目的：把「空间方位 / 角色状态 / 道具状态 / 线索顺序 / 动作因果 / 转场动机」
--       落成可校验的数据字段，而不是留在提示词字符串里靠 LLM 记忆。
-- 生成：阶段 3（全局蓝图）之后抽取；
-- 消费：阶段 4（分集细化）注入，作为「地点 / 起始状态 / 结束状态」的硬约束来源。
-- TableType 取值见 ManhuaPipeline/Models/ContinuityTables.cs：
--   SceneSpace / CharacterContinuity / PropState / ClueReveal / ActionCausality / TransitionMotive
-- 需手工执行一次：ManhuaPipeline/Database/Upgrade_连续性表.sql
-- ============================================================
IF OBJECT_ID(N'dbo.ProjectContinuityTables', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectContinuityTables (
        ContinuityId    INT IDENTITY(1,1) NOT NULL,
        ProjectId       INT NOT NULL,
        EpisodeNumber   INT NOT NULL DEFAULT(0),   -- 0 = 全片/整季级；>0 = 该集级
        TableType       NVARCHAR(40) NOT NULL,
        ContentJson     NVARCHAR(MAX) NOT NULL DEFAULT(N'{}'),
        ContentText     NVARCHAR(MAX) NULL,        -- 注入下游提示词的渲染文本
        Source          NVARCHAR(20) NOT NULL DEFAULT(N'llm'),  -- llm / manual
        CreatedAt       DATETIME2(0) NOT NULL DEFAULT(SYSDATETIME()),
        UpdatedAt       DATETIME2(0) NOT NULL DEFAULT(SYSDATETIME()),
        CONSTRAINT PK_ProjectContinuityTables PRIMARY KEY (ContinuityId)
    );
    CREATE INDEX IX_ProjectContinuityTables_Project ON dbo.ProjectContinuityTables (ProjectId);
    CREATE UNIQUE INDEX UX_ProjectContinuityTables_Project_Type
        ON dbo.ProjectContinuityTables (ProjectId, EpisodeNumber, TableType);
END
GO
