/* 一次性数据回填：漫剧 25（苔岬原创动漫项目-番外日常）——补立项 + 补 12 集

   背景：立项（P0）是在项目 57 上跑的（RunId=15），那时漫剧级运行的入口还没接上，
   产出都留在 DirectorSkillRunSteps 里，Dramas 那一行却一个立项字段都没填。
   于是漫剧 25 的立项面板全空、12 集也只有一个项目 57（还没集号）。

   这个脚本把已经跑出来的东西搬回该在的位置，不重新调模型：
     ① 立项字段 ← RunId=15 的 P0 输入 + P0 产出里的锁定表
     ② 分集提纲 ← P0 产出里的「12 集分集卡点 + Cliffhanger」表（与 EpisodeOutlineParser 同结构）
     ③ 单集项目 ← 按提纲建 2~12 集，并把已有的项目 57 归位成第 1 集

   为什么用 SQL 而不是点页面按钮：页面只有「解析分集表」和「跑立项并生成分集」，
   后者会重新调一次 P0（几分钟）只为建项目——产出早就有了，没必要再跑。
   「建项目」这个动作对应的是 /api/drama/{id}/episodes/build，页面没暴露按钮，
   这里按 BuildEpisodeProjects 的同一套规则复刻（集号已存在就更新标题与简介，不重复建）。

   可重跑：集号用 EpisodeNumber 判重，跑第二次只会把标题简介刷成同一份。 */

/* sqlcmd 默认 QUOTED_IDENTIFIER OFF，写 EpisodeOutlineJson 那步会直接报 1934。 */
SET QUOTED_IDENTIFIER ON;

-- ============================================================
-- ① 立项字段
-- ============================================================

/* 提示词引擎顺手归一成 'H3'：库里原来存的是「H3（MiniMax·本地 ComfyUI）」，
   立项面板那个下拉只有 SD / H3 两个选项，值对不上时 jQuery 的 .val() 设不进去，
   用户一打开页面看到的是空的、随手一点保存就把 H3 存成了 SD。 */
UPDATE Dramas
SET Title            = N'苔岬原创动漫项目-番外日常',   -- 原名 '05'，是建漫剧时随手填的
    Aspect           = N'16:9 横屏',
    Delivery         = N'Markdown + 离线看板',
    Genre            = N'宫崎骏自愈系',
    Hook             = N'宫崎骏自愈系（手绘水彩质感 · 自然光 · 日常魔法感 · 无大反派 · 心灵修复弧线）',
    Premise          = N'宫崎骏自愈系——以“慢”为叙事节奏，以“自然/手艺/食物/风”为治愈介质，角色在微小日常中完成自我和解',
    Platform         = N'抖音',
    EpisodeCount     = 12,
    EpisodeDuration  = 180,
    CharactersJson   = N'["杨彦刚","刘如烟"]',
    PromptEngine     = N'H3',
    UpdatedAt        = SYSDATETIME()
WHERE DramaId = 25;

-- ============================================================
-- ② 分集提纲（照 RunId=15 的 P0 产出原样录入）
-- ============================================================

CREATE TABLE #E (episodeNumber int PRIMARY KEY, title nvarchar(200), outline nvarchar(max), cliffhanger nvarchar(max));

INSERT INTO #E VALUES
 (1,  N'回不去的屋顶',
      N'建置：杨彦刚拖着行李箱推开外婆老屋门；发展：试图自己修漏雨屋顶，摔下来，狼狈；收束：刘如烟递来一壶茶，不说一句话',
      N'他抬头——屋顶裂缝里，一只青鸟衔着半片旧瓦飞走'),
 (2,  N'靛蓝的指纹',
      N'建置：杨彦刚发现工坊染缸“被谁动过”；发展：刘如烟教他揉布，他笨拙把布搅浑；收束：他第一次安静看水波纹',
      N'染缸底部沉着一枚外婆的铜扣——她没解释'),
 (3,  N'风车不转了',
      N'建置：镇上老风车停转，杨彦刚想“修好它证明自己能行”；发展：爬上去发现轴承锈死，越急越拧不动；收束：刘如烟坐在风车下织布，说“它只是累了”',
      N'风车叶片突然无风自转半圈，发出外婆年轻时哼的调子'),
 (4,  N'一碗面',
      N'建置：杨彦刚试图复刻外婆的葱油面，面粉飞满厨房；发展：反复失败，摔碗，蹲在地上哭；收束：刘如烟默默把碗捡起来，重新下面',
      N'面汤里浮起一片他从未见过的干花瓣——不是这个季节的'),
 (5,  N'雨后的田',
      N'建置：暴雨后稻田倒伏，杨彦刚想去扶，被刘如烟拦住“让它自己站起来”；发展：他焦躁地来回踱步，最终坐在田埂上；收束：黄昏，稻穗真的慢慢直起',
      N'田埂尽头泥里露出一截旧木牌，刻着“彦”字'),
 (6,  N'铜扣的另一半',
      N'建置：杨彦刚追问铜扣来历；发展：刘如烟第一次主动讲——她曾与外婆学染，外婆走后她守工坊三年；收束：两人沉默共染一块布，颜色渐变',
      N'布染到一半，靛蓝里渗出一缕不该出现的朱红'),
 (7,  N'山那边的信号',
      N'建置：杨彦刚旧手机收到前公司“回来吧”的消息；发展：他犹豫、收拾行李、走到镇口又折返；收束：刘如烟在工坊门口等他，没问去哪',
      N'折返路上，他看见外婆老屋烟囱冒出烟——他走之前没生火'),
 (8,  N'不说话的夜',
      N'建置：停电，两人点蜡烛；发展：杨彦刚第一次讲城市里的崩溃，刘如烟只是听；收束：蜡烛燃尽，黑暗里只剩呼吸和虫鸣',
      N'黑暗中刘如烟说了一句：“我也是。”——语气极轻'),
 (9,  N'朱红色的布',
      N'建置：那块染坏的朱红布被杨彦刚捡回；发展：他试图还原外婆当年的配方，翻遍工坊笔记；收束：找到外婆手书——配方空白处画着一朵他认不出的花',
      N'笔记最后一页夹着一张旧照片：外婆年轻时，身边站着一个背影——与刘如烟一模一样'),
 (10, N'青鸟回来了',
      N'建置：杨彦刚按笔记去后山找那朵花；发展：迷路、跌倒、在溪边坐下，决定“不找了”；收束：起身时，那只第 1 集的青鸟落在肩头，嘴里衔着花',
      N'青鸟把花放在他掌心后，朝镇子方向飞——他第一次跟着鸟跑'),
 (11, N'染完最后一缸',
      N'建置：杨彦刚用那朵花 + 外婆配方，和刘如烟一起染完最后一缸布；发展：染的过程两人默契到无需言语，布展开是外婆老屋屋顶的图案；收束：他们把布挂在风车上，风车转了',
      N'布在风中翻飞，露出背面——外婆的笔迹：“刚，够了就停。”'),
 (12, N'够了就停',   -- P0 原文是「够了就停（终集）」，建项目时把终集括注剥掉（与 EpisodeOutlineParser 一致）
      N'建置：杨彦刚不再修屋顶，搬一把椅子坐在漏雨的屋里听雨；发展：刘如烟来，两人分一碗面，窗外雨停；收束：他推开窗，山那边有光。没有大团圆宣言，只是“今天天气不错”',
      N'终帧定格：窗台上，青鸟留下的那朵花，旁边压着铜扣——完整的一枚。画面渐白，无黑场，无字幕卡。');

/* 用 FOR JSON PATH 生成，不手写 json 串：正文里那些引号交给 SQL Server 转义，
   免得手拼的字符串一遇到引号就变成非法 json，读回来整份提纲都作废。 */
DECLARE @j nvarchar(max);
SELECT @j = (SELECT episodeNumber, title, outline, cliffhanger FROM #E ORDER BY episodeNumber FOR JSON PATH);

UPDATE Dramas SET EpisodeOutlineJson = @j, UpdatedAt = SYSDATETIME() WHERE DramaId = 25;

-- ============================================================
-- ③ 单集项目
-- ============================================================

/* 项目 57 是这个漫剧下唯一的项目（第 1 集跑完 P0~P4 那个），但 EpisodeNumber 是 NULL——
   BuildEpisodeProjects 判重只看「集号非空」的项目，不先归位的话它会新建第 1 集，
   结果同一个漫剧下出现两个第 1 集。先把它标成第 1 集。 */
UPDATE Projects SET EpisodeNumber = 1, UpdatedAt = SYSDATETIME()
WHERE ProjectId = 57 AND DramaId = 25 AND EpisodeNumber IS NULL;

-- 已存在的集号：只刷标题与简介（项目简介 = 三幕骨架 + 本集卡点，与 ComposeProjectDescription 同格式）
UPDATE p
SET Title       = e.title,
    Description = e.outline + CHAR(13) + CHAR(10) + N'【本集卡点】' + e.cliffhanger,
    UpdatedAt   = SYSDATETIME()
FROM Projects p
JOIN #E e ON e.episodeNumber = p.EpisodeNumber
WHERE p.DramaId = 25 AND p.UserId = 1 AND p.Status <> N'deleted';

-- 还没有的集号：新建
INSERT INTO Projects (UserId, DramaId, Title, Description, EpisodeCount, ProjectType, EpisodeNumber)
SELECT 1, 25, e.title,
       e.outline + CHAR(13) + CHAR(10) + N'【本集卡点】' + e.cliffhanger,
       1, N'drama', e.episodeNumber
FROM #E e
WHERE NOT EXISTS (SELECT 1 FROM Projects p
                  WHERE p.DramaId = 25 AND p.UserId = 1 AND p.Status <> N'deleted'
                    AND p.EpisodeNumber = e.episodeNumber);

DROP TABLE #E;

-- ============================================================
-- 确认
-- ============================================================
SELECT DramaId, Title, Aspect, Platform, EpisodeCount, EpisodeDuration, PromptEngine,
       LEN(EpisodeOutlineJson) AS OutlineLen
FROM Dramas WHERE DramaId = 25;

SELECT EpisodeNumber, ProjectId, Title FROM Projects WHERE DramaId = 25 ORDER BY EpisodeNumber;
