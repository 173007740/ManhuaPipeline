-- ============================================================
-- 音色库（VoiceLibraryItems）
-- 用途：把常用的参考音频存成「音色」，跨项目复用。
--       在项目的「音色参考」里可以直接从音色库选一个绑定给角色，
--       不用每个项目都重新上传一遍音频。
-- 归属：按 UserId 隔离；同一用户下音色名唯一。
-- 幂等：可重复执行。
-- ============================================================
IF OBJECT_ID(N'dbo.VoiceLibraryItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.VoiceLibraryItems(
        Id               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_VoiceLibraryItems PRIMARY KEY,
        UserId           INT           NOT NULL,
        Name             NVARCHAR(100) NOT NULL,   -- 音色名，如「陈默-沉稳男声」
        Category         NVARCHAR(50)  NULL,       -- 大类：动漫 / 游戏 / 写实 / 仙侠（与资产库类型同一套）
        Tag              NVARCHAR(100) NULL,       -- 分类标签，如 男声 / 女声 / 少年 / 旁白
        Note             NVARCHAR(500) NULL,       -- 备注：适合什么角色、什么情绪
        AudioUrl         NVARCHAR(1000) NOT NULL,  -- /uploads/voices/xxx.mp3
        OriginalFileName NVARCHAR(400) NULL,
        DurationSec      INT           NULL,
        CreatedAt        DATETIME2(0)  NOT NULL CONSTRAINT DF_VoiceLibraryItems_CreatedAt DEFAULT(SYSDATETIME()),
        UpdatedAt        DATETIME2(0)  NOT NULL CONSTRAINT DF_VoiceLibraryItems_UpdatedAt DEFAULT(SYSDATETIME())
    );

    CREATE UNIQUE INDEX UX_VoiceLibraryItems_User_Name
        ON dbo.VoiceLibraryItems(UserId, Name);

    CREATE INDEX IX_VoiceLibraryItems_User
        ON dbo.VoiceLibraryItems(UserId);
END
GO
