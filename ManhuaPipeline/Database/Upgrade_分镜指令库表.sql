-- 分镜指令库表（景别/表情/运镜/转场/光影/组合 提示词库）
IF OBJECT_ID(N'dbo.ShotDirective', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ShotDirective] (
        [ShotDirectiveId]   INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]            INT           NOT NULL REFERENCES [Users]([UserId]) ON DELETE CASCADE,
        [Name]              NVARCHAR(100) NOT NULL,
        [Category]          NVARCHAR(20)  NOT NULL,
        [Description]       NVARCHAR(MAX) NULL,
        [Tags]              NVARCHAR(500) NULL,
        [CreatedAt]         DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_ShotDirective_User] ON [dbo].[ShotDirective]([UserId]);
    PRINT 'ShotDirective table created';
END
ELSE
    PRINT 'ShotDirective already exists';
