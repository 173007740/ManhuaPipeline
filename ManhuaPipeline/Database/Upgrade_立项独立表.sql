-- =============================================================
-- 【已作废】ProjectBriefs 已经并进 Dramas，见 Upgrade_立项并入漫剧表.sql。
-- 这份只留作历史记录，别再跑——跑一次就把表建回来，而代码已经不认它了。
--
-- 作废原因（原思路）：立项独立成表（ProjectBriefs）
--
-- 为什么不再走交付物表：立项是「设定」，不是「产出」。
-- 剧本 / 图册 / 质检是一次跑完的成品，躺着给人看就行；
-- 立项里的画幅、平台、集数、提示词引擎是下游每一步都要读的参数——
-- P4 生成投喂提示词时要知道走 SD 还是 H3，P3 分镜要按单集时长切镜。
-- 以前它们被压成一段 3612 字的 Markdown 塞进 DirectorSkillDeliverables.Content，
-- 下游只能让模型自己从文本里「读」出来，读错一次后面全错；
-- 想改个画幅也没有一行能 UPDATE，只能改表单整条重跑。
--
-- 为什么要独立一张表而不是通用键值表（RunId+StageKey+FieldKey）：
-- 模块要独立，就要能加字段、删字段、加约束。键值表里「加字段」是插一行数据，
-- 没有类型、没有默认值、没有非空约束，画幅和集数都是 nvarchar，索引也建不到值上。
--
-- 为什么不绑 RunId：立项的生命周期不属于某一次运行。
-- 导演流水线一次只能做一集，立项却是一部剧共用的——
-- 一份立项，12 集各起一次 run，P0 从「生成一段文本」变成「引用这份已锁定的立项」。
--
-- (ProjectId, EpisodeId) 唯一：一个项目只有一份项目级立项（EpisodeId 为 NULL），
-- 另允许每集一份集级覆盖。SQL Server 唯一索引里 NULL 视为相等，正好 enforces 这一点。
-- =============================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProjectBriefs')
BEGIN
    CREATE TABLE ProjectBriefs(
        BriefId         INT IDENTITY(1,1) PRIMARY KEY,

        ProjectId       INT            NOT NULL,   -- 属于哪个项目
        EpisodeId       INT            NULL,       -- 集级立项才填；项目级留 NULL
        Status          NVARCHAR(16)   NOT NULL DEFAULT 'draft',   -- draft 草稿 / locked 已锁定

        -- ↓ 立项表单上的字段，每一列都能 ALTER，加删随业务走
        Aspect          NVARCHAR(16)   NULL,       -- 16:9 横屏 / 9:16 竖屏 / 21:9 超宽
        Delivery        NVARCHAR(64)   NULL,       -- 交付形态
        Genre           NVARCHAR(128)  NULL,       -- 题材
        ArtStyleId      INT            NULL,       -- 画风（指向图片风格库）
        Hook            NVARCHAR(MAX)  NULL,       -- 核心爽点
        Premise         NVARCHAR(MAX)  NULL,       -- 核心设定
        Platform        NVARCHAR(64)   NULL,       -- 目标平台
        EpisodeCount    INT            NULL,       -- 总集数
        EpisodeDuration INT            NULL,       -- 单集时长（秒）
        CharactersJson  NVARCHAR(MAX)  NULL,       -- 角色清单（JSON 数组）

        -- 提示词引擎：P4 生成投喂提示词时直接读这一列决定走 SD 还是 H3。
        -- 以前它存在 DirectorSkillRuns.InputsJson 里，P4 靠正则从 JSON 里抠；
        -- 现在它是立项的一个字段，改这一列就是换引擎，不用重开一次运行。
        PromptEngine    NVARCHAR(32)   NULL,       -- SD / H3

        CreatedAt       DATETIME2      NOT NULL DEFAULT SYSDATETIME(),
        UpdatedAt       DATETIME2      NOT NULL DEFAULT SYSDATETIME()
    );

    CREATE UNIQUE INDEX UX_PB_Project_Episode ON ProjectBriefs(ProjectId, EpisodeId);
END
GO

-- 老数据兜底：跑过的立项文本还在交付物表里，先按项目回填一条骨架，
-- 省得切过来那一天立项是空的。字段值留给用户在页面上补——
-- 从 3612 字 Markdown 里反解出画幅、集数，既不可靠也没必要。
IF NOT EXISTS (SELECT 1 FROM ProjectBriefs)
BEGIN
    INSERT INTO ProjectBriefs(ProjectId, EpisodeId, Status)
    SELECT DISTINCT d.ProjectId, NULL, 'draft'
    FROM DirectorSkillDeliverables d
    WHERE d.StageKey = 'P0' AND d.ProjectId IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM ProjectBriefs b WHERE b.ProjectId = d.ProjectId AND b.EpisodeId IS NULL);
END
GO
