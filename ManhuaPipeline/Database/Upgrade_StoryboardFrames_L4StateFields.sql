-- StoryboardFrames: L4 镜头状态机六字段
--
-- 背景：阶段 5 原先只产出「描述文本集合」，镜头之间是否接得上只能靠人眼检查。
-- 改造后每个镜头必须给出六项状态字段（对应 short-drama-agent 节 16「视频镜头任务卡」），使分镜变成可校验的状态机：
--   StartState       起始状态：本镜开始时角色/道具/空间的状态，必须接住上一镜的 EndState
--   SingleAction     单一动作：本镜只做一件什么事（一镜一动作）
--   EndState         结束状态：本镜结束时的状态，供下一镜接住
--   NextConnection   衔接下一镜：由此镜进入下一镜的动机（动作延续/视线/声音先入/信息提问/空间移动）
--   ForbiddenChanges 禁止变化：本镜内不得改变的空间关系/服装/发型/伤势/道具位置与归属/时间光照
--   NewInformation   新信息：观众在本镜获得的信息增量（空转镜头写「无」，供阶段 9 判空转）
--
-- 代码依赖：Models/Episode.cs（StoryboardFrame 六属性）、StoryboardFrameFieldParser（中英文别名字段）、
--          DbService.ReadFrame / InsertFrameSql / BindFrame、
--          LLMService.PlanShots（输出格式与【镜头状态机铁律】）、
--          DatabaseSchemaValidator 的 StoryboardFrames 校验。
-- 未执行本脚本时应用会启动失败并提示缺少对应列。

IF COL_LENGTH(N'dbo.StoryboardFrames', N'StartState') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD [StartState] NVARCHAR(MAX) NULL;
GO
IF COL_LENGTH(N'dbo.StoryboardFrames', N'SingleAction') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD [SingleAction] NVARCHAR(MAX) NULL;
GO
IF COL_LENGTH(N'dbo.StoryboardFrames', N'EndState') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD [EndState] NVARCHAR(MAX) NULL;
GO
IF COL_LENGTH(N'dbo.StoryboardFrames', N'NextConnection') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD [NextConnection] NVARCHAR(MAX) NULL;
GO
IF COL_LENGTH(N'dbo.StoryboardFrames', N'ForbiddenChanges') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD [ForbiddenChanges] NVARCHAR(MAX) NULL;
GO
IF COL_LENGTH(N'dbo.StoryboardFrames', N'NewInformation') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD [NewInformation] NVARCHAR(MAX) NULL;
GO
