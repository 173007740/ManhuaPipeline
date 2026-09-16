-- ============================================================
-- L3 关键帧层：每个剧情节点一张关键帧（8-16 张/集）
-- 目的（对标 short-drama-agent 第 15 节）：
--   把「角色站位 / 道具状态 / 场景朝向 / 线索可见性 / 镜头衔接」在剧情节点上定死，
--   作为分镜与视频生成之间的锚点，避免后续镜头站位漂移、道具状态倒退、线索提前暴露。
-- 与资产卡的「出图」是两条不同产线：关键帧是**方案层**（提示词 + 锁定项），
-- 本版本不进出图队列，只做方案落库 + 阶段 9 锚定注入。
-- 生成：人工触发（项目页「关键帧」页的「生成关键帧方案」按钮）；
-- 消费：阶段 9（提示词）按集注入【关键帧锚定】——未生成关键帧的项目行为与以前完全一致。
-- 需手工执行一次：ManhuaPipeline/Database/Upgrade_关键帧层.sql
-- ============================================================
IF OBJECT_ID(N'dbo.ProjectKeyframes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectKeyframes (
        KeyframeId           INT IDENTITY(1,1) NOT NULL,
        ProjectId            INT NOT NULL,
        EpisodeNumber        INT NOT NULL DEFAULT(0),   -- 0 = 全片级节点；>0 = 该集级
        SortOrder            INT NOT NULL DEFAULT(0),
        NodeLabel            NVARCHAR(100) NULL,        -- 剧情节点标签：开场钩子/首次揭示/道具状态变化/收尾悬念
        NodeReason           NVARCHAR(400) NULL,        -- 选该节点的理由（信息增量/情绪转折/因果拐点）
        ShotLabel            NVARCHAR(40) NULL,         -- 锚定的分镜镜头编号，如 2.3-1
        UnitNumber           NVARCHAR(40) NULL,         -- 所属单元编号，如 2.3
        Composition          NVARCHAR(1000) NULL,       -- 构图：机位/景别/主体位置
        LockedCharacters     NVARCHAR(1000) NULL,       -- 锁定的角色站位与朝向
        LockedProps          NVARCHAR(1000) NULL,       -- 锁定的道具状态
        LockedSceneDirection NVARCHAR(1000) NULL,       -- 锁定的场景空间与朝向
        ClueVisible          NVARCHAR(500) NULL,        -- 线索可见性（必须看到/必须看不到）
        NextConnection       NVARCHAR(1000) NULL,       -- 与下一张关键帧的衔接
        ImagePrompt          NVARCHAR(MAX) NULL,        -- 关键帧图像提示词（文生图用）
        Status               NVARCHAR(20) NOT NULL DEFAULT(N'draft'),  -- draft / accepted
        Source               NVARCHAR(20) NOT NULL DEFAULT(N'llm'),    -- llm / fallback
        CreatedAt            DATETIME2(0) NOT NULL DEFAULT(SYSDATETIME()),
        UpdatedAt            DATETIME2(0) NOT NULL DEFAULT(SYSDATETIME()),
        CONSTRAINT PK_ProjectKeyframes PRIMARY KEY (KeyframeId)
    );
    CREATE INDEX IX_ProjectKeyframes_Project ON dbo.ProjectKeyframes (ProjectId, EpisodeNumber, SortOrder);
END
GO
