-- =============================================================
-- 版本记录初始补录
--
-- 两类条目，务必区分看待：
--
--   Author = 'AI·推算' —— AI 接手后的改动从未提交过 git（长期堆在工作区，
--       只按「新增文件 + 建表脚本」反推出功能线）。ReleasedAt 记的是补录当天，
--       不是真实改动日，顺序也未必严格等于真实先后。只能看功能脉络，不能当精确改动史。
--
--   Author = 'AI'      —— 2026-09-27 起会话内有完整上下文，条目与日期都准确。
--
-- 从今往后每次改动插一条，Author 一律 'AI'，ReleasedAt 默认取当前时间。
-- =============================================================
SET NOCOUNT ON;

-- 以最新一条作为「是否已补录过」的判断依据，脚本可重复执行，不会灌出重复版本
IF NOT EXISTS (SELECT 1 FROM VersionLogs WHERE Version = N'v1.18.0')
BEGIN

INSERT INTO VersionLogs(Version, Title, Category, Content, ReleasedAt, Author, IsPublished) VALUES

-- ---------- 以下为反推补录 ----------
(N'v1.1.0', N'无限画布', N'feature', N'- 新增无限画布：节点式创作板，多画布并存
- 节点可单独指定出图渠道，不指定时走全局默认
- 出图任务队列 + 实时事件推送（CanvasImageQueueService / CanvasEventHub）
- 依据：canvas.html、CanvasController、DbService.Canvas、Upgrade_无限画布.sql', '2026-09-05 10:00', N'AI·推算', 1),

(N'v1.2.0', N'图片风格库', N'feature', N'- 新增独立图片风格表 ImageStyles，风格与所属项目解耦
- 不挂项目的独立灵感板也能挑风格
- 风格图只作预览缩略图，不送进模型
- 依据：image-styles.html、ImageStyleController、Upgrade_图片风格库.sql', '2026-09-08 10:00', N'AI·推算', 1),

(N'v1.3.0', N'出图渠道多配置', N'feature', N'- 出图渠道改为可配多条并存（中转 / 方舟 / 302 等），可指定哪条是全局默认
- 资产卡出图用默认渠道，画布节点可逐节点改
- 模型名改为分组预设下拉 + 自定义手填，避免填错 model id
- 依据：Upgrade_图片模型多配置.sql', '2026-09-11 10:00', N'AI·推算', 1),

(N'v1.4.0', N'出图参数可配与自动降级', N'optimize', N'- 出图支持质量 / 背景 / 输出格式三项参数
- 中转站不认这些字段被 4xx 拒时逐级往下摘，换中转站不至于整站出不了图
- 依据：Upgrade_出图参数.sql、Models/ImageGenOptions.cs', '2026-09-14 10:00', N'AI·推算', 1),

(N'v1.5.0', N'视频像素档', N'feature', N'- 视频生成分辨率改为按档位选择
- 依据：Upgrade_视频像素档.sql', '2026-09-16 10:00', N'AI·推算', 1),

(N'v1.6.0', N'系统报告页', N'feature', N'- 新增系统报告：出图 / 视频 / Token 三块统计
- 「出图清单」列出最近节点与失败原因，用来定位哪张图出错了
- 依据：report.html、DashboardController、DbService.Dashboard', '2026-09-18 10:00', N'AI·推算', 1),

(N'v1.7.0', N'系统配置中心改版', N'refactor', N'- 系统配置改为「左侧分区 + 右侧内嵌页」结构
- 内嵌页自带的导航栏与粒子背景会被隐藏，避免重复一份
- 依据：system-config.html', '2026-09-20 10:00', N'AI·推算', 1),

(N'v1.8.0', N'音色库', N'feature', N'- 新增音色库：音色条目 + 封面图
- 依据：voice-library.html、VoiceLibraryController、Upgrade_音色库表.sql、Upgrade_音色库封面图.sql', '2026-09-22 10:00', N'AI·推算', 1),

(N'v1.9.0', N'项目类型与歌词轨', N'feature', N'- 项目增加类型字段，新增歌词轨数据结构
- 依据：Upgrade_项目类型与歌词轨.sql、Models/ProjectLyricLine.cs', '2026-09-23 10:00', N'AI·推算', 1),

(N'v1.10.0', N'打斗链路重构', N'refactor', N'- 打斗生成拆成独立模块：动作链 / 招式语法目录 / 意图映射 / 时长预算 / 招式弧 / 分镜打包
- 依据：Services/Combat/ 下 ActionChainBuilder、CombatGrammarCatalog、CombatIntentMapper、CombatTimeBudgetBuilder、FightArcCatalog、ShotPacker', '2026-09-24 10:00', N'AI·推算', 1),

(N'v1.11.0', N'补齐新表新字段', N'optimize', N'- 补齐前期迭代中遗漏的表与字段
- 依据：Upgrade_补齐新表新字段.sql', '2026-09-25 10:00', N'AI·推算', 1),

-- ---------- 以下为会话内确认 ----------
(N'v1.12.0', N'「系统配置」首项改名接口配置', N'optimize', N'- 系统配置左侧第一项由「API 配置」改名为「接口配置」
- config.html 内页标题同步改为「接口配置」，顶部导航入口保持「系统配置」不变', '2026-09-27 15:00', N'AI', 1),

(N'v1.13.0', N'资产提示词模版独立成页', N'refactor', N'- 资产提示词模版从接口配置页中拆出，独立为 asset-prompt-templates.html
- 四类（角色 / 道具 / 环境 / 特效）各自维护统一视觉风格、类别规则、负面提示词
- 接口不变（/api/config/asset-prompt-templates），只是换了维护入口，避免两处都能改', '2026-09-27 16:00', N'AI', 1),

(N'v1.14.0', N'画布移除「所属项目」', N'optimize', N'- 画布工具栏移除「所属项目」下拉：风格已改由独立图片风格表提供，不再跟随项目
- 新建画布不再挂项目；旧的 projectId 字段保留不删，避免历史数据出错', '2026-09-27 17:00', N'AI', 1),

(N'v1.15.0', N'出图模型下拉新增 Gemini 系列', N'feature', N'- 出图渠道的模型下拉新增 Gemini（香蕉 / nano banana）一组四个型号
- gemini-3.1-flash-image、gemini-3.1-flash-image-preview、gemini-3.1-pro-preview、gemini-3.8-flash
- 实测：中转对非 imagen 模型走 /images/generations 会报 convert_request_failed，自动降级 /chat/completions 可出图', '2026-09-27 18:00', N'AI', 1),

(N'v1.16.0', N'修复报告页时间差 8 小时', N'fix', N'- 现象：系统报告「出图清单」更新时间比实际早 8 小时
- 根因：CanvasBoards / CanvasNodes / CanvasEdges / CanvasTasks / ImageStyles 五张表默认存 UTC，其余表存服务器本地时间，读出后未经转换直接显示
- 修复：写入端改 SYSDATETIME()，7 个默认约束同步改，已有记录整体平移（偏移量按 GETUTCDATE 与 GETDATE 之差动态算，不写死 8）
- 依据：Upgrade_时间统一本地时间.sql', '2026-09-27 23:50', N'AI', 1),

(N'v1.17.0', N'版本记录', N'feature', N'- 新增 VersionLogs 表：每次改动钉一条（版本号 / 分类 / 正文 / 时间）
- 系统配置 → 版本记录：时间线倒序展示，分类标签区分新功能 / 修复 / 优化 / 重构
- 系统配置标题旁显示当前版本号（取最新一条）
- 只提供只读接口，记录由开发侧写入，页面上不能改
- 早期条目为反推补录，Author 标为「AI·推算」，日期是补录当天而非真实改动日', SYSDATETIME(), N'AI', 1),

(N'v1.18.0', N'启用 git 版本管理', N'optimize', N'- 此前所有改动都堆在工作区从未提交（51 个改动 + 36 个新增），一次误操作覆盖就无从回滚
- 建立基线提交，此后每次改动单独提交，改动史有据可查
- 与版本记录表互补：表给人看「每次改了什么」，git 给机器看「改了哪些行」', SYSDATETIME(), N'AI', 1);

PRINT 'version logs seeded.';

END
