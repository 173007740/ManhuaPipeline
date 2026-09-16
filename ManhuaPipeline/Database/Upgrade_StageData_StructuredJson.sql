-- StageData: 结构化产物列（L1 故事层）
--
-- 背景：阶段 1/2/3 原先各自独立调用一次大模型（阶段 2 吃阶段 1 的文本、阶段 3 吃阶段 2 的文本），
-- 每转述一次就衰减一次故事信息。改造后改为一次调用产出「节 1-5」结构化故事基线：
--   节 1 故事核心 + 节 2 主线因果链        → 阶段 1（创意构思）
--   节 3 观众必须看懂的信息 + 节 4 情绪曲线 → 阶段 2（故事分析）
--   节 5 视觉锚点 + 分集大纲               → 阶段 3（全局蓝图）
-- JSON 落在阶段 1 这一行的 StructuredJson 上，阶段 2/3 直接复用渲染，不再重复调用大模型。
--
-- 代码依赖：Models/StageData.cs（StructuredJson）、DbService.SaveStageStructuredJson、
--          StoryFoundationRenderer、DatabaseSchemaValidator 的 StageData 校验。
-- 未执行本脚本时应用会启动失败并提示缺少 StageData.StructuredJson 列。

IF COL_LENGTH(N'dbo.StageData', N'StructuredJson') IS NULL
BEGIN
    ALTER TABLE dbo.StageData ADD StructuredJson NVARCHAR(MAX) NULL;
END
GO
