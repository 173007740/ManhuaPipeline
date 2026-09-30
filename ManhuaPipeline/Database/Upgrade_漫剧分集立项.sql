-- ============================================================
-- 漫剧立项 → 分集提纲 → 单集项目
--
-- 立项是漫剧级的：一部漫剧立一次项（画幅 / 集数 / 提示词引擎 …），
-- P0 跑完产出「N 集分集卡点 + Cliffhanger」表，解析出来的分集提纲
-- 存在 Dramas 那一行上，再按它长出 N 个单集项目（一集一个项目）。
--
-- 新增三列：
--   1) Dramas.EpisodeOutlineJson     分集提纲（建单集项目的唯一依据）
--   2) Projects.EpisodeNumber        这条项目是第几集（一集一个项目）
--   3) DirectorSkillRuns.DramaId     立项运行挂在漫剧上——跑 P0 时还没有任何项目
--
-- 幂等：可重复执行。
-- ============================================================

-- 1) 分集提纲：P0 产出解析后的结果，json 数组 [{n,title,outline,cliffhanger}]
IF COL_LENGTH(N'dbo.Dramas', N'EpisodeOutlineJson') IS NULL
    ALTER TABLE dbo.Dramas ADD EpisodeOutlineJson NVARCHAR(MAX) NULL;

-- 2) 集号：排序与「第N集」标签都靠它，标题保持纯集名不掺编号
IF COL_LENGTH(N'dbo.Projects', N'EpisodeNumber') IS NULL
    ALTER TABLE dbo.Projects ADD EpisodeNumber INT NULL;

-- 3) 运行的漫剧归属：立项运行不绑项目（那时还没建），只绑漫剧
IF COL_LENGTH(N'dbo.DirectorSkillRuns', N'DramaId') IS NULL
    ALTER TABLE dbo.DirectorSkillRuns ADD DramaId INT NULL;
GO

-- 同部漫剧下集号不重复：提纲重复生成也不会建出两条「第3集」
-- 筛选索引要求 QUOTED_IDENTIFIER 为 ON，sqlcmd 默认不带，这里显式开
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Projects_Drama_Episode'
                                           AND object_id = OBJECT_ID(N'dbo.Projects'))
    CREATE UNIQUE INDEX UX_Projects_Drama_Episode
        ON dbo.Projects(DramaId, EpisodeNumber)
        WHERE EpisodeNumber IS NOT NULL AND Status <> N'deleted';
GO
