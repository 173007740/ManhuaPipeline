-- =============================================
-- DirectorPlans V1.5：结构化 ActionPlan + 回合数 + VFX 峰值段
-- 幂等，可重复执行；旧的 CombatBeats 列不再由代码读写，保留不删除。
-- =============================================
IF COL_LENGTH(N'dbo.DirectorPlans', N'CombatRoundCount') IS NULL
BEGIN
    ALTER TABLE dbo.DirectorPlans ADD CombatRoundCount INT NOT NULL
        CONSTRAINT DF_DirectorPlans_CombatRoundCount DEFAULT 0;
END
GO

IF COL_LENGTH(N'dbo.DirectorPlans', N'VfxPeakPhase') IS NULL
BEGIN
    ALTER TABLE dbo.DirectorPlans ADD VfxPeakPhase NVARCHAR(20) NOT NULL
        CONSTRAINT DF_DirectorPlans_VfxPeakPhase DEFAULT N'';
END
GO

IF COL_LENGTH(N'dbo.DirectorPlans', N'ActionPlan') IS NULL
BEGIN
    ALTER TABLE dbo.DirectorPlans ADD ActionPlan NVARCHAR(MAX) NOT NULL
        CONSTRAINT DF_DirectorPlans_ActionPlan DEFAULT N'';
END
GO
