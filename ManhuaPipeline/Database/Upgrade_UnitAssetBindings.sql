-- ============================================================
-- 分集细化单元资产绑定表
-- Stage 4（分集细化）完成后，把每个 【单元X.Y】 解析成对项目资产的确定引用并写入本表；
-- Stage 5（分镜脚本）以「本单元已绑资产」为候选集，保证分镜不引入单元外的资产。
-- 该表只存“单元 ↔ 资产”关系，引用图/正文一律使用规范资产名，AssetId 仅作库内外键。
-- 需手工执行一次：ManhuaPipeline/Database/Upgrade_UnitAssetBindings.sql
-- ============================================================
IF OBJECT_ID(N'dbo.UnitAssetBindings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UnitAssetBindings (
        BindingId       INT IDENTITY(1,1) NOT NULL,
        ProjectId       INT NOT NULL,
        EpisodeNumber   INT NOT NULL,
        UnitNumber      NVARCHAR(64) NOT NULL,
        Category        NVARCHAR(16) NOT NULL,   -- Character / Environment / Prop / Effect
        AssetId         INT NOT NULL,
        Name            NVARCHAR(200) NOT NULL,
        HasImage        BIT NOT NULL DEFAULT(0),
        SortOrder       INT NOT NULL DEFAULT(0),
        CONSTRAINT PK_UnitAssetBindings PRIMARY KEY (BindingId)
    );
    CREATE INDEX IX_UnitAssetBindings_Project ON dbo.UnitAssetBindings (ProjectId);
    CREATE INDEX IX_UnitAssetBindings_Unit ON dbo.UnitAssetBindings (ProjectId, EpisodeNumber, UnitNumber);
END
GO
