SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'踏天步')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'踏天步',N'体',2,N'岳沉罡',NULL,N'类型:身法位移；岳沉罡一步踏出，脚下白环炸开，身形化作残影贴地疾行；连踏数步，白环在落脚点连环爆开，越过荒野/屋顶/山道；镜头:低机位仰拍起踏→贴地侧方跟拍→纵跃时全景展示跨度→落地气环慢动作；节奏:起踏（快）→连踏（更快）→落地顿帧（重）',NULL,N'踏天,疾行,气环,纵跃,身法',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'擒龙手')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'擒龙手',N'体',3,N'岳沉罡',NULL,N'类型:技能拉满；岳沉罡侧身让过来势，五指如钩扣住对手手腕/兵器，顺势转腕反锁，将攻势引偏；肩颈发力把对手按倒/压跪，兵器脱手；镜头:突进抢手中景→锁腕手部特写→按压制服低机位；节奏:让势（慢）→抢手（快）→锁死（顿）→制服（重）',NULL,N'擒拿,控拿,锁腕,制敌,卸兵器',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'碎金劲')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'碎金劲',N'体',3,N'岳沉罡',NULL,N'类型:技能拉满；赤金气血沿手臂压向拳面，四道短拳同时/连环击出，拳劲透体；命中处先顿一瞬，再炸开多层气环；镜头:拳锋特写→正面广角看四拳轨迹→命中顿帧→贯穿气环慢动作；节奏:蓄力（慢）→短拳连发（快）→命中定格（重）',NULL,N'短拳,寸劲,赤金气血,连击,穿透',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'赤金气血')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'赤金气血',N'体',2,N'岳沉罡',NULL,N'类型:状态爆发；赤金气血从背部、肩臂涌向拳锋，皮肤/拳面浮现金纹，气浪扭曲空气；随后一拳轰出，气血随拳罡喷发；镜头:背部气血升腾仰拍→拳锋金纹特写→轰拳正面广角；节奏:升腾（慢·亮）→凝聚（静）→爆发（快·重）',NULL,N'气血,赤金,爆发,强化,拳罡',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'舍身一拳')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'舍身一拳',N'体',4,N'岳沉罡',NULL,N'类型:大招释放；岳沉罡放弃防御，全身赤金气血压向右拳，踏碎地面冲向阵眼；一拳轰出，前方阵纹/气墙被拳压贯穿，冲击波炸开；镜头:正面中景→冲刺贴身跟拍→拳锋破阵极近景→全景看阵纹崩碎；节奏:蓄力（极静）→舍身冲锋（快）→破阵定格（重）→余波（慢）',NULL,N'舍身,一拳,破阵,重击,孤注一掷',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'镇岳崩')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'镇岳崩',N'体',4,N'岳沉罡',NULL,N'类型:大招释放；岳沉罡凌空/贴地轰出，拳势凝成山岳虚影当空压落；目标被压进地面，环形裂纹扩散；镜头:仰拍山岳虚影→拳锋推进→命中顿帧→俯拍裂纹扩散；节奏:起手（慢）→下压（快）→镇落（重）→余震（长）',NULL,N'镇岳,重击,砸落,山岳,压制',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'暗金山岳虚影')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'暗金山岳虚影',N'圣',5,N'岳沉罡',NULL,N'类型:终极大招；暗金色山岳虚影自天穹压落，山体纹理清晰，金纹流转；下方地面先静默，再整片塌陷，气浪环状横扫；镜头:超远景建立山岳体量→仰拍虚影镇落→地面裂缝特写→冲击波广角；节奏:显形（慢·宏大）→压落（快）→镇地（极重）→余波（长）',NULL,N'山岳虚影,暗金,镇落,终极,领域',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'流星坠地')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'流星坠地',N'体',5,N'岳沉罡',NULL,N'类型:终极大招；岳沉罡从高空加速坠落，周身赤金流光拖成彗尾；落地一拳轰出，地面如陨石撞击般炸开环形冲击；镜头:高空俯拍坠落→侧方跟拍加速→落点慢动作→冲击波全景；节奏:起落（慢）→加速（极快）→撞击定格（重）→余波（长）',NULL,N'流星,坠地,重击,高速,终极',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'拳风')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'拳风',N'体',1,N'岳沉罡',NULL,N'类型:基础招式；岳沉罡一拳轰出，拳风破空化作可见气浪，吹熄火焰/震开杂物/掀飞敌人衣袍；镜头:拳锋正面特写→气浪沿轴线推进→目标被掀飞全景；节奏:出拳（快）→破空（快）→命中（顿）',NULL,N'拳风,远程,破空,气浪,基础',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'火墙')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'火墙',N'火',2,N'赤焰宗守山弟子',NULL,N'类型:技能拉满；以火符或掌势引燃，烈焰自地面连排升起形成火墙，热浪扭曲空气；人物穿墙或被阻时火浪炸开；镜头:低机位仰拍火墙→火焰细节特写→人物穿墙/被阻中景；节奏:起符（慢）→火墙升腾（快）→碰撞（重）',NULL,N'火墙,防御,拦截,烈焰,赤焰宗',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'七曜诛圣阵')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'七曜诛圣阵',N'圣',5,N'七宗宗主',NULL,N'类型:终极大招；七根阵柱同时亮起，七道法光（赤红血火、幽蓝寒冰、紫金雷蛇、白光剑影、青灰风刃、漆黑暗雾、白金圣辉）自阵柱升起，交织封住天地；阵纹沿台面蔓延，抽取三城生机汇向献祭阵眼；七宗宗主立于柱侧同催阵纹，最后一道白金圣光自云层落下。镜头:大远景建立七柱环阵→仰拍七色法光冲天→阵纹/生机流线特写→七道法光同时压下；节奏:起阵（慢·宏大）→封天（快）→抽生（阴）→压落（重）。',NULL,N'镇世武圣,七曜,诛圣阵,合击,封天,七宗',30);
IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'御剑诀')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'御剑诀',N'剑阵',1,NULL,NULL,N'类型:基础招式；手中/背后飞剑出鞘，剑光划出弧线直取目标；剑身嗡鸣，气劲随剑指牵引；镜头:剑指特写→飞剑出鞘中景→剑光命中目标全景；节奏:起指（慢）→出鞘（快）→命中（顿）',NULL,N'飞剑,御剑,基础,剑光,远程',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'万剑归宗')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'万剑归宗',N'剑阵',4,NULL,NULL,N'类型:大招释放；漫天剑气凝成千百道剑影，悬于身后如孔雀开屏；随后万剑齐发，如骤雨倾泻覆盖整片战场；镜头:仰拍万剑悬空→剑雨倾泻全景→剑影命中连爆特写；节奏:凝剑（慢·宏大）→齐发（快）→覆盖（重）→余波（长）',NULL,N'万剑,剑雨,覆盖,群攻,大招',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'紫霄雷鞭')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'紫霄雷鞭',N'雷',3,NULL,NULL,N'类型:技能拉满；掌心凝聚紫电，甩出化作数丈雷鞭，抽击时电弧炸裂四溅；可横扫成弧、可直刺一点；镜头:掌心紫电特写→雷鞭抽击侧拍→电弧炸裂慢动作；节奏:聚电（慢）→甩鞭（快）→抽中（重）',NULL,N'紫霄,雷鞭,电弧,抽击,雷系',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'九霄神雷')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'九霄神雷',N'雷',5,NULL,NULL,N'类型:终极大招；天穹阴云旋聚，九道紫金神雷自云心连环劈落，锁定目标连续轰击；雷光灼白，地面焦黑龟裂；镜头:云心雷光特写→神雷连落全景→落点爆炸慢动作；节奏:聚云（慢·压抑）→连落（快）→最终一击（极重）→余威（长）',NULL,N'九霄,神雷,连环,天罚,终极',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'寒渊冰锁')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'寒渊冰锁',N'冰',3,NULL,NULL,N'类型:技能拉满；寒气自脚底蔓延成冰纹，凝成冰锁缠向目标四肢与躯干；冰锁收紧时表面结霜裂纹；镜头:冰纹蔓延特写→冰锁缠缚中景→收紧爆裂慢动作；节奏:起霜（慢）→蔓延（快）→锁死（重）',NULL,N'寒渊,冰锁,冻结,束缚,冰系',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'冰封千里')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'冰封千里',N'冰',5,NULL,NULL,N'类型:终极大招；寒气自施法者为中心瞬间爆发，冰霜以肉眼可见速度吞没大地、建筑与敌人；万物凝晶，呼息成冰；镜头:中心冰爆特写→冰封扩散全景→冰晶世界慢动作；节奏:凝寒（慢·静谧）→爆发（极快）→封冻（重）→寂静（长）',NULL,N'冰封,千里,冻结,领域,终极',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'裂空风刃')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'裂空风刃',N'风',2,NULL,NULL,N'类型:技能拉满；抬手凝出数道半透明风刃，破空旋斩而出；风刃切开空气带出尖啸，可连续释放；镜头:掌心风刃特写→风刃旋飞侧拍→命中切割慢动作；节奏:凝刃（快）→掷出（快）→命中（顿）',NULL,N'裂空,风刃,切割,旋斩,风系',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'罡风龙卷')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'罡风龙卷',N'风',4,NULL,NULL,N'类型:大招释放；双手引风成旋，龙卷拔地而起裹挟碎石断木横扫战场；风力撕扯一切，目标被卷上半空；镜头:仰拍龙卷成形→横扫推进全景→被卷入空中目标跟拍；节奏:引风（慢）→成形（快）→横扫（重）→卷杀（长）',NULL,N'罡风,龙卷,横扫,卷入,大招',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'噬魂暗幕')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'噬魂暗幕',N'暗',4,NULL,NULL,N'类型:大招释放；墨色暗雾自周身涌出遮天蔽日，吞噬光线的同时侵蚀目标气血与斗志；暗雾中浮现影爪连环抓摄；镜头:暗雾涌出低机位→吞噬天地全景→影爪抓摄特写；节奏:涌雾（慢·压抑）→遮蔽（快）→侵蚀（重）',NULL,N'噬魂,暗幕,吞光,侵蚀,大招',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'万噬鬼域')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'万噬鬼域',N'暗',5,NULL,NULL,N'类型:终极大招；以自身为域心展开漆黑领域，万千鬼影自虚影中浮现扑向目标；领域内生机被抽干，草木枯萎；镜头:领域展开俯拍→鬼影蜂拥全景→生机抽离慢动作；节奏:张域（慢·阴森）→蜂拥（快）→吞噬（极重）→枯寂（长）',NULL,N'万噬,鬼域,领域,吞噬,终极',30);

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'焚天火海')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'焚天火海',N'火',4,NULL,NULL,N'类型:大招释放；双手结印引动地火，火海自脚下向四周燎原般扩散，热浪冲天；火舌舔舐天空，空气扭曲；镜头:结印特写→火海扩散全景→火舌冲天仰拍；节奏:结印（慢）→燎原（快）→冲天（重）→余烬（长）',NULL,N'焚天,火海,燎原,扩散,大招',30);
-- 技能库按标签跨剧集/项目过滤，项目30技能统一补"镇世武圣"标签。
UPDATE SkillLibrary SET Tags = CASE
    WHEN Tags IS NULL OR LTRIM(RTRIM(Tags)) = N'' THEN N'镇世武圣'
    WHEN Tags LIKE N'%镇世武圣%' THEN Tags
    ELSE Tags + N',镇世武圣'
END
WHERE UserId=1 AND ProjectId=30;
SELECT COUNT(*) AS TotalProject30Skills FROM SkillLibrary WHERE UserId=1 AND ProjectId=30;
