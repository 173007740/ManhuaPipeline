-- ============================================================
-- 立项结果落到 Dramas 那一行（可重复执行）
--
-- 立项 = 创建漫剧：立完项按集数生成 N 个集项目（Projects），一集一个。
-- 立项的产出整部漫剧一份，每一集都要看得到它。
--
-- 可产出一直没地方落：P0 跑在漫剧上（那时一个项目都还没建），
-- 入库只认 (ProjectId, EpisodeId)，落点拿不到就整段跳过——
-- 产出原文只剩 DirectorSkillRunSteps.OutputText 这一处。
-- 于是每一集要看立项结果，只能回头翻运行、翻步骤，
-- 还得跨到「哪一集跑了 P0」那条运行史里找：第 1 集跑的，第 2 集打开也要认。
--
-- 立项跟画幅、集数一样是漫剧的属性，就该存在漫剧那一行上：
--   Dramas.P0OutputText  立项产出原文
--   Dramas.P0StepId      它是哪一步跑出来的（要看细则顺着它能找到那次运行）
--   Dramas.P0FinishedAt  什么时候跑完的
-- 之后每一集按 dramaId 一次读出来，不再问它是哪一集跑的。
-- ============================================================

-- 1) 补列。一律可空：没跑过立项的漫剧这三列是 NULL，不是空串。
IF COL_LENGTH(N'dbo.Dramas', N'P0OutputText') IS NULL
    ALTER TABLE dbo.Dramas ADD P0OutputText NVARCHAR(MAX) NULL;

IF COL_LENGTH(N'dbo.Dramas', N'P0StepId') IS NULL
    ALTER TABLE dbo.Dramas ADD P0StepId INT NULL;

IF COL_LENGTH(N'dbo.Dramas', N'P0FinishedAt') IS NULL
    ALTER TABLE dbo.Dramas ADD P0FinishedAt DATETIME2 NULL;
GO

-- 2) 回填老数据：把每部漫剧最近一次 P0 产出搬到漫剧那一行。
--    两种归属都认——立项运行挂 DramaId，早期跑在某一集上的挂 ProjectId。
--    只填还没填过的，跑过多次立项的以最近那次为准。
UPDATE d
SET P0OutputText = s.OutputText,
    P0StepId     = s.StepId,
    P0FinishedAt = s.UpdatedAt,
    UpdatedAt    = SYSDATETIME()
FROM Dramas d
CROSS APPLY (
    SELECT TOP 1 x.StepId, x.OutputText, x.UpdatedAt
    FROM DirectorSkillRunSteps x
    JOIN DirectorSkillRuns r ON r.RunId = x.RunId
    WHERE x.StageKey = N'P0'
      AND x.Status = N'done'
      AND LEN(ISNULL(x.OutputText, '')) > 0
      AND (r.DramaId = d.DramaId
           OR r.ProjectId IN (SELECT p.ProjectId FROM Projects p WHERE p.DramaId = d.DramaId))
    ORDER BY x.StepId DESC
) s
WHERE d.P0OutputText IS NULL;
GO

-- 3) 确认
SELECT DramaId, Title, P0StepId, LEN(P0OutputText) AS P0Len, P0FinishedAt
FROM Dramas
WHERE P0OutputText IS NOT NULL;
GO
