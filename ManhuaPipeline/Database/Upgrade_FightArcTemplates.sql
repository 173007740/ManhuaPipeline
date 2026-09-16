-- =============================================
-- FightArcTemplates 战斗段落骨架库
-- 新增表 + 6 套种子骨架；DirectorPlans 增加 FightArcType / FightSequenceJson。
-- 幂等，可重复执行；只建表加字段补种子，不删旧数据。
-- 执行：SSMS 连接 ManhuaPipeline 后整段执行，或 sqlcmd -i 本脚本。
-- 使用 sqlcmd 时请加 UTF-8 编码参数：sqlcmd -f 65001 -i 本脚本。
-- =============================================
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.FightArcTemplates', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FightArcTemplates (
        FightArcTemplateId   INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_FightArcTemplates PRIMARY KEY,
        ArcTypeId            NVARCHAR(50) NOT NULL,
        Name                 NVARCHAR(100) NOT NULL,
        Description          NVARCHAR(500) NOT NULL
            CONSTRAINT DF_FightArcTemplates_Description DEFAULT N'',
        Version              NVARCHAR(20) NOT NULL
            CONSTRAINT DF_FightArcTemplates_Version DEFAULT N'1.0',
        PhasesJson           NVARCHAR(MAX) NOT NULL
            CONSTRAINT DF_FightArcTemplates_PhasesJson DEFAULT N'[]',
        DurationBudgetJson   NVARCHAR(MAX) NOT NULL
            CONSTRAINT DF_FightArcTemplates_DurationBudgetJson DEFAULT N'{}',
        RulesJson            NVARCHAR(MAX) NOT NULL
            CONSTRAINT DF_FightArcTemplates_RulesJson DEFAULT N'[]',
        Status               NVARCHAR(20) NOT NULL
            CONSTRAINT DF_FightArcTemplates_Status DEFAULT N'Active',
        CreatedAt            DATETIME2 NOT NULL
            CONSTRAINT DF_FightArcTemplates_CreatedAt DEFAULT GETDATE(),
        UpdatedAt            DATETIME2 NOT NULL
            CONSTRAINT DF_FightArcTemplates_UpdatedAt DEFAULT GETDATE(),
        CONSTRAINT UQ_FightArcTemplates_ArcType UNIQUE (ArcTypeId)
    );
END
GO

IF COL_LENGTH(N'dbo.DirectorPlans', N'FightArcType') IS NULL
BEGIN
    ALTER TABLE dbo.DirectorPlans ADD FightArcType NVARCHAR(50) NOT NULL
        CONSTRAINT DF_DirectorPlans_FightArcType DEFAULT N'';
END
GO

IF COL_LENGTH(N'dbo.DirectorPlans', N'FightSequenceJson') IS NULL
BEGIN
    ALTER TABLE dbo.DirectorPlans ADD FightSequenceJson NVARCHAR(MAX) NOT NULL
        CONSTRAINT DF_DirectorPlans_FightSequenceJson DEFAULT N'';
END
GO

-- ========== 种子 1：完整对决 ==========
IF NOT EXISTS (SELECT 1 FROM dbo.FightArcTemplates WHERE ArcTypeId=N'full_duel')
BEGIN
    INSERT INTO dbo.FightArcTemplates(ArcTypeId, Name, Description, Version, PhasesJson, DurationBudgetJson, RulesJson)
    VALUES(
        N'full_duel',
        N'完整对决',
        N'主要角色、高潮对决：对峙→对话→接触→缠斗→升级→对轰→胜负→切场',
        N'1.0',
        N'[
          {"phaseNo":1,"name":"对峙进场","purpose":"A/B 特写与双人同框，立住对手","durationPercent":10,"minSeconds":6,"maxSeconds":9,"shotCount":3,"shotStyle":"特写→特写→双人同框","camera":"缓推/固定","vfxLevel":5,"skills":[],"dialogue":"立住对手，交代此战动机","endState":"双方进入战斗距离，武器与气血待发","nextCondition":"对话动机成立或对方先出手"},
          {"phaseNo":2,"name":"对话动机","purpose":"交代为什么打、条件与情绪","durationPercent":15,"minSeconds":9,"maxSeconds":14,"shotCount":3,"shotStyle":"正反打特写","camera":"缓推正反打","vfxLevel":5,"skills":[],"dialogue":"谈判破裂或条件达成","endState":"情绪推到临界","nextCondition":"进入接触战"},
          {"phaseNo":3,"name":"接触战","purpose":"双方首次对撞，建立攻防节奏","durationPercent":15,"minSeconds":9,"maxSeconds":14,"shotCount":3,"shotStyle":"跟拍中景","camera":"贴身跟拍/手持","vfxLevel":20,"skills":["T3常规技能"],"dialogue":"","endState":"首次对撞，双方距离拉近","nextCondition":"进入连续缠斗"},
          {"phaseNo":4,"name":"缠斗","purpose":"多镜头连续攻防，回合密集","durationPercent":30,"minSeconds":18,"maxSeconds":27,"shotCount":6,"shotStyle":"快切中近景","camera":"高速快切/环绕","vfxLevel":30,"skills":["T3常规技能"],"dialogue":"","endState":"多回合攻防，双方各有受伤或压制","nextCondition":"一方找到破绽或升级"},
          {"phaseNo":5,"name":"升级","purpose":"蓄力停顿，镜头拉开，准备大招","durationPercent":10,"minSeconds":6,"maxSeconds":9,"shotCount":2,"shotStyle":"拉开全景","camera":"拉高/定格","vfxLevel":60,"skills":["T4强力技能"],"dialogue":"技能名与台词入镜","endState":"大招蓄力完成","nextCondition":"进入大招对轰"},
          {"phaseNo":6,"name":"大招对轰","purpose":"双方大招正面相撞，特效峰值","durationPercent":15,"minSeconds":9,"maxSeconds":14,"shotCount":3,"shotStyle":"大远景+正面","camera":"正面广角/慢动作0.2-0.4s","vfxLevel":100,"skills":["T5终极大招"],"dialogue":"","endState":"冲击波扩散，胜负倾向明确","nextCondition":"分出胜负"},
          {"phaseNo":7,"name":"胜负定格","purpose":"一人倒下或被压制，情绪定格","durationPercent":3,"minSeconds":2,"maxSeconds":4,"shotCount":1,"shotStyle":"特写定格","camera":"定格收尾","vfxLevel":40,"skills":[],"dialogue":"","endState":"一方倒下/被压制，留有代价","nextCondition":"余韵收束"},
          {"phaseNo":8,"name":"切场","purpose":"接下一场戏","durationPercent":2,"minSeconds":1,"maxSeconds":3,"shotCount":1,"shotStyle":"空镜/远景","camera":"缓拉","vfxLevel":10,"skills":[],"dialogue":"","endState":"镜头离开战场或接到下一场","nextCondition":"下一单元可接"}
        ]',
        N'{"minSeconds":60,"maxSeconds":90,"defaultSeconds":75,"shotTiers":[5,11,15]}',
        N'["阶段按顺序推进，禁止跳段或只写一段","镜头间状态不跳变，后一镜接前一镜结束画面","VFX 克制且峰值只在大招对轰阶段","技能名与技能归属不可改","胜负必须有代价，禁止无代价取胜"]');
END
GO

-- ========== 种子 2：遭遇战 ==========
IF NOT EXISTS (SELECT 1 FROM dbo.FightArcTemplates WHERE ArcTypeId=N'encounter')
BEGIN
    INSERT INTO dbo.FightArcTemplates(ArcTypeId, Name, Description, Version, PhasesJson, DurationBudgetJson, RulesJson)
    VALUES(
        N'encounter',
        N'遭遇战',
        N'中途遇敌、小冲突：警觉→对冲→快速交锋→短暂胜负→脱战',
        N'1.0',
        N'[
          {"phaseNo":1,"name":"警觉","purpose":"发现敌人，快速判断威胁","durationPercent":20,"minSeconds":3,"maxSeconds":6,"shotCount":2,"shotStyle":"远景→特写","camera":"急推","vfxLevel":10,"skills":[],"dialogue":"","endState":"双方互相锁定","nextCondition":"直接对冲"},
          {"phaseNo":2,"name":"对冲","purpose":"双方提速对撞，第一轮接触","durationPercent":20,"minSeconds":3,"maxSeconds":6,"shotCount":2,"shotStyle":"中景跟拍","camera":"侧方跟拍","vfxLevel":25,"skills":[],"dialogue":"","endState":"第一次兵器/拳脚相接","nextCondition":"进入快速交锋"},
          {"phaseNo":3,"name":"快速交锋","purpose":"2-4 回合连续攻防，节奏最快","durationPercent":40,"minSeconds":6,"maxSeconds":12,"shotCount":4,"shotStyle":"快切中近景","camera":"手持快切","vfxLevel":40,"skills":["T3常规技能"],"dialogue":"","endState":"一方被压制或找到破绽","nextCondition":"短暂分胜负"},
          {"phaseNo":4,"name":"短暂胜负","purpose":"命中/击退，明确本次冲突结果","durationPercent":15,"minSeconds":2,"maxSeconds":5,"shotCount":2,"shotStyle":"特写/全景","camera":"顿帧+拉远","vfxLevel":60,"skills":["T4强力技能"],"dialogue":"","endState":"一方受伤/退开，冲突中断","nextCondition":"脱战或继续追打"},
          {"phaseNo":5,"name":"脱战","purpose":"离开冲突，接下一段剧情","durationPercent":5,"minSeconds":1,"maxSeconds":2,"shotCount":1,"shotStyle":"远景","camera":"缓拉","vfxLevel":15,"skills":[],"dialogue":"","endState":"双方拉开距离或一方撤离","nextCondition":"下一单元可接"}
        ]',
        N'{"minSeconds":15,"maxSeconds":30,"defaultSeconds":20,"shotTiers":[5,11,15]}',
        N'["快速交锋阶段回合必须密集，禁止一次挥击结束","脱战前必须交代胜负或受伤","技能归属角色必须出场"]');
END
GO

-- ========== 种子 3：偷袭/速杀 ==========
IF NOT EXISTS (SELECT 1 FROM dbo.FightArcTemplates WHERE ArcTypeId=N'assassination')
BEGIN
    INSERT INTO dbo.FightArcTemplates(ArcTypeId, Name, Description, Version, PhasesJson, DurationBudgetJson, RulesJson)
    VALUES(
        N'assassination',
        N'偷袭/速杀',
        N'暗杀、秒杀、一击制胜：潜伏→突进→一击命中→环境反应→切场',
        N'1.0',
        N'[
          {"phaseNo":1,"name":"潜伏","purpose":"隐藏身形，观察目标破绽","durationPercent":25,"minSeconds":2,"maxSeconds":4,"shotCount":2,"shotStyle":"特写/环境空镜","camera":"缓推/固定","vfxLevel":5,"skills":[],"dialogue":"","endState":"目标进入攻击范围","nextCondition":"瞬间突进"},
          {"phaseNo":2,"name":"突进","purpose":"高速逼近，破绽一瞬即逝","durationPercent":20,"minSeconds":2,"maxSeconds":3,"shotCount":2,"shotStyle":"贴地跟拍","camera":"高速甩镜","vfxLevel":20,"skills":[],"dialogue":"","endState":"进入一击距离","nextCondition":"致命一击"},
          {"phaseNo":3,"name":"一击命中","purpose":"唯一重击，命中必须清晰","durationPercent":20,"minSeconds":2,"maxSeconds":3,"shotCount":1,"shotStyle":"近景/特写","camera":"顿帧+慢动作0.2-0.4s","vfxLevel":70,"skills":["T4/T5技能"],"dialogue":"技能名可入台词","endState":"目标受击定格","nextCondition":"环境反应"},
          {"phaseNo":4,"name":"环境反应","purpose":"用环境反馈体现一击的后果","durationPercent":20,"minSeconds":2,"maxSeconds":3,"shotCount":1,"shotStyle":"全景","camera":"震动跟随/拉远","vfxLevel":60,"skills":[],"dialogue":"","endState":"气浪/尘土/碎物扩散","nextCondition":"切场"},
          {"phaseNo":5,"name":"切场","purpose":"收束本单元，接下一场","durationPercent":15,"minSeconds":1,"maxSeconds":2,"shotCount":1,"shotStyle":"空镜/远景","camera":"缓拉","vfxLevel":10,"skills":[],"dialogue":"","endState":"镜头离开现场","nextCondition":"下一单元可接"}
        ]',
        N'{"minSeconds":8,"maxSeconds":15,"defaultSeconds":10,"shotTiers":[5,11,15]}',
        N'["潜伏与突进必须存在，禁止直接瞬移命中","一击命中必须有受击反馈与环境反馈","技能名必须完整保留"]');
END
GO

-- ========== 种子 4：围杀/突围 ==========
IF NOT EXISTS (SELECT 1 FROM dbo.FightArcTemplates WHERE ArcTypeId=N'siege_breakout')
BEGIN
    INSERT INTO dbo.FightArcTemplates(ArcTypeId, Name, Description, Version, PhasesJson, DurationBudgetJson, RulesJson)
    VALUES(
        N'siege_breakout',
        N'围杀/突围',
        N'以少敌多、阵法围困：合围→轮攻→硬抗→破局大招→反杀/撤离',
        N'1.0',
        N'[
          {"phaseNo":1,"name":"合围","purpose":"展示敌方人数与阵型，压力建立","durationPercent":15,"minSeconds":7,"maxSeconds":11,"shotCount":3,"shotStyle":"大远景→环绕","camera":"升空/环绕","vfxLevel":15,"skills":[],"dialogue":"","endState":"包围圈成型","nextCondition":"轮攻开始"},
          {"phaseNo":2,"name":"轮攻","purpose":"敌方分批进攻，主角连续应对","durationPercent":35,"minSeconds":16,"maxSeconds":26,"shotCount":7,"shotStyle":"快切中近景","camera":"手持快切/环绕","vfxLevel":40,"skills":["T3常规技能"],"dialogue":"","endState":"多人被击退但包围仍在","nextCondition":"主角被压制到硬抗"},
          {"phaseNo":3,"name":"硬抗","purpose":"主角承受合击，受伤或金身表现","durationPercent":15,"minSeconds":7,"maxSeconds":11,"shotCount":3,"shotStyle":"特写/低机位","camera":"固定/缓推","vfxLevel":60,"skills":["T3/T4防御或护体"],"dialogue":"","endState":"主角撑住合击，找到破局时机","nextCondition":"破局大招"},
          {"phaseNo":4,"name":"破局大招","purpose":"范围大招撕开包围，特效峰值","durationPercent":15,"minSeconds":7,"maxSeconds":11,"shotCount":2,"shotStyle":"正面全景","camera":"正面广角/慢动作","vfxLevel":100,"skills":["T5终极大招"],"dialogue":"技能名与台词入镜","endState":"包围被撕开","nextCondition":"反杀或撤离"},
          {"phaseNo":5,"name":"反杀/撤离","purpose":"确认战果或脱离战场","durationPercent":20,"minSeconds":9,"maxSeconds":15,"shotCount":4,"shotStyle":"远景/跟拍","camera":"跟随撤离","vfxLevel":50,"skills":["T4强力技能"],"dialogue":"","endState":"敌人退散或主角突围成功","nextCondition":"下一单元可接"}
        ]',
        N'{"minSeconds":45,"maxSeconds":75,"defaultSeconds":60,"shotTiers":[5,11,15]}',
        N'["敌方人数与阵型必须有画面反馈，禁止排队单挑","大招前必须有硬抗/蓄力铺垫","破局后必须交代反杀或撤离结果"]');
END
GO

-- ========== 种子 5：追逐战 ==========
IF NOT EXISTS (SELECT 1 FROM dbo.FightArcTemplates WHERE ArcTypeId=N'chase')
BEGIN
    INSERT INTO dbo.FightArcTemplates(ArcTypeId, Name, Description, Version, PhasesJson, DurationBudgetJson, RulesJson)
    VALUES(
        N'chase',
        N'追逐战',
        N'追击、逃亡、赶路冲突：追逃→地形变化→拦截→正面交锋→再逃/停下',
        N'1.0',
        N'[
          {"phaseNo":1,"name":"追逃","purpose":"建立速度差与追逐方向","durationPercent":35,"minSeconds":7,"maxSeconds":14,"shotCount":4,"shotStyle":"远景/贴地跟拍","camera":"侧方跟随/甩镜","vfxLevel":20,"skills":[],"dialogue":"","endState":"前后距离稳定","nextCondition":"地形变化"},
          {"phaseNo":2,"name":"地形变化","purpose":"借地形拉开或逼近距离","durationPercent":25,"minSeconds":5,"maxSeconds":10,"shotCount":3,"shotStyle":"全景/俯拍","camera":"穿越机/升空","vfxLevel":30,"skills":["轻功或位移技能"],"dialogue":"","endState":"穿越障碍，距离改变","nextCondition":"拦截"},
          {"phaseNo":3,"name":"拦截","purpose":"前方拦截或陷阱，追逃中断","durationPercent":10,"minSeconds":2,"maxSeconds":4,"shotCount":2,"shotStyle":"正面中景","camera":"急停/快推","vfxLevel":40,"skills":["T3常规技能"],"dialogue":"","endState":"被迫停下或转向","nextCondition":"正面交锋"},
          {"phaseNo":4,"name":"正面交锋","purpose":"短暂硬碰硬，解决拦截","durationPercent":20,"minSeconds":4,"maxSeconds":8,"shotCount":3,"shotStyle":"快切中近景","camera":"手持快切","vfxLevel":60,"skills":["T4强力技能"],"dialogue":"","endState":"拦截被击退/绕过","nextCondition":"再逃或停下"},
          {"phaseNo":5,"name":"再逃/停下","purpose":"回到追逐或结束追逐","durationPercent":10,"minSeconds":2,"maxSeconds":4,"shotCount":2,"shotStyle":"远景","camera":"缓拉/跟随","vfxLevel":20,"skills":[],"dialogue":"","endState":"追逃继续或目标停下","nextCondition":"下一单元可接"}
        ]',
        N'{"minSeconds":20,"maxSeconds":40,"defaultSeconds":30,"shotTiers":[5,11,15]}',
        N'["位移方向必须明确，禁止原地打转","地形变化要影响距离与路径","正面交锋只解决拦截，不喧宾夺主"]');
END
GO

-- ========== 种子 6：文戏对峙 ==========
IF NOT EXISTS (SELECT 1 FROM dbo.FightArcTemplates WHERE ArcTypeId=N'drama_confrontation')
BEGIN
    INSERT INTO dbo.FightArcTemplates(ArcTypeId, Name, Description, Version, PhasesJson, DurationBudgetJson, RulesJson)
    VALUES(
        N'drama_confrontation',
        N'文戏对峙',
        N'不下死手的冲突、装逼对峙：对峙→言语交锋→单次冲突→被制止/各自退场',
        N'1.0',
        N'[
          {"phaseNo":1,"name":"对峙","purpose":"双方站位与气机对立","durationPercent":30,"minSeconds":5,"maxSeconds":9,"shotCount":3,"shotStyle":"特写/双人同框","camera":"缓推正反打","vfxLevel":5,"skills":[],"dialogue":"先立住双方立场","endState":"双方距离与姿态对立","nextCondition":"言语交锋"},
          {"phaseNo":2,"name":"言语交锋","purpose":"嘴炮/谈判，情绪升级","durationPercent":45,"minSeconds":7,"maxSeconds":14,"shotCount":4,"shotStyle":"正反打特写","camera":"固定/缓推","vfxLevel":10,"skills":[],"dialogue":"逐字保留台词","endState":"情绪推到临界点","nextCondition":"单次冲突"},
          {"phaseNo":3,"name":"单次冲突","purpose":"只出手一次，展示实力不扩大","durationPercent":15,"minSeconds":2,"maxSeconds":5,"shotCount":2,"shotStyle":"中景/特写","camera":"快推+顿帧","vfxLevel":40,"skills":["T3常规技能"],"dialogue":"","endState":"一次交手后被制止或各自退开","nextCondition":"收束退场"},
          {"phaseNo":4,"name":"被制止/各自退场","purpose":"冲突收束，接下一场戏","durationPercent":10,"minSeconds":2,"maxSeconds":3,"shotCount":2,"shotStyle":"全景","camera":"缓拉","vfxLevel":15,"skills":[],"dialogue":"","endState":"双方退场或第三方介入","nextCondition":"下一单元可接"}
        ]',
        N'{"minSeconds":15,"maxSeconds":30,"defaultSeconds":20,"shotTiers":[5,11,15]}',
        N'["台词必须逐字保留","单次冲突只出手一次，禁止扩大成完整打斗","退场前必须交代双方去向"]');
END
GO

PRINT N'FightArcTemplates 升级脚本执行完成：';
SELECT COUNT(*) AS FightArcTemplateCount FROM dbo.FightArcTemplates;
SELECT COUNT(*) AS DirectorPlanFightArcTypeColumn FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.DirectorPlans') AND name=N'FightArcType';
GO
