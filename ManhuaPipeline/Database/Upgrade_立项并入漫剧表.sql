-- =============================================================
-- 立项字段并入 Dramas，ProjectBriefs 作废
--
-- 层级错了：立项是「漫剧级」的，不是「项目级」的。
--   漫剧（Dramas）          ← 立项挂这一层：一部漫剧立一次项
--     └ 项目（Projects）    ← 立项完成后长出来的，一部漫剧 N 个项目（≈ 一集一个）
--         └ 剧集（Episodes）← 流水线实际跑的粒度
-- 库里的数据就是这么长的：漫剧 1 下面挂着项目 5/6/7/8（第1~4集），
-- 漫剧 20 下面挂着 42/43/45/.../54（遐蝶篇 01~08）。
-- 把立项绑在 ProjectId 上，等于逼着「第1集」和「第2集」各立一次项——
-- 画幅、题材、角色、集数这些一部剧只该定一次的东西，被复制了 N 份，
-- 改一处还得挨个项目改。
--
-- 为什么不绑 RunId：立项的生命周期不属于某一次运行。
-- 导演流水线一次只跑一集，立项却是一部剧共用的。
--
-- 为什么加列而不是另建一张 DramasBriefs：立项和漫剧是一一对应的，
-- 一部漫剧不会有两份立项。拆成两张表就得靠外键 + 唯一索引去维持这个一对一，
-- 不如直接并进 Dramas——立项本来就是「这部漫剧的设定」。
-- =============================================================

-- 1. 给 Dramas 加立项列。一律可空：没立过项的漫剧这些列全是 NULL，
--    不是空串——「没填」和「填了空的」要能分得开。
IF COL_LENGTH('Dramas', 'Status') IS NULL
BEGIN
    ALTER TABLE Dramas ADD
        Status          NVARCHAR(16)   NULL,       -- draft 草稿 / locked 已锁定
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
        -- 改这一列就是换引擎，不用重开一次运行。
        PromptEngine    NVARCHAR(32)   NULL;       -- SD / H3
END
GO

-- 2. 老数据搬家：项目级立项按 Projects.DramaId 归到所属漫剧。
--    同一部漫剧若有多条（多个项目各存过一份），取最近更新的那条，不互相覆盖。
IF OBJECT_ID('ProjectBriefs') IS NOT NULL
BEGIN
    UPDATE d SET
        d.Status          = b.Status,
        d.Aspect          = b.Aspect,
        d.Delivery        = b.Delivery,
        d.Genre           = b.Genre,
        d.ArtStyleId      = b.ArtStyleId,
        d.Hook            = b.Hook,
        d.Premise         = b.Premise,
        d.Platform        = b.Platform,
        d.EpisodeCount    = b.EpisodeCount,
        d.EpisodeDuration = b.EpisodeDuration,
        d.CharactersJson  = b.CharactersJson,
        d.PromptEngine    = b.PromptEngine,
        d.UpdatedAt       = SYSDATETIME()
    FROM Dramas d
    JOIN (
        -- 同一漫剧可能从多个项目迁来，按 UpdatedAt 取最新的那条
        SELECT p.DramaId, b.Status, b.Aspect, b.Delivery, b.Genre, b.ArtStyleId, b.Hook,
               b.Premise, b.Platform, b.EpisodeCount, b.EpisodeDuration, b.CharactersJson,
               b.PromptEngine,
               ROW_NUMBER() OVER (PARTITION BY p.DramaId ORDER BY b.UpdatedAt DESC, b.BriefId DESC) AS rn
        FROM ProjectBriefs b
        JOIN Projects p ON p.ProjectId = b.ProjectId
        WHERE b.EpisodeId IS NULL          -- 只迁项目级；集级覆盖在新的层级里没有对应位置
    ) b ON b.DramaId = d.DramaId AND b.rn = 1;

    -- 集级立项在新层级里无处安放：立项归漫剧，不再有「某集单独一份设定」。
    -- 有就打出来让人看见，别静默丢掉。
    IF EXISTS (SELECT 1 FROM ProjectBriefs WHERE EpisodeId IS NOT NULL)
        PRINT '警告：存在集级立项（EpisodeId 非 NULL），本次不迁移，请确认是否还需要';

    DROP TABLE ProjectBriefs;
END
GO
