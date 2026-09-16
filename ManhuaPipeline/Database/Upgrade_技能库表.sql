-- 技能库表（技能大招提示词库）+ 项目关联 + 系别维护表
IF OBJECT_ID(N'dbo.SkillLibrary', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[SkillLibrary] (
        [SkillId]       INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]        INT           NOT NULL REFERENCES [Users]([UserId]) ON DELETE CASCADE,
        [ProjectId]     INT           NULL,
        [Name]          NVARCHAR(100) NOT NULL,
        [Element]       NVARCHAR(20) NULL,
        [Tier]          INT NULL DEFAULT 4,
        [PromptImage]   NVARCHAR(MAX) NULL,
        [PromptVideo]   NVARCHAR(MAX) NULL,
        [ImageUrl]      NVARCHAR(500) NULL,
        [Tags]          NVARCHAR(500) NULL,
        [CreatedAt]     DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_SkillLibrary_User] ON [dbo].[SkillLibrary]([UserId]);
    CREATE INDEX [IX_SkillLibrary_Project] ON [dbo].[SkillLibrary]([ProjectId]);
    PRINT 'SkillLibrary table created';
END
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.SkillLibrary', N'ProjectId') IS NULL
        ALTER TABLE [dbo].[SkillLibrary] ADD [ProjectId] INT NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SkillLibrary') AND name=N'IX_SkillLibrary_Project')
        CREATE INDEX [IX_SkillLibrary_Project] ON [dbo].[SkillLibrary]([ProjectId]);
    PRINT 'SkillLibrary already exists';
END
GO

IF OBJECT_ID(N'dbo.SkillElements', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[SkillElements] (
        [ElementId] INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]    INT NOT NULL REFERENCES [Users]([UserId]) ON DELETE CASCADE,
        [Name]      NVARCHAR(50) NOT NULL,
        [SortOrder] INT NOT NULL DEFAULT 0,
        [CreatedAt] DATETIME2 NOT NULL DEFAULT GETDATE()
    );
    CREATE UNIQUE INDEX [UX_SkillElements_User_Name] ON [dbo].[SkillElements]([UserId], [Name]);
    PRINT 'SkillElements table created';
END
ELSE
    PRINT 'SkillElements already exists';
GO

-- 给所有缺失默认系别的用户补齐火/冰/雷/剑阵/风/暗/圣
INSERT INTO [dbo].[SkillElements]([UserId], [Name], [SortOrder])
SELECT u.[UserId], x.[Name], x.[SortOrder]
FROM [dbo].[Users] u
CROSS APPLY (VALUES (N'火',1),(N'冰',2),(N'雷',3),(N'剑阵',4),(N'风',5),(N'暗',6),(N'圣',7)) x([Name], [SortOrder])
WHERE NOT EXISTS (SELECT 1 FROM [dbo].[SkillElements] se WHERE se.[UserId]=u.[UserId] AND se.[Name]=x.[Name]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name=N'FK_SkillLibrary_Projects')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM [dbo].[SkillLibrary] s LEFT JOIN [dbo].[Projects] p ON p.[ProjectId]=s.[ProjectId] WHERE s.[ProjectId] IS NOT NULL AND p.[ProjectId] IS NULL)
        ALTER TABLE [dbo].[SkillLibrary] WITH CHECK ADD CONSTRAINT [FK_SkillLibrary_Projects] FOREIGN KEY ([ProjectId]) REFERENCES [dbo].[Projects]([ProjectId]);
END
GO
