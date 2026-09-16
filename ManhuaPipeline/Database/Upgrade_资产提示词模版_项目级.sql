/* ============================================================================
   Upgrade_资产提示词模版_项目级.sql
   目标：资产提示词模版从「账号级一份」升级为「账号级默认 + 每部剧可覆盖」。

   背景：原来 AssetPromptTemplates 唯一键是 (UserId, Category)，四类模版全账号共用，
         出厂默认里写死了某部剧的风格，导致「换个剧还是同一套风格」。

   1) 新增 ProjectId：0 = 账号级默认模版（所有剧共用）；>0 = 该剧专属覆盖。
   2) 唯一索引由 (UserId, Category) 改为 (UserId, ProjectId, Category)。
      已有数据 ProjectId 默认 0，自动成为账号级默认模版，向后兼容。

   生效顺序（代码里实现）：本剧模版 → 账号级默认 → 出厂默认。

   幂等：可重复执行。
   ============================================================================ */
SET NOCOUNT ON;
GO

IF COL_LENGTH(N'dbo.AssetPromptTemplates', N'ProjectId') IS NULL
BEGIN
    ALTER TABLE dbo.AssetPromptTemplates
        ADD ProjectId INT NOT NULL CONSTRAINT DF_AssetPromptTemplates_ProjectId DEFAULT(0);
    PRINT N'已添加列 ProjectId（0 = 账号级默认模版，>0 = 剧级覆盖）';
END
ELSE
    PRINT N'列 ProjectId 已存在，跳过';
GO

-- 旧唯一索引 (UserId, Category) 会挡住「同一用户同一分类存剧级覆盖」，必须换掉
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'UX_AssetPromptTemplates_User_Category'
             AND object_id = OBJECT_ID(N'dbo.AssetPromptTemplates'))
BEGIN
    DROP INDEX UX_AssetPromptTemplates_User_Category ON dbo.AssetPromptTemplates;
    PRINT N'已删除旧唯一索引 UX_AssetPromptTemplates_User_Category';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_AssetPromptTemplates_User_Project_Category'
                 AND object_id = OBJECT_ID(N'dbo.AssetPromptTemplates'))
BEGIN
    CREATE UNIQUE INDEX UX_AssetPromptTemplates_User_Project_Category
        ON dbo.AssetPromptTemplates(UserId, ProjectId, Category);
    PRINT N'已创建唯一索引 UX_AssetPromptTemplates_User_Project_Category';
END
ELSE
    PRINT N'唯一索引 UX_AssetPromptTemplates_User_Project_Category 已存在，跳过';
GO

PRINT N'=== 结果核对：模版表 ===';
SELECT TemplateId, UserId, ProjectId, Category, Enabled, UpdatedAt
FROM dbo.AssetPromptTemplates
ORDER BY UserId, ProjectId, Category;
GO
