SET NOCOUNT ON;
/* 5648 修正：笔记本落位方向锚点
   问题：原文只写「笔记本底部精准贴合承托面」，未指定正反面与轴向，模型自由发挥导致倒放。
   修法：统一给出四个方位锚点 —— 顶盖面朝上 / 底面（脚垫面）朝下 / 转轴侧朝画面深处 / 前缘朝观众抵住挡托，并显式禁止翻转倒置。 */

/* 1) summary 补朝向 */
UPDATE SeedancePrompts SET
 PromptText   = REPLACE(PromptText,   N'双手将 <Subject 3> 平稳放置于 <Subject 2> 上的产品落位特写', N'双手将 <Subject 3> 以顶盖面朝上、底面朝下、屏幕转轴侧朝向画面深处的正向姿态平稳放置于 <Subject 2> 上的产品落位特写'),
 PromptTextH3 = REPLACE(PromptTextH3, N'双手将 <Subject 3> 平稳放置于 <Subject 2> 上的产品落位特写', N'双手将 <Subject 3> 以顶盖面朝上、底面朝下、屏幕转轴侧朝向画面深处的正向姿态平稳放置于 <Subject 2> 上的产品落位特写')
WHERE PromptId = 5648;

/* 2) retention_analysis 里 Subject 3 的保真描述补朝向锁定 */
UPDATE SeedancePrompts SET
 PromptText   = REPLACE(PromptText,   N'<Subject 3>（出现在 [Shot 1]、[Shot 2]）：fully_preserved - consistent with <Picture 3>；由手持入画至精准贴合于 <Subject 2> 上方。', N'<Subject 3>（出现在 [Shot 1]、[Shot 2]）：fully_preserved - consistent with <Picture 3>；全程保持正向摆放——顶盖面朝上、底面（脚垫面）朝下，屏幕转轴侧朝向画面深处、前缘朝向观众，不翻转、不倒置、不侧立；由手持入画至精准贴合于 <Subject 2> 上方。'),
 PromptTextH3 = REPLACE(PromptTextH3, N'<Subject 3>（出现在 [Shot 1]、[Shot 2]）：fully_preserved - consistent with <Picture 3>；由手持入画至精准贴合于 <Subject 2> 上方。', N'<Subject 3>（出现在 [Shot 1]、[Shot 2]）：fully_preserved - consistent with <Picture 3>；全程保持正向摆放——顶盖面朝上、底面（脚垫面）朝下，屏幕转轴侧朝向画面深处、前缘朝向观众，不翻转、不倒置、不侧立；由手持入画至精准贴合于 <Subject 2> 上方。')
WHERE PromptId = 5648;

/* 3) [Shot 1] 持握与下放：锁死机身轴向 */
UPDATE SeedancePrompts SET
 PromptText   = REPLACE(PromptText,   N'<Subject 1> 双手从画外右上方持握 <Subject 3> 缓慢进入画面，以约四十五度角匀速下放，镜头同步极缓推近聚焦于落位区域。', N'<Subject 1> 双手从画外右上方持握 <Subject 3> 缓慢进入画面，持握时即为正向：顶盖面朝上、底面（脚垫面）朝下、屏幕转轴侧朝向画面深处、前缘（开合边）朝向观众，机身全程不自转、不翻转、不侧立；以约四十五度角匀速下放，镜头同步极缓推近聚焦于落位区域。'),
 PromptTextH3 = REPLACE(PromptTextH3, N'<Subject 1> 双手从画外右上方持握 <Subject 3> 缓慢进入画面，以约四十五度角匀速下放，镜头同步极缓推近聚焦于落位区域。', N'<Subject 1> 双手从画外右上方持握 <Subject 3> 缓慢进入画面，持握时即为正向：顶盖面朝上、底面（脚垫面）朝下、屏幕转轴侧朝向画面深处、前缘（开合边）朝向观众，机身全程不自转、不翻转、不侧立；以约四十五度角匀速下放，镜头同步极缓推近聚焦于落位区域。')
WHERE PromptId = 5648;

/* 4) [Shot 1] 落位接触：把「底部」这种歧义词换成明确的底面+前缘 */
UPDATE SeedancePrompts SET
 PromptText   = REPLACE(PromptText,   N'笔记本底部精准贴合 <Subject 2> 承托面，接触瞬间', N'<Subject 3> 底面（带脚垫的一面）精准贴合 <Subject 2> 承托面，前缘抵住支架前挡托，顶盖面朝上正对镜头可见，接触瞬间'),
 PromptTextH3 = REPLACE(PromptTextH3, N'笔记本底部精准贴合 <Subject 2> 承托面，接触瞬间', N'<Subject 3> 底面（带脚垫的一面）精准贴合 <Subject 2> 承托面，前缘抵住支架前挡托，顶盖面朝上正对镜头可见，接触瞬间')
WHERE PromptId = 5648;

/* 5) [Shot 2] 落位后保向 */
UPDATE SeedancePrompts SET
 PromptText   = REPLACE(PromptText,   N'随后双手沿进入路径平稳退出画外，', N'<Subject 3> 全程保持已落位的正向姿态不变，不得翻转或倒置。随后双手沿进入路径平稳退出画外，'),
 PromptTextH3 = REPLACE(PromptTextH3, N'随后双手沿进入路径平稳退出画外，', N'<Subject 3> 全程保持已落位的正向姿态不变，不得翻转或倒置。随后双手沿进入路径平稳退出画外，')
WHERE PromptId = 5648;

/* 6) 风格锁尾部追加一条画面级禁止项，作为双保险 */
UPDATE SeedancePrompts SET
 PromptText   = REPLACE(PromptText,   N'no subtitles, no watermarks, no character-name overlays', N'no subtitles, no watermarks, no character-name overlays. <Subject 3> 全程正放：顶盖面朝上、底面贴合 <Subject 2> 承托面，禁止出现倒置、底朝天、屏幕朝下、前后颠倒或侧立的机位'),
 PromptTextH3 = REPLACE(PromptTextH3, N'no subtitles, no watermarks, no character-name overlays', N'no subtitles, no watermarks, no character-name overlays. <Subject 3> 全程正放：顶盖面朝上、底面贴合 <Subject 2> 承托面，禁止出现倒置、底朝天、屏幕朝下、前后颠倒或侧立的机位')
WHERE PromptId = 5648;

/* 7) overall_soundscape 里的「笔记本底部」同步改为「底面（脚垫面）」，与画面描述一致 */
UPDATE SeedancePrompts SET
 PromptText   = REPLACE(PromptText,   N'笔记本底部贴合金属承托面时一声短促而克制的咔嗒轻响', N'笔记本底面（脚垫面）贴合金属承托面时一声短促而克制的咔嗒轻响'),
 PromptTextH3 = REPLACE(PromptTextH3, N'笔记本底部贴合金属承托面时一声短促而克制的咔嗒轻响', N'笔记本底面（脚垫面）贴合金属承托面时一声短促而克制的咔嗒轻响')
WHERE PromptId = 5648;

SELECT PromptId,
       LEN(PromptText) AS LenText,
       LEN(PromptTextH3) AS LenH3,
       CASE WHEN PromptText LIKE N'%顶盖面朝上%' THEN 'Y' ELSE 'N' END AS TextOk,
       CASE WHEN PromptTextH3 LIKE N'%顶盖面朝上%' THEN 'Y' ELSE 'N' END AS H3Ok,
       CASE WHEN PromptText LIKE N'%笔记本底部%' THEN 'Y' ELSE 'N' END AS LeftOldText,
       CASE WHEN PromptTextH3 LIKE N'%笔记本底部%' THEN 'Y' ELSE 'N' END AS LeftOldH3
FROM SeedancePrompts WHERE PromptId = 5648;
