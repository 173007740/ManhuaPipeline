-- =============================================================
-- 无限画布（节点连线式出图画布）
--
-- 三张主表 + 一张后台出图队列表。设计要点：
--   · CanvasBoards.ProjectId 可空 —— 既能挂到某个项目下，也能作为不挂项目的独立灵感板。
--   · 节点类型与输入/输出端口由代码层的 CanvasNodeCatalog 定义，不写死在数据库里；
--     新增一种节点（例如以后接 ComfyUI 工作流）只改代码，不用改表。
--   · CanvasNodes.ExtraJson 预留给节点专属参数（如 seed / steps / 工作流 id），
--     避免每加一种节点就加一列。
--   · 出图走后台队列（CanvasTasks）：关页面、刷新、换设备都会跑完，
--     与 AssetImageTasks（资产卡出图）同一套思路。
-- =============================================================

-- 画布
IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='CanvasBoards' AND xtype='U')
BEGIN
    CREATE TABLE CanvasBoards (
        Id            INT IDENTITY PRIMARY KEY,
        UserId        INT           NOT NULL,
        ProjectId     INT           NULL,            -- 可空：独立灵感板
        Title         NVARCHAR(200) NOT NULL CONSTRAINT DF_CanvasBoards_Title DEFAULT N'未命名画布',
        ViewportX     REAL          NOT NULL CONSTRAINT DF_CanvasBoards_VX DEFAULT 0,
        ViewportY     REAL          NOT NULL CONSTRAINT DF_CanvasBoards_VY DEFAULT 0,
        ViewportScale REAL          NOT NULL CONSTRAINT DF_CanvasBoards_VS DEFAULT 1,
        CreatedAt     DATETIME2     NOT NULL CONSTRAINT DF_CanvasBoards_Created DEFAULT SYSDATETIME(),
        UpdatedAt     DATETIME2     NOT NULL CONSTRAINT DF_CanvasBoards_Updated DEFAULT SYSDATETIME()
    );
    CREATE INDEX IX_CanvasBoards_User    ON CanvasBoards(UserId);
    CREATE INDEX IX_CanvasBoards_Project ON CanvasBoards(ProjectId);
END

-- 节点
IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='CanvasNodes' AND xtype='U')
BEGIN
    CREATE TABLE CanvasNodes (
        Id        INT IDENTITY PRIMARY KEY,
        BoardId   INT           NOT NULL,
        NodeType  NVARCHAR(32)  NOT NULL,            -- text2image / image2image / asset ...（见 CanvasNodeCatalog）
        X         REAL          NOT NULL,            -- 画布坐标（非屏幕坐标，随视口一起变换）
        Y         REAL          NOT NULL,
        Title     NVARCHAR(100) NULL,
        Prompt    NVARCHAR(MAX) NULL,
        Size      NVARCHAR(16)  NULL,                -- 1280x720 / 1024x1024 / 864x1152，空则取类型默认
        Status    NVARCHAR(16)  NOT NULL CONSTRAINT DF_CanvasNodes_Status DEFAULT 'idle', -- idle|queued|running|done|failed
        ImageUrl  NVARCHAR(512) NULL,                -- /uploads/canvas/{boardId}/xxx.png
        AssetId   INT           NULL,                -- asset 节点引用的素材
        ErrorMsg  NVARCHAR(MAX) NULL,
        ExtraJson NVARCHAR(MAX) NULL,                -- 节点专属扩展参数，新增节点类型不用加列
        CreatedAt DATETIME2     NOT NULL CONSTRAINT DF_CanvasNodes_Created DEFAULT SYSDATETIME(),
        UpdatedAt DATETIME2     NOT NULL CONSTRAINT DF_CanvasNodes_Updated DEFAULT SYSDATETIME()
    );
    CREATE INDEX IX_CanvasNodes_Board ON CanvasNodes(BoardId);
END

-- 连线（输出端口 → 输入端口）
IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='CanvasEdges' AND xtype='U')
BEGIN
    CREATE TABLE CanvasEdges (
        Id         INT IDENTITY PRIMARY KEY,
        BoardId    INT           NOT NULL,
        FromNodeId INT           NOT NULL,
        FromPort   NVARCHAR(32)  NOT NULL,           -- 目前固定 'out'，保留字段名以便将来多输出端口
        ToNodeId   INT           NOT NULL,
        ToPort     NVARCHAR(32)  NOT NULL,           -- 目前固定 'ref'（可接多条 = 多张参考图）
        CreatedAt  DATETIME2     NOT NULL CONSTRAINT DF_CanvasEdges_Created DEFAULT SYSDATETIME()
    );
    CREATE INDEX IX_CanvasEdges_Board ON CanvasEdges(BoardId);
    CREATE INDEX IX_CanvasEdges_To    ON CanvasEdges(ToNodeId);
    CREATE INDEX IX_CanvasEdges_From  ON CanvasEdges(FromNodeId);
END

-- 出图任务队列（后台执行，关页面也跑完）
IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='CanvasTasks' AND xtype='U')
BEGIN
    CREATE TABLE CanvasTasks (
        TaskId     INT IDENTITY PRIMARY KEY,
        BoardId    INT           NOT NULL,
        NodeId     INT           NOT NULL,
        UserId     INT           NOT NULL,
        Status     NVARCHAR(16)  NOT NULL CONSTRAINT DF_CanvasTasks_Status DEFAULT 'queued', -- queued|running|completed|failed
        ErrorMsg   NVARCHAR(MAX) NULL,
        CreatedAt  DATETIME2     NOT NULL CONSTRAINT DF_CanvasTasks_Created DEFAULT SYSDATETIME(),
        StartedAt  DATETIME2     NULL,
        FinishedAt DATETIME2     NULL
    );
    CREATE INDEX IX_CanvasTasks_Queue ON CanvasTasks(Status, TaskId);
    CREATE INDEX IX_CanvasTasks_Board ON CanvasTasks(BoardId);
END
