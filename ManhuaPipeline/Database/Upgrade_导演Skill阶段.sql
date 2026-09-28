-- =============================================================
-- 导演流水线的「阶段定义」落成数据
--
-- 继 DirectorSkillDocs（规则可编辑）之后的第二块地基。
-- 光有规则可编辑还不够：跑到哪一步、每步问用户什么、产出什么格式、什么条件下放行，
-- 这些如果写死在 C# 里，改一次流程还是要发版。所以阶段本身也进库。
--
-- 三张表合起来就完整定义了「跑什么」：
--   DirectorSkillPacks   包
--   DirectorSkillDocs    规则正文（说什么）
--   DirectorSkillStages  阶段编排（什么时候说、要什么、产出什么、什么能放行）
--
-- 引擎（后续 DirectorAgentService）只做四件事：按阶段取规则 → 拼 prompt → 调 LLM → 解析落库。
-- 它不认识"仙侠""4 View""七段式"这些概念，全在下面这几行数据里。
--
-- InputsJson（该阶段要向用户收集什么）：
--   [{"key":"model","label":"模型","type":"select","options":["Seedance 2.0","Seedance 2.5"],"required":true}]
--   type 支持 text / textarea / select / number / list
-- DocFilter（本阶段额外加载哪些规则）：
--   逗号分隔的 scope 表达式，如 "always,stage:P2,when:战斗"；always 由引擎兜底永远带
-- HumanConfirm=1 的阶段是硬门禁：引擎必须停下来等人在页面上点确认，不能自动往下跑。
-- =============================================================

IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='DirectorSkillStages' AND xtype='U')
BEGIN
    CREATE TABLE DirectorSkillStages (
        StageId         INT           IDENTITY PRIMARY KEY,
        PackId          INT           NOT NULL,
        StageKey        NVARCHAR(32)  NOT NULL,          -- P0 / P1 / P2a / P2b / P2c / P2d / P3 / P4 / P5
        Name            NVARCHAR(100) NOT NULL,
        SortOrder       INT           NOT NULL,
        DocFilter       NVARCHAR(200) NULL,              -- 本阶段额外加载的规则范围
        InputsJson      NVARCHAR(MAX) NULL,              -- 立项/输入表单字段定义
        OutputContract  NVARCHAR(MAX) NULL,              -- 产出契约：给 LLM 的格式要求，也是解析依据
        Gates           NVARCHAR(MAX) NULL,              -- 门禁规则（含人工确认说明）
        HumanConfirm    BIT           NOT NULL CONSTRAINT DF_DSS_Confirm DEFAULT 0,
        IsEnabled       BIT           NOT NULL CONSTRAINT DF_DSS_Enabled DEFAULT 1,
        CreatedAt       DATETIME2     NOT NULL CONSTRAINT DF_DSS_Created DEFAULT SYSDATETIME(),
        UpdatedAt       DATETIME2     NOT NULL CONSTRAINT DF_DSS_Updated DEFAULT SYSDATETIME()
    );
    CREATE INDEX IX_DirectorSkillStages_Pack ON DirectorSkillStages(PackId, SortOrder);
END

-- 初始阶段定义：照 short-drama-director V6.8 的 Asset-First 六阶段铺一遍
DECLARE @pk INT;
SELECT @pk = PackId FROM DirectorSkillPacks WHERE PackKey = N'short-drama-director';
IF @pk IS NULL
BEGIN
    PRINT '未找到 short-drama-director 包，先跑 Scripts/ImportSkillPack.py';
END
ELSE IF NOT EXISTS (SELECT 1 FROM DirectorSkillStages WHERE PackId = @pk)
BEGIN
    INSERT INTO DirectorSkillStages(PackId, StageKey, Name, SortOrder, DocFilter, InputsJson, OutputContract, Gates, HumanConfirm) VALUES

    (@pk, N'P0', N'立项锁定', 10, N'always',
     N'[{"key":"model","label":"模型","type":"select","options":["Seedance 2.0","Seedance 2.5"],"required":true,"tip":"不支持默认值，必须二选一"},
        {"key":"aspect","label":"画幅","type":"select","options":["16:9 横屏","9:16 竖屏","21:9 超宽"],"required":true},
        {"key":"delivery","label":"交付形态","type":"select","options":["Markdown + 离线看板","纯 Markdown"],"required":true},
        {"key":"genre","label":"题材","type":"text","required":true},
        {"key":"episodeCount","label":"总集数","type":"number","required":true},
        {"key":"episodeDuration","label":"单集时长（秒）","type":"number","required":true},
        {"key":"platform","label":"目标平台","type":"text","required":true},
        {"key":"premise","label":"核心设定","type":"textarea","required":true},
        {"key":"characters","label":"角色","type":"list","required":true,"tip":"逗号分隔，如：赵日天，刘如烟"}]',
     N'产出「P0A 创作基准」表 + 前置锁定清单（模型/画幅/交付/题材/集数/时长/平台全部回填确认）。
单集三幕比例 30/50/20；连续剧须给出分集卡点与结尾 Cliffhanger。',
     N'前置铁律 A~E 未全部锁定，禁止进入 P1。', 0),

    (@pk, N'P1', N'剧本五阶门控', 20, N'always,stage:P1',
     N'[{"key":"premise","label":"一句话前提","type":"textarea","required":true},
        {"key":"episodeCount","label":"总集数","type":"number","required":true},
        {"key":"episodeDuration","label":"单集时长（秒）","type":"number","required":true}]',
     N'产出：故事前提 → 人物关系 → 全剧梗概 → 第 N 集剧本（md）。
剧本页头固定四行：【题材】【平台】【总集数】【目标时长】。
场号格式：[场景序号][内/外]·[地点]-[日/夜]。',
     N'Gate1 前提公式（主角+核心欲望+不可抗力阻碍+失败的致命代价）→
Gate2 宏观三幕比例与分集卡点（连续剧必须有 Cliffhanger）→
Gate3 因果节拍（禁巧合推进）→
Gate4 实体边界（新实体须可资产化）→
Gate5 剧本页格式与语速自检。
任一 Gate 不过，回退重写，不进入下一步。', 0),

    (@pk, N'P2a', N'实体提取', 30, N'always,stage:P2',
     N'[{"key":"script","label":"剧本","type":"textarea","required":true}]',
     N'产出资产清单：CHR- / SCN- / PRP- / VFX- / AUD- 编码台账，
每项标注是否跨镜/跨集复用（决定要不要出资产图）。',
     N'禁止把"名字+装扮"当资产；未标注复用范围不得进下一步。', 0),

    (@pk, N'P2b', N'资产清单确认', 40, N'always,stage:P2',
     N'[]',
     N'把 P2a 清单摆给用户逐项确认（增/删/降级 A~C 级）。',
     N'【硬门禁 1】清单未经确认，禁止出图。', 1),

    (@pk, N'P2c', N'分批出图提示词', 50, N'always,stage:P2,when:战斗',
     N'[{"key":"batch","label":"批次","type":"select","options":["第一批 角色 4 View","第二批 场景","第三批 道具与特效"],"required":true}]',
     N'默认只出提示词，不直接出图。
4 View 严格版式：左侧唯一带头面部特写，右侧无头正面/90°侧面/背面三联全身。
每项给中英文正式提示词 + 负面约束。',
     N'【硬门禁 2】分批逐批确认，禁止把"开始 P2 出图"当默认动作；
只有用户明确逐次下出图口令才真的出图。', 1),

    (@pk, N'P2d', N'资产图册', 60, N'always,stage:P2',
     N'[]',
     N'产出《XXX_资产图册_LOCKED.md》：资产板清单 + 一致性描述 + 分集单元执行表。',
     N'【硬门禁 3】全部资产未锁定，禁止进入 P3 分镜。', 0),

    (@pk, N'P3', N'分镜调度', 70, N'always,stage:P3,when:战斗',
     N'[{"key":"script","label":"剧本","type":"textarea","required":true},
        {"key":"lockedAssets","label":"已锁定资产","type":"textarea","required":true}]',
     N'产出分镜 md：镜号 / 景别 / 机位 / 180°轴线与轴线侧标注 / 站位四要素 / 镜长（由剧情倒推，禁机械等分）。
引用资产一律用 @锚点（@CHR-xxx / @SCN-xxx / @PRP-xxx）。',
     N'【硬门禁 4】分镜引用的锚点若没有对应已锁定资产图 → 阻断回补，不得放行。', 0),

    (@pk, N'P4', N'投喂提示词', 80, N'always,stage:P4',
     N'[{"key":"storyboard","label":"分镜","type":"textarea","required":true}]',
     N'七段式：画幅风格 → 场景资产 → 核心人物 → 站位声明 → 时间轴分镜（末尾强制【接续状态】尾行）→ 音效 → 强制禁止项。
首行句尾写 model=seedance-2.0；末尾参数行已废止，禁写。
单段 ≤15s（2.0）/ ≤30s（2.5），镜长由剧情倒推。',
     N'【硬门禁 5】画幅/脸型/服装与已锁定资产图不一致 → 阻断回 P0。
机检 C1~C13：段长越界、镜数与单镜时长、时间码倒挂、字符数、2.0 禁 [SFX:]、画幅回填、
七段式缺栏、2.0 全面禁首尾帧、平台高危词、单镜 >6s、缺接续状态、否定式硬删除、镜长雷同。', 0),

    (@pk, N'P5', N'质检交付', 90, N'always,stage:P4',
     N'[{"key":"prompts","label":"投喂提示词","type":"textarea","required":true}]',
     N'产出质检报告：P0 前置一致性 / P1 叙事结构 / P2 资产与空间 / P4 机检，分级 WARN/BLOCK/FAIL。',
     N'【硬门禁 6】改稿未跑三步复验 + 《改动复验报告》，禁止交付。', 1);

    PRINT 'director skill stages seeded.';
END
