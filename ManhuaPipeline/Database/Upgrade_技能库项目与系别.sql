SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
BEGIN
    THROW 51100, N'请选择 ManhuaPipeline 数据库或其还原副本后再执行。', 1;
END;

IF OBJECT_ID(N'dbo.SkillLibrary', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Projects', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    THROW 51101, N'缺少 SkillLibrary、Projects 或 Users 表，请检查数据库。', 1;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.SkillLibrary', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.SkillLibrary (
            SkillId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SkillLibrary PRIMARY KEY,
            UserId INT NOT NULL,
            ProjectId INT NULL,
            Name NVARCHAR(100) NOT NULL,
            Element NVARCHAR(20) NULL,
            Tier INT NULL CONSTRAINT DF_SkillLibrary_Tier DEFAULT 4,
            PromptImage NVARCHAR(MAX) NULL,
            PromptVideo NVARCHAR(MAX) NULL,
            ImageUrl NVARCHAR(500) NULL,
            Tags NVARCHAR(500) NULL,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_SkillLibrary_CreatedAt DEFAULT GETDATE()
        );
    END
    ELSE
    BEGIN
        IF COL_LENGTH(N'dbo.SkillLibrary', N'ProjectId') IS NULL
            ALTER TABLE dbo.SkillLibrary ADD ProjectId INT NULL;
    END;

    IF OBJECT_ID(N'dbo.SkillElements', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.SkillElements (
            ElementId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SkillElements PRIMARY KEY,
            UserId INT NOT NULL,
            Name NVARCHAR(50) NOT NULL,
            SortOrder INT NOT NULL CONSTRAINT DF_SkillElements_SortOrder DEFAULT 0,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_SkillElements_CreatedAt DEFAULT GETDATE()
        );
    END;

    DECLARE @Sql NVARCHAR(MAX);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SkillLibrary') AND name=N'IX_SkillLibrary_User')
    BEGIN
        SET @Sql = N'CREATE INDEX IX_SkillLibrary_User ON dbo.SkillLibrary(UserId);';
        EXEC sys.sp_executesql @Sql;
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SkillLibrary') AND name=N'IX_SkillLibrary_Project')
    BEGIN
        SET @Sql = N'CREATE INDEX IX_SkillLibrary_Project ON dbo.SkillLibrary(ProjectId);';
        EXEC sys.sp_executesql @Sql;
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SkillElements') AND name=N'UX_SkillElements_User_Name')
    BEGIN
        SET @Sql = N'CREATE UNIQUE INDEX UX_SkillElements_User_Name ON dbo.SkillElements(UserId, Name);';
        EXEC sys.sp_executesql @Sql;
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name=N'FK_SkillElements_Users')
    BEGIN
        SET @Sql = N'ALTER TABLE dbo.SkillElements WITH CHECK ADD CONSTRAINT FK_SkillElements_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId) ON DELETE CASCADE;';
        EXEC sys.sp_executesql @Sql;
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name=N'FK_SkillLibrary_Users')
    BEGIN
        DECLARE @UserOrphans BIGINT;
        SET @Sql = N'SELECT @c=COUNT_BIG(*) FROM dbo.SkillLibrary s LEFT JOIN dbo.Users u ON u.UserId=s.UserId WHERE u.UserId IS NULL;';
        EXEC sys.sp_executesql @Sql, N'@c BIGINT OUTPUT', @c=@UserOrphans OUTPUT;
        IF @UserOrphans = 0
        BEGIN
            SET @Sql = N'ALTER TABLE dbo.SkillLibrary WITH CHECK ADD CONSTRAINT FK_SkillLibrary_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId) ON DELETE CASCADE;';
            EXEC sys.sp_executesql @Sql;
        END;
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name=N'FK_SkillLibrary_Projects')
    BEGIN
        DECLARE @ProjectOrphans BIGINT;
        SET @Sql = N'SELECT @c=COUNT_BIG(*) FROM dbo.SkillLibrary s LEFT JOIN dbo.Projects p ON p.ProjectId=s.ProjectId WHERE s.ProjectId IS NOT NULL AND p.ProjectId IS NULL;';
        EXEC sys.sp_executesql @Sql, N'@c BIGINT OUTPUT', @c=@ProjectOrphans OUTPUT;
        IF @ProjectOrphans = 0
        BEGIN
            SET @Sql = N'ALTER TABLE dbo.SkillLibrary WITH CHECK ADD CONSTRAINT FK_SkillLibrary_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId);';
            EXEC sys.sp_executesql @Sql;
        END;
    END;

    SET @Sql = N'INSERT INTO dbo.SkillElements(UserId, Name, SortOrder) SELECT u.UserId, x.Name, x.SortOrder FROM dbo.Users u CROSS APPLY (VALUES (N''火'',1),(N''冰'',2),(N''雷'',3),(N''剑阵'',4),(N''风'',5),(N''暗'',6),(N''圣'',7)) x(Name, SortOrder) WHERE NOT EXISTS (SELECT 1 FROM dbo.SkillElements se WHERE se.UserId=u.UserId AND se.Name=x.Name);';
    EXEC sys.sp_executesql @Sql;

    COMMIT TRANSACTION;
    PRINT N'技能库项目关联与系别表升级完成。';
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
