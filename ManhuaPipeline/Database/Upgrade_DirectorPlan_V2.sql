-- =============================================
-- DirectorPlans V2：分镜校验闭环结果
-- NeedsReview / ValidationScore / ViolationsJson / RepairCount / LastValidationAt
-- 幂等，可重复执行；不改动旧列。
-- =============================================
IF COL_LENGTH(N'dbo.DirectorPlans', N'NeedsReview') IS NULL
BEGIN
    ALTER TABLE dbo.DirectorPlans ADD NeedsReview BIT NOT NULL
        CONSTRAINT DF_DirectorPlans_NeedsReview DEFAULT 0;
END
GO

IF COL_LENGTH(N'dbo.DirectorPlans', N'ValidationScore') IS NULL
BEGIN
    ALTER TABLE dbo.DirectorPlans ADD ValidationScore INT NOT NULL
        CONSTRAINT DF_DirectorPlans_ValidationScore DEFAULT 0;
END
GO

IF COL_LENGTH(N'dbo.DirectorPlans', N'ViolationsJson') IS NULL
BEGIN
    ALTER TABLE dbo.DirectorPlans ADD ViolationsJson NVARCHAR(MAX) NOT NULL
        CONSTRAINT DF_DirectorPlans_ViolationsJson DEFAULT N'';
END
GO

IF COL_LENGTH(N'dbo.DirectorPlans', N'RepairCount') IS NULL
BEGIN
    ALTER TABLE dbo.DirectorPlans ADD RepairCount INT NOT NULL
        CONSTRAINT DF_DirectorPlans_RepairCount DEFAULT 0;
END
GO

IF COL_LENGTH(N'dbo.DirectorPlans', N'LastValidationAt') IS NULL
BEGIN
    ALTER TABLE dbo.DirectorPlans ADD LastValidationAt DATETIME NULL;
END
GO
