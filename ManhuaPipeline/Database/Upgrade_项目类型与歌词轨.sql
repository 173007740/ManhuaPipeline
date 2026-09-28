/*
    项目内容类型（短剧 / 广告 / MV）与 MV 歌词轨

    设计要点：
    - ProjectType 只决定结构性规则（时间轴来源、是否线性叙事、产品如何出现、镜头时长如何取档），
      不决定画风与题材；去水、动作过程化等画面质量规则对三种类型一律通用，不按类型分叉。
    - ProjectLyricLines 仅对 ProjectType='mv' 且有对口型需求的项目有意义。
      H3 口型由 <d> 标签逐字触发，歌词必须进「对话/台词」才有口型；
      但唱歌比说话慢，镜头时长不能按说话语速的字数启发式推算，必须取真歌的实际时间戳。

    幂等，可重复执行。
*/

SET NOCOUNT ON;
GO

-- 一、Projects.ProjectType
-- drama=短剧（默认，等同改造前行为） / ad=广告 / mv=歌曲MV
IF COL_LENGTH('dbo.Projects','ProjectType') IS NULL
BEGIN
    ALTER TABLE [dbo].[Projects]
        ADD [ProjectType] NVARCHAR(20) NOT NULL
        CONSTRAINT DF_Projects_ProjectType DEFAULT N'drama';
END
GO

-- 历史数据兜底：空串或非法值一律归为 drama，保证旧项目行为不变
UPDATE [dbo].[Projects]
SET [ProjectType] = N'drama'
WHERE [ProjectType] IS NULL
   OR LTRIM(RTRIM([ProjectType])) = N''
   OR [ProjectType] NOT IN (N'drama', N'ad', N'mv');
GO

-- 二、MV 歌词轨
IF OBJECT_ID('dbo.ProjectLyricLines','U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ProjectLyricLines] (
        [LineId]    INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId] INT           NOT NULL,
        [LineIndex] INT           NOT NULL,
        [StartSec]  DECIMAL(8,2)  NOT NULL,
        [EndSec]    DECIMAL(8,2)  NOT NULL,
        [Text]      NVARCHAR(500) NOT NULL,
        [CreatedAt] DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_ProjectLyricLines_Project]
        ON [dbo].[ProjectLyricLines]([ProjectId], [LineIndex]);
END
GO
