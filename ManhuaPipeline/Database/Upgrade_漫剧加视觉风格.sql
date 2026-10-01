-- 漫剧立项加「视觉风格」（VideoStyles）。
--
-- 以前立项上只有 ArtStyleId（图片风格）：它定的是画面长什么样，资产出图时拼进提示词。
-- 而「视觉风格」（VideoStyles，整部剧的影像调性：米哈游二次元 / 苔岬映画体 / 都市短剧…）在漫剧层没有地方存，
-- 只能逐个项目去设 Projects.StyleId —— 一部剧十几个项目各设各的，改一次要改十几个。
-- 建漫剧弹窗要加这个下拉，就得有地方落，所以加这一列（漫剧级，一部剧定一次）。

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Dramas' AND COLUMN_NAME='VideoStyleId')
    EXEC('ALTER TABLE Dramas ADD VideoStyleId INT NULL');

SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Dramas' AND COLUMN_NAME='VideoStyleId';

-- 参考：把已有漫剧的视觉风格按「第一个项目的画风」回填一次（可选，按需放开）
-- UPDATE d SET d.VideoStyleId = p.StyleId
-- FROM Dramas d CROSS APPLY (
--     SELECT TOP 1 StyleId FROM Projects WHERE DramaId = d.DramaId AND StyleId IS NOT NULL
-- ) p
-- WHERE d.VideoStyleId IS NULL;
