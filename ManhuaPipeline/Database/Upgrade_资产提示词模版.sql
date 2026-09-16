/* ============================================================================
   Upgrade_资产提示词模版.sql
   目标：让「资产提取」（Stage 6 角色 / 7 道具 / 8 环境 / 11 特效）在提取时
         按用户自定义模版自动生成每个资产的「出图提示词」，后续出图直接取用。

   1) 新增 AssetPromptTemplates：每用户每资产类型一条模版
      - StyleLock       统一视觉风格（出图时自动追加）
      - RuleText        该类资产的提示词规则（提取时指导 LLM 怎么写正文）
      - NegativePrompt  统一负面提示词（出图时自动追加）
   2) 四张资产表新增 ImagePrompt / NegativePrompt：存「提取时生成」的提示词
      - ImagePrompt     资产本体描述（不含统一风格，风格出图时拼接，便于统一改风格）
      - NegativePrompt  该资产专属负面词（可空，出图时再叠加模版负面词）

   幂等：可重复执行，已存在的表/字段会跳过。
   ============================================================================ */
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.AssetPromptTemplates', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AssetPromptTemplates
    (
        TemplateId     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AssetPromptTemplates PRIMARY KEY,
        UserId         INT               NOT NULL,
        Category       NVARCHAR(20)      NOT NULL,   -- characters / props / environments / effects
        StyleLock      NVARCHAR(MAX)     NULL,       -- 统一视觉风格（硬锁定）
        NegativePrompt NVARCHAR(MAX)     NULL,       -- 统一负面提示词
        RuleText       NVARCHAR(MAX)     NULL,       -- 该类资产的出图提示词规则
        Enabled        BIT               NOT NULL CONSTRAINT DF_AssetPromptTemplates_Enabled DEFAULT(1),
        UpdatedAt      DATETIME2         NOT NULL CONSTRAINT DF_AssetPromptTemplates_UpdatedAt DEFAULT(SYSDATETIME())
    );
    CREATE UNIQUE INDEX UX_AssetPromptTemplates_User_Category ON dbo.AssetPromptTemplates(UserId, Category);
    PRINT N'已创建表 dbo.AssetPromptTemplates';
END
ELSE
    PRINT N'表 dbo.AssetPromptTemplates 已存在，跳过';
GO

IF COL_LENGTH(N'dbo.CharacterAssets', N'ImagePrompt') IS NULL
    ALTER TABLE dbo.CharacterAssets ADD ImagePrompt NVARCHAR(MAX) NULL, NegativePrompt NVARCHAR(MAX) NULL;
IF COL_LENGTH(N'dbo.PropAssets', N'ImagePrompt') IS NULL
    ALTER TABLE dbo.PropAssets ADD ImagePrompt NVARCHAR(MAX) NULL, NegativePrompt NVARCHAR(MAX) NULL;
IF COL_LENGTH(N'dbo.EnvironmentAssets', N'ImagePrompt') IS NULL
    ALTER TABLE dbo.EnvironmentAssets ADD ImagePrompt NVARCHAR(MAX) NULL, NegativePrompt NVARCHAR(MAX) NULL;
IF COL_LENGTH(N'dbo.EffectAssets', N'ImagePrompt') IS NULL
    ALTER TABLE dbo.EffectAssets ADD ImagePrompt NVARCHAR(MAX) NULL, NegativePrompt NVARCHAR(MAX) NULL;
GO

PRINT N'=== 结果核对：资产表字段 ===';
SELECT t.name AS TableName, c.name AS ColumnName, TYPE_NAME(c.user_type_id) AS DataType
FROM sys.tables t
JOIN sys.columns c ON c.object_id = t.object_id
WHERE t.name IN (N'CharacterAssets', N'PropAssets', N'EnvironmentAssets', N'EffectAssets', N'AssetPromptTemplates')
  AND c.name IN (N'ImagePrompt', N'NegativePrompt', N'TemplateId', N'Category', N'StyleLock', N'RuleText')
ORDER BY t.name, c.name;
GO
