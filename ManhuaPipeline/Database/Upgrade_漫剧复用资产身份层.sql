/* 漫剧复用资产身份层
   ------------------------------------------------------------------
   现象：角色 / 场景 / 道具都是整部漫剧复用的，但资产表是按集（ProjectId）存的：
        第 1 集一条「杨彦刚」、第 2 集又一条「杨彦刚」——两条互不相干的记录，
        没有任何东西说明「这俩是同一个人」。于是每一集各自抽、各自出图，
        12 集下来杨彦刚是 12 张不同的脸。
        想靠名字临时匹配也不牢靠：某一集写成「杨彦刚（少年）」就又断了。

   改法：加一层「漫剧复用资产身份」——把身份提到漫剧层，每集的资产只是这个身份
        在这一集的一份实例，用 IdentityId 指过去。
        四类共用一张表（角色 / 场景 / 道具 / 特效），机制一样，不重复建四套。

   锚点（AnchorImageUrl / AnchorPrompt / AnchorProjectId / AnchorAssetId）：
        这个身份的「定妆图」出自哪一集哪条资产。后面几集出图照着它出，脸才对得上。
   ------------------------------------------------------------------ */

IF OBJECT_ID('DramaAssetIdentities', 'U') IS NULL
BEGIN
    CREATE TABLE DramaAssetIdentities (
        IdentityId       INT IDENTITY(1,1) PRIMARY KEY,
        DramaId          INT            NOT NULL,
        Category         NVARCHAR(20)   NOT NULL,          -- character / environment / prop / effect
        Name             NVARCHAR(200)  NOT NULL,          -- 规范名：全剧只在这一处定
        AliasJson        NVARCHAR(MAX)  NULL,              -- 别名：兜住「杨彦刚（少年）」这类写法
        Description      NVARCHAR(MAX)  NULL,
        AnchorImageUrl   NVARCHAR(512)  NULL,              -- 形象锚图
        AnchorPrompt     NVARCHAR(MAX)  NULL,              -- 锚图那次用的提示词
        AnchorProjectId  INT            NULL,              -- 锚图出自哪一集
        AnchorAssetId    INT            NULL,              -- 锚图出自哪条资产
        CreatedAt        DATETIME2      NOT NULL DEFAULT SYSDATETIME(),
        UpdatedAt        DATETIME2      NOT NULL DEFAULT SYSDATETIME()
    );
    CREATE UNIQUE INDEX UX_DramaAssetIdentities
        ON DramaAssetIdentities(DramaId, Category, Name);
END

-- 四张资产表各加一列，指向身份
DECLARE @t NVARCHAR(50), @sql NVARCHAR(MAX);
DECLARE cur CURSOR FOR
    SELECT name FROM (VALUES ('CharacterAssets'),('EnvironmentAssets'),('PropAssets'),('EffectAssets')) v(name);
OPEN cur; FETCH NEXT FROM cur INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME=@t AND COLUMN_NAME='IdentityId')
    BEGIN
        SET @sql = 'ALTER TABLE ' + @t + ' ADD IdentityId INT NULL';
        EXEC(@sql);
    END
    FETCH NEXT FROM cur INTO @t;
END
CLOSE cur; DEALLOCATE cur;

SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
WHERE COLUMN_NAME='IdentityId' ORDER BY TABLE_NAME;
