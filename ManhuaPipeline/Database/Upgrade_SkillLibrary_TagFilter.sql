SET NOCOUNT ON;
SET XACT_ABORT ON;

-- 技能库改为按标签过滤：项目标签（如“镇世武圣”）跨剧集/项目取技能，不再依赖 ProjectId。
IF OBJECT_ID(N'dbo.SkillLibrary', N'U') IS NULL
BEGIN
    THROW 51100, N'缺少 SkillLibrary 表，请先执行 Setup.sql。', 1;
END;

IF COL_LENGTH(N'dbo.SkillLibrary', N'Tags') IS NULL
    ALTER TABLE dbo.SkillLibrary ADD Tags NVARCHAR(500) NULL;

-- 项目30存量技能补齐“镇世武圣”标签，保证 Stage 5/9 按项目标签过滤时能取到。
UPDATE s
SET s.Tags = CASE
    WHEN s.Tags IS NULL OR LTRIM(RTRIM(s.Tags)) = N'' THEN N'镇世武圣'
    WHEN s.Tags LIKE N'%镇世武圣%' THEN s.Tags
    ELSE s.Tags + N',镇世武圣'
END
FROM dbo.SkillLibrary s
WHERE s.UserId = 1 AND s.ProjectId = 30;

PRINT N'SkillLibrary.Tags 升级完成。';
