-- ============================================================
-- 角色音色参考音频（MiniMax H3 参考音频 / <Audio n>）
-- 用途：给项目里的角色绑定一段参考音频，生成 H3 视频时作为音色参考，
--       让同一角色在不同镜头里的说话音色保持一致。
-- 幂等：可重复执行。
-- ============================================================
IF OBJECT_ID(N'dbo.VoiceReferences', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.VoiceReferences(
        VoiceId          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_VoiceReferences PRIMARY KEY,
        ProjectId        INT NOT NULL,
        CharacterName    NVARCHAR(200)  NOT NULL,
        AudioUrl         NVARCHAR(1000) NOT NULL,
        OriginalFileName NVARCHAR(400)  NULL,
        CreatedAt        DATETIME2(0)   NOT NULL CONSTRAINT DF_VoiceReferences_CreatedAt DEFAULT(SYSDATETIME())
    );

    CREATE UNIQUE INDEX UX_VoiceReferences_Project_Character
        ON dbo.VoiceReferences(ProjectId, CharacterName);

    CREATE INDEX IX_VoiceReferences_Project
        ON dbo.VoiceReferences(ProjectId);
END
GO
