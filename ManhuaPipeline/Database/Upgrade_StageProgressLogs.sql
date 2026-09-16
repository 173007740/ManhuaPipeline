IF OBJECT_ID(N'dbo.StageProgressLogs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StageProgressLogs (
        ProgressLogId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ProjectId INT NOT NULL,
        StageNumber INT NOT NULL,
        LogText NVARCHAR(1000) NOT NULL,
        CreatedAt DATETIME NOT NULL DEFAULT GETDATE()
    );
END
GO
