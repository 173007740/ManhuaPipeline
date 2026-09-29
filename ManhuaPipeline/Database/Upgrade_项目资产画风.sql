-- =============================================================
-- 项目级「资产画风」：跟视频画风（Projects.StyleId → VideoStyles）分开维护
--
-- 为什么要分开：
--   · StyleId     视频画风，出视频时作为【项目风格】整段写进提示词，全片唯一画风来源
--   · ImageStyleId 资产画风，只管四类资产（角色/道具/环境/特效）的出图
--   两套风格诉求不一样：成片要的是统一的电影感基调，资产图要的是干净的设定图质感，
--   混在一个字段上改一边就会动到另一边。
--
-- ImageStyleId 指向 ImageStyles（图片风格库，见 Upgrade_图片风格库.sql），
-- 取的是 StyleDesc 那段描述，出图时拼到提示词末尾。
--
-- 没设（NULL）时资产出图退回原来的视频画风，老项目行为不变。
-- =============================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Projects') AND name = N'ImageStyleId')
BEGIN
    ALTER TABLE dbo.Projects ADD ImageStyleId INT NULL;      -- 资产画风（→ ImageStyles.StyleId）
END
GO

-- 立项表单（P0）的「画风方向」从手填改成图片风格库下拉：
-- type 改成 select，source=imageStyle 告诉前端选项要去 /api/image-style 拉，
-- 选中的值就是 ImageStyles.StyleId，立项时顺手存进项目。
UPDATE DirectorSkillStages
SET InputsJson = N'[
 {"key":"aspect","label":"画幅","type":"select","options":["16:9 横屏","9:16 竖屏","21:9 超宽"],"default":"16:9 横屏","required":true},
 {"key":"delivery","label":"交付形态","type":"select","options":["Markdown + 离线看板","纯 Markdown"],"default":"Markdown + 离线看板","required":true},
 {"key":"genre","label":"题材","type":"text","required":true},
 {"key":"artStyle","label":"画风方向","type":"select","source":"imageStyle","required":true,"tip":"从图片风格库里挑，资产出图会自动带上这段风格描述"},
 {"key":"hook","label":"核心爽点","type":"textarea","required":true},
 {"key":"episodeCount","label":"总集数","type":"number","default":"12","required":true},
 {"key":"episodeDuration","label":"单集时长（秒）","type":"number","default":"180","required":true},
 {"key":"platform","label":"目标平台","type":"text","default":"抖音","required":true},
 {"key":"premise","label":"核心设定","type":"textarea","required":true},
 {"key":"characters","label":"角色","type":"list","required":true,"tip":"逗号分隔"}
]'
WHERE PackId = 1 AND StageKey = 'P0';
GO
