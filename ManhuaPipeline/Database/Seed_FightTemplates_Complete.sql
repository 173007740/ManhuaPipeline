SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'轻功位移 · 疾行远去')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'轻功位移 · 疾行远去',1,11,N'轻功赶路、纵跃奔行、甩开追兵，画面重点在速度与空间跨度',N'起势蹬地（快）→ 连环纵跃（快）→ 落地提速（中）→ 远去定格（慢）',N'A:沉腰蹬地，脚下气环炸开；一步踏出破庙/廊檐，身形压低贴地疾行；中途借岩壁、树梢、屋顶连续借力纵跃，白环在落脚点连环爆开；最后一步跨过沟壑/高墙，落地不停顿，继续远去。',N'起手低机位仰拍蹬地特写；起步瞬间甩镜跟拍；中途贴地侧方跟拍，纵跃时拉至全景展示空间跨度；结尾镜头升空拉远，人物缩为小点。',N'速度感优先，禁止慢动作拖节奏；落脚点白环/气浪只做一帧反馈；位移方向明确，禁止原地打转；镜头全程跟随不切碎。',N'轻功,位移,纵跃,赶路,疾行,气环');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'贴身短打 · 拳拳到肉')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'贴身短打 · 拳拳到肉',1,5,N'快节奏徒手近战、密集短拳、压制对手',N'前踏抢攻（快）→ 三连短拳（快）→ 命中顿帧（重）',N'A:前踏抢进中线，沉肩送拳；三记短拳连续轰向头、肋、腹，拳拳压实；最后一记重拳命中，B 吃痛后仰，A 顺势收拳护头。',N'手持近身贴拍，拳锋特写与面部反应快速切换；命中瞬间顿帧0.3秒接慢动作看肌肉颤动。',N'禁止出现兵刃和技能特效；距离始终贴近，禁止拉远成空镜头；命中必须顿帧。',N'徒手,短打,连击,拳拳到肉,压制');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'徒手近身 · 肘膝摔投')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'徒手近身 · 肘膝摔投',2,11,N'徒手近身缠斗、肘击、膝撞、抱摔、摔投',N'抢步近身（快）→ 肘膝连击（快）→ 锁拿摔投（顿）→ 落地压制（重）',N'A:侧身避开来势，一步抢入内线；肘击撞脸，膝撞顶腹，借对手前倾之势扣住手臂/衣领；转身发力将 B 摔投出去，B 砸地翻滚；A 落地压身补一记控制。',N'贴身侧后方跟拍抢步与肘膝动作；摔投瞬间切正面广角，镜头随 B 划出抛物线；落地瞬间顿帧+慢动作看尘土/碎石。',N'禁止兵刃、禁止大范围特效；动作链完整，抢步到落地不断裂；命中点清晰，禁止软绵绵隔空比划。',N'徒手,近身,肘击,膝撞,摔投,格斗');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'防守反击 · 格挡反打')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'防守反击 · 格挡反打',2,11,N'先防后攻、挡开攻击、抓住破绽反打',N'格挡卸力（慢）→ 侧闪（快）→ 反打（快）→ 震开收势（重）',N'B 连续攻来；A 双臂/手掌格挡卸力，脚步侧移避开第二击；趁 B 旧力未收，A 一记直拳/掌刀反打胸口，B 后退；A 震开双臂，站稳收势。',N'正面中近景拍格挡与卸力；侧闪瞬间手持横移；反打切拳锋特写，命中顿帧。',N'格挡要有受力感，禁止凭空闪避；反打必须接在破绽之后，禁止无铺垫反击；镜头以A为轴，不切B主观视角。',N'防守,反击,格挡,侧闪,徒手');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'兵器突刺 · 一击贯穿')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'兵器突刺 · 一击贯穿',2,11,N'刀剑枪等兵器直线突进、单发破防、贯穿一击',N'沉肩蓄势（慢）→ 直线突刺（快）→ 贯穿顿帧（重）→ 收刃（静）',N'A:压刀/枪/剑于腰侧，气机沉落；踏地突进，直线刺出，兵刃穿过 B 防御中缝；命中瞬间贯穿/点停，B 被钉住一瞬；A 抽刃甩血/收势。',N'低机位正面拍蓄势；突刺瞬间高速甩镜沿兵刃轴线推进；贯穿瞬间极近景顿帧，随后拉远看战果。',N'禁止乱切多角度；直线感必须强；贯穿类命中禁止血腥，用顿帧和光影表达。',N'兵器,突刺,贯穿,破防,刀剑枪');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'远程对轰 · 法术互射')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'远程对轰 · 法术互射',3,11,N'远程技能/法术对射、拳风对火符、能量互撞',N'双方蓄力（慢）→ 远程连发（快）→ 能量对撞（顿）→ 爆散（重）',N'A 与 B 拉开距离，各自蓄力；远程攻击交错对射，第一波擦身，第二波正面相撞；相撞处能量炸开、气浪横扫；A 穿过爆散余波继续逼近。',N'中远景双人分屏构图；对撞瞬间正面广角定格+慢动作看冲击波；爆散后镜头快速推近 A。',N'远程轨迹必须清晰可见；对撞点必须明确，禁止各打各的；爆炸禁止遮挡角色脸超过两秒。',N'远程,对轰,法术,拳风,火符,能量对撞');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'破阵突入 · 火线压制')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'破阵突入 · 火线压制',3,11,N'突破阵列、火墙/箭雨/法阵压制、正面突围',N'观察阵型（慢）→ 正面冲锋（快）→ 破口（顿）→ 穿阵而过（快）',N'多名敌人列阵压制，火墙/攻击线推来；A 压低身形正面冲锋，拳风/掌劲轰开第一道口子；两侧敌人合围，A 连续闪避并击退近身者；最后从破口穿阵而出，头也不回。',N'超远景展示阵型规模；冲锋时贴地跟拍；破口瞬间正面慢动作看攻击线崩散；穿阵后升空拉远。',N'阵型与人数必须体现；破口顺序要清楚；禁止敌人原地消失，被打倒者要有反馈。',N'破阵,突入,压制,火墙,冲锋');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'环境闪避 · 坠物连环')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'环境闪避 · 坠物连环',3,11,N'躲避陨石/坠物/机关，闪避为主、反击为辅',N'预警抬头（慢）→ 连续闪避（快）→ 借物反击（快）→ 安全落地（重）',N'高空坠物/陨石连续砸落；A 抬头预警，横向翻滚避开第一落点；左右坠物夹击时从缝隙穿过；借一块坠物作踏板跃起，拳脚击碎/击偏来袭物；最后落回安全区。',N'垂直俯拍坠物轨迹；闪避瞬间手持快速横移；穿缝用慢动作表现险势；落地顿帧看气浪。',N'每块坠物落点必须有先后和空间差；闪避动作禁止原地瞬移；穿过缝隙必须交代起跳与落地。',N'闪避,躲避,陨石,坠物,环境破坏');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'突进擒拿 · 控拿制敌')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'突进擒拿 · 控拿制敌',3,11,N'突进近身、擒拿/锁拿、卸兵器/制敌',N'观察来势（慢）→ 突进抢手（快）→ 锁拿（顿）→ 制服（重）',N'B 远程攻击/兵器袭来；A 侧身让过，一步突进抢入中门；单手扣住 B 手腕/兵器，顺势转腕反锁；另一手按住肩颈，将 B 压跪/按倒；武器被卸下或脱手。',N'正面中景拍突进；锁腕瞬间切手部特写；按压制服用低机位仰拍。',N'擒拿必须有锁死感，禁止抓空气；被拿住后禁止 B 再自由出招；动作清楚交代手腕、肩颈受力。',N'突进,擒拿,锁拿,控拿,卸兵器,制服');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'群战护主 · 一夫当关')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'群战护主 · 一夫当关',3,15,N'一人对多人、保护目标、边打边退/边推进',N'被围（慢）→ 连续击退（快）→ 护住目标（顿）→ 突围（重）',N'多人同时围攻；A 以目标为轴心移动，拳/脚/兵刃连续击退近身者；短暂空隙中把目标护到身后；随后以一敌多打开缺口，边打边撤/推进。',N'环绕全景展示围攻密度；近身击退用手持快切；护目标瞬间切中景确认站位；突围时跟随 A 冲出。',N'目标位置始终明确；敌人数量必须有画面反馈；禁止敌人排队单挑。',N'群战,护主,一夫当关,围攻,突围');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'法相天地 · 全力一击')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'法相天地 · 全力一击',5,15,N'法相/虚影/巨大化招式，蓄力到全功率一击',N'气血升腾（慢·宏大）→ 法相凝实（稳）→ 同步蓄势（极静）→ 轰落（极快·重）→ 余波（长）',N'A 双脚踏稳，气血冲天在身后聚成巨大法相/虚影；法相与本体同步沉腰收拳；A 向前轰拳，法相巨拳沿同一轴线压下，前方云层/术法/山峰被拳压分开；命中后天地回声、尘浪扩散。',N'低机位仰拍气血升空→高速拉远揭示法相全貌→人物与法相同轴构图→巨拳正面逼近镜头→极远景展示天地分界。',N'法相与本体动作必须同步；禁止法相单独行动；压轴一击前必须有静场；禁止普通小打小闹。',N'法相,虚影,全力一击,天地变色,大招');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'双大绝对撞 · 天地崩裂')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'双大绝对撞 · 天地崩裂',5,15,N'终极对轰、双方大招正面相撞',N'双方蓄力（慢·宏大）→ 能量对冲（快）→ 相撞失声（极静）→ 天地崩裂（重·长）',N'双方同时释放大招，能量洪流从两侧压来；相撞点先短暂失声，随后冲击波环状扩散，云层、地面、建筑依次崩裂；两人各自后退，胜负未分/一方被压退。',N'超远景建立双方能量规模；正面广角拍对冲；相撞瞬间绝对静止半秒，再慢动作看冲击波；最后垂直升空展示破坏范围。',N'大招名称和形态必须与技能库一致；相撞前必须有蓄力过程，禁止直接对波；破坏范围要分层次推进。',N'大招,对撞,对轰,终极,天地崩裂');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'高手过招 · 一剑定音')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'高手过招 · 一剑定音',4,11,N'高阶精英对决、攻防转换极快、一击定音',N'对峙蓄势（静）→ 试探交错（快）→ 拆招换式（快）→ 定音一剑（顿）→ 收剑（静）',N'双方持械/空手对峙，气机锁定；第一波快速交错各攻半招；第二波拆招换式，攻防转换两次以上；最后一击格开/破防，一剑/一拳定音，胜负分明；胜者收势，输者退步。',N'环绕中景保持双方对称构图；交错瞬间快切特写；拆招用侧跟拍交代攻防转换；定音一击正面顿帧+短慢动作。',N'攻防转换必须清晰可数；禁止一方全程压制无还手；定音一击必须建立在前面对拆的势差之上。',N'高手过招,精英对决,拆招,定音,势差');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'浮空连段 · 凌空压制')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'浮空连段 · 凌空压制',4,11,N'把对手打浮空后连续追击、空中压制、落地终结',N'起手挑空（快）→ 凌空连击（快）→ 空中换位（快）→ 落地砸击（重）',N'A 一记上挑/剑气把 B 挑离地面；随即跃起凌空连击，拳/剑连续命中保持浮空；空中变向绕到 B 上方/侧方，最后一记重击将其砸向地面；B 砸地，A 落地收势。',N'挑空瞬间慢动作+仰拍；凌空连击用环绕跟拍锁定双方；空中换位切背面；落地砸击正面广角顿帧看尘土。',N'浮空高度与连击次数匹配，禁止空中乱打；落地砸击必须接住浮空轨迹；空中命中禁止无反馈。',N'浮空,连段,空中追击,压制,落地终结');

IF NOT EXISTS (SELECT 1 FROM FightTemplate WHERE UserId=1 AND Name=N'以寡敌众 · 无双扫荡')
INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags)
VALUES(1,N'以寡敌众 · 无双扫荡',4,15,N'以一敌多高阶版、连续放倒、气势无双',N'被围（慢）→ 连续放倒（快）→ 短暂喘息（顿）→ 再次扫荡（快）→ 收势（重）',N'多名敌人分批扑上；A 不后退，以位移+反击连续放倒近身者，每次击退都带出明确方向；批次之间用短促喘息/环视节奏区分；最后一波正面硬接后尽数扫开；A 站在倒地的敌人之间收势。',N'中景跟随 A 的连续移动，镜头保持人物居中；每次放倒切一次正面特写；喘息瞬间拉全景展示战果；收势用低机位仰拍。',N'敌人分批进攻，禁止一拥而上原地挨打；每次放倒的敌人要有先后层次；A 的位移必须与镜头调度一致。',N'以一敌多,无双,扫荡,放倒,气势');

SELECT COUNT(*) AS TotalFightTemplates FROM FightTemplate WHERE UserId=1;
