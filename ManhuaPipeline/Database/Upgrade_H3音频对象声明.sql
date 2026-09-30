/* H3 音色参考：<Audio M> 补上对象声明，并把内心独白 OS 算进「开口」

   问题：P4 跑出来的 H3 提示词里，正文照写了「以参考 <Audio 1> 的音色和说话方式说道」，
   但【参考素材说明】只列了 @图片1~@图片3、首行「共 3 个输入素材」也只数图片——
   <Audio 1> 在提示词里没有任何声明，是个悬空引用。
   对照 <Picture N> 那条链是闭合的：@图片N（素材声明）→ <Picture N> → subject_definitions。
   <Audio M> 缺的正是源头这两环：素材声明行 + 定义节。

   同时补一条判定：镜头 1.1-1 分镜 dialogue 写「无」，模型据画面补了内心独白并写了 <d>，
   却以「未开口」为由没带 <Audio 1>——音色白配了。凡写出 <d> 的（含 OS / 旁白 / 画外音）一律算开口。

   改两处：规则库 DocId 40（H3 规范）+ P4 阶段契约。改完需重跑 P4 才生效。 */

-- ========== 1. H3 规范（DocId 40）==========

-- 1.1 六节结构：说明 audio_definitions 是条件节，不算凭空加章节
UPDATE DirectorSkillDocs
SET Content = REPLACE(Content,
    N'4. 输出固定为下面的六节英文结构，必须完整输出全部章节，禁止省略或增删章节。',
    N'4. 输出固定为下面的六节英文结构，必须完整输出全部章节，禁止省略或增删章节
   （audio_definitions 是唯一的条件节：本镜有【音色参考表】时插在 subject_definitions 之后，
   本镜无音色参考时整节省略，不得输出空的 audio_definitions:）。')
WHERE DocId = 40;

-- 1.2 【参考素材说明】补音频声明行：@音频M 与 @图片N 是两套编号
UPDATE DirectorSkillDocs
SET Content = REPLACE(Content,
    N'只输出素材清单本身，禁止输出以上任何规则、说明或示例文字。',
    N'只输出素材清单本身，禁止输出以上任何规则、说明或示例文字。

（【音色参考音频声明·硬约束】系统给出【音色参考表】时，@图片N 行之后必须续写音频声明行，
M 与表内编号严格一致、顺序照抄：

@音频M [角色名]音色参考，保持音色与说话方式一致；

音频是独立于图片的输入素材：@音频M 与 @图片N 是两套编号，互不换算，音频不占用 @图片N 的号；
首行「共 N 个输入素材」的 N 只数图片，音频不计入，也禁止把音频混进 @图片N 序列。
漏掉这一行，正文里的 <Audio M> 就没有对应的输入素材，模型无从知道它指谁。）')
WHERE DocId = 40;

-- 1.3 新增 audio_definitions: 节（插在 subject_definitions 之后、summary 之前）
-- 锚点必须连着上一行的 summary: 一起匹配：只拿 [reference generation] 当锚点会插到 summary: 之后，
-- 多出一个孤立的 summary: 行。库里存的是 CRLF，拼接 CHAR(13)+CHAR(10) 才能精确命中。
UPDATE DirectorSkillDocs
SET Content = REPLACE(Content,
    N'summary:' + CHAR(13) + CHAR(10) + N'[reference generation] 目标视频展示 [一句话概括：主体、核心动作、使用的参考关系]。',
    N'audio_definitions:
<Audio 1> is the voice of [角色名]（音色参考：只提供音色与说话方式，不复制参考音频里的原话）.
（本节只在系统给出【音色参考表】时输出，每个 <Audio M> 一行，M 与 @音频M 声明行严格一致；
本镜无音色参考时整节省略，禁止输出空的 audio_definitions:。
本节是 <Audio M> 的唯一定义处——没有它，正文里的 <Audio M> 就是悬空引用。）

summary:
[reference generation] 目标视频展示 [一句话概括：主体、核心动作、使用的参考关系]。')
WHERE DocId = 40;

-- 1.4 音色编号铁律：补「开口」判定，OS / 旁白 / 画外音一律算开口
UPDATE DirectorSkillDocs
SET Content = REPLACE(Content,
    N'（最多 3 段音色参考，由系统注入，禁止超出）',
    N'（最多 3 段音色参考，由系统注入，禁止超出）

【开口判定】只要该镜写出了 <d> 标签，该角色即视为开口，说话句必须带 <Audio M>——
内心独白（OS）、旁白、画外音都算开口，不因「不是对别人说话」而豁免。
分镜 dialogue 写「无」、但正文据画面补出内心独白的，同样按开口处理，必须带 <Audio M>。
只有全程沉默、不写 <d> 的角色才不引用 <Audio M>。')
WHERE DocId = 40;

-- 1.5 转换规则第 6 条同步：无台词才不引用，OS 算台词
UPDATE DirectorSkillDocs
SET Content = REPLACE(Content,
    N'6. 该镜头无台词时：不写 <d> 标签、不分配说话人 ID。',
    N'6. 该镜头无台词时：不写 <d> 标签、不分配说话人 ID、不引用 <Audio M>。
   内心独白（OS）与画外音算台词，按开口处理：<d> 照写、说话人 ID 照分配、<Audio M> 照带。')
WHERE DocId = 40;

-- ========== 2. P4 阶段契约：H3 那句补上声明要求 ==========

UPDATE DirectorSkillStages
SET OutputContract = REPLACE(OutputContract,
    N'参考图写 @图片N，台词写 <d>[Chinese] 原文</d>，音色写 <Audio N>；',
    N'参考图写 @图片N，台词写 <d>[Chinese] 原文</d>，音色写 <Audio N>——有音色参考时必须在【参考素材说明】里补 @音频N 声明行、并在 subject_definitions 之后输出 audio_definitions: 节把 <Audio N> 定义出来，两处缺一，<Audio N> 就是悬空引用；内心独白 OS / 旁白 / 画外音也算开口，同样带 <Audio N>；')
WHERE PackId = 1 AND StageKey = N'P4';

-- ========== 确认 ==========
SELECT DocId, Title, Chars, LEN(Content) AS NowLen FROM DirectorSkillDocs WHERE DocId = 40;
SELECT LEN(OutputContract) AS ContractLen FROM DirectorSkillStages WHERE PackId = 1 AND StageKey = N'P4';
