-- =============================================================
-- 导演 Skill 包（把外部 Agent Skill 的规则库搬进系统，做成可编辑的数据）
--
-- 为什么建这两张表：
--   外部 skill（如 short-drama-director）本质是「给 LLM 看的规则 md」，跑完产出也是 md，
--   规则散落在磁盘上、跟系统不通气。我们想做到"改 SKILL 就改功能"，前提是：
--   规则必须是库里的数据，能在线改、能按阶段取用、能换版本。
--   业务规则一旦不再写进 C# 代码，引擎就能保持稳定 —— 引擎只负责跑，不管跑什么。
--
-- 两张表的关系：
--   DirectorSkillPacks  包（short-drama-director v6.8 这种）
--   DirectorSkillDocs   包里的每一份规则文档 + 它的"加载范围"（决定什么时候喂给 LLM）
--
-- Scope / ScopeValue 决定这一份在什么时候进 prompt：
--   always       每次都加载（核心路由与质检，控制在 4~6 份，别撑爆上下文）
--   stage:P2     只在跑到某个阶段时加载
--   when:战斗    只在满足条件时加载（跟 skill 自己 S0~S4 / A0~A3 / FACS 按需触发一个道理）
--   ref          备查，默认不加载（页面能看，不进 prompt）
-- Scope 在页面上可以直接改 —— 这就是"调规则不调代码"的入口。
-- =============================================================

IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='DirectorSkillPacks' AND xtype='U')
BEGIN
    CREATE TABLE DirectorSkillPacks (
        PackId      INT           IDENTITY PRIMARY KEY,
        PackKey     NVARCHAR(64)  NOT NULL,                  -- 技术标识，如 short-drama-director
        Name        NVARCHAR(100) NOT NULL,                  -- 展示名
        Version     NVARCHAR(20)  NOT NULL,                  -- 包版本，如 V6.8
        SourcePath  NVARCHAR(400) NULL,                      -- 来源目录，便于重新导入覆盖
        Description NVARCHAR(MAX) NULL,
        IsActive    BIT           NOT NULL CONSTRAINT DF_DSP_Active DEFAULT 1,
        CreatedAt   DATETIME2     NOT NULL CONSTRAINT DF_DSP_Created DEFAULT SYSDATETIME(),
        UpdatedAt   DATETIME2     NOT NULL CONSTRAINT DF_DSP_Updated DEFAULT SYSDATETIME()
    );
    CREATE UNIQUE INDEX UX_DirectorSkillPacks_Key ON DirectorSkillPacks(PackKey);
END

IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='DirectorSkillDocs' AND xtype='U')
BEGIN
    CREATE TABLE DirectorSkillDocs (
        DocId       INT           IDENTITY PRIMARY KEY,
        PackId      INT           NOT NULL,                  -- 所属包
        FileName    NVARCHAR(200) NOT NULL,                  -- 原始文件名，如 asset-first-pipeline.md
        Title       NVARCHAR(200) NOT NULL,                  -- 中文名，取自 md 一级标题
        Scope       NVARCHAR(32)  NOT NULL CONSTRAINT DF_DSD_Scope DEFAULT 'ref',  -- always / stage / when / ref
        ScopeValue  NVARCHAR(100) NULL,                      -- stage 时写 P2；when 时写 战斗 / FACS
        IsCore      BIT           NOT NULL CONSTRAINT DF_DSD_Core DEFAULT 0,       -- 原包标记 ★权威文件
        Chars       INT           NOT NULL CONSTRAINT DF_DSD_Chars DEFAULT 0,      -- 正文字数，评估 prompt 开销
        SortOrder   INT           NOT NULL CONSTRAINT DF_DSD_Sort DEFAULT 0,
        Content     NVARCHAR(MAX) NOT NULL,
        CreatedAt   DATETIME2     NOT NULL CONSTRAINT DF_DSD_Created DEFAULT SYSDATETIME(),
        UpdatedAt   DATETIME2     NOT NULL CONSTRAINT DF_DSD_Updated DEFAULT SYSDATETIME()
    );
    CREATE INDEX IX_DirectorSkillDocs_Pack ON DirectorSkillDocs(PackId, SortOrder);
    CREATE INDEX IX_DirectorSkillDocs_Scope ON DirectorSkillDocs(PackId, Scope);
END
