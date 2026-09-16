-- 打斗模板库表（打斗动作提示词库）
IF OBJECT_ID(N'dbo.FightTemplate', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[FightTemplate] (
        [FightTemplateId]   INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]            INT           NOT NULL REFERENCES [Users]([UserId]) ON DELETE CASCADE,
        [Name]              NVARCHAR(100) NOT NULL,
        [Tier]              INT           NULL DEFAULT 3,
        [Duration]          INT           NULL DEFAULT 11,
        [Scene]             NVARCHAR(500) NULL,
        [Beat]              NVARCHAR(MAX) NULL,
        [ActionPrompt]      NVARCHAR(MAX) NULL,
        [CameraPrompt]      NVARCHAR(MAX) NULL,
        [ConstraintPrompt]  NVARCHAR(MAX) NULL,
        [Tags]              NVARCHAR(500) NULL,
        [CreatedAt]         DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_FightTemplate_User] ON [dbo].[FightTemplate]([UserId]);
    PRINT 'FightTemplate table created';
END
ELSE
    PRINT 'FightTemplate already exists';
