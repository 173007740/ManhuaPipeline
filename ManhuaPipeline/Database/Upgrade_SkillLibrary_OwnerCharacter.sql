SET NOCOUNT ON;
SET XACT_ABORT ON;

-- 技能库增加归属角色：用于 Stage 5/9 只允许出场角色使用自己的技能。
IF OBJECT_ID(N'dbo.SkillLibrary', N'U') IS NULL
BEGIN
    THROW 51100, N'缺少 SkillLibrary 表，请先执行 Setup.sql。', 1;
END;

IF COL_LENGTH(N'dbo.SkillLibrary', N'OwnerCharacter') IS NULL
    ALTER TABLE dbo.SkillLibrary ADD OwnerCharacter NVARCHAR(100) NULL;

-- 项目30存量技能按角色资产与技能元素补齐归属：
-- 火/冰/雷/剑阵/风/暗/圣 = 七宗宗主（七曜诛圣阵）；体 = 岳沉罡；火墙 = 赤焰宗守山弟子。
UPDATE s
SET s.OwnerCharacter = CASE
    WHEN s.Name = N'火墙' THEN N'赤焰宗守山弟子'
    WHEN s.Tags LIKE N'%赤焰宗%' THEN N'赤焰宗守山弟子'
    WHEN s.Name IN (N'踏天步', N'擒龙手', N'碎金劲', N'赤金气血', N'舍身一拳', N'镇岳崩', N'暗金山岳虚影', N'流星坠地', N'拳风') THEN N'岳沉罡'
    WHEN s.Element = N'体' THEN N'岳沉罡'
    WHEN s.Element IN (N'火', N'冰', N'雷', N'剑阵', N'风', N'暗', N'圣') THEN N'七宗宗主'
    ELSE s.OwnerCharacter
END
FROM dbo.SkillLibrary s
WHERE s.ProjectId = 30 AND s.UserId = 1 AND s.OwnerCharacter IS NULL;

PRINT N'SkillLibrary.OwnerCharacter 升级完成。';
