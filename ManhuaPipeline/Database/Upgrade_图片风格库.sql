-- =============================================================
-- 图片风格库（无限画布节点「图片风格」下拉的数据源）
--
-- 为什么单独建表：以前画布的风格只能「跟随所属项目的画风」，不挂项目就没风格可用。
-- 现在改成一张独立的表，画布直接读它 —— 风格跟项目解耦，不挂项目也能挑。
--
-- 字段刻意只有三个：
--   · StyleName     画布下拉里显示的就是它，用户按名字挑
--   · StyleDesc     出图时整段拼进提示词（"画面风格：…"），风格的实际内容
--   · StyleImageUrl 只作预览缩略图，让挑风格时能看着图挑；不送进模型，
--                   免得和节点上游连进来的参考图抢名额、把出图行为搞复杂
--
-- 与 VideoStyles 一样是全局共享的（不带 UserId）：风格属于系统配置层面的东西，
-- 跟「谁在建」关系不大。
-- =============================================================

IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='ImageStyles' AND xtype='U')
BEGIN
    CREATE TABLE ImageStyles (
        StyleId       INT           IDENTITY PRIMARY KEY,
        StyleName     NVARCHAR(200) NOT NULL,                 -- 风格名称
        StyleDesc     NVARCHAR(MAX) NOT NULL,                 -- 风格描述（拼进提示词的那段）
        StyleImageUrl NVARCHAR(512) NULL,                     -- 风格图片（仅预览，不送模型）
        CreatedAt     DATETIME2     NOT NULL CONSTRAINT DF_ImageStyles_Created DEFAULT SYSDATETIME(),
        UpdatedAt     DATETIME2     NOT NULL CONSTRAINT DF_ImageStyles_Updated DEFAULT SYSDATETIME()
    );
END
