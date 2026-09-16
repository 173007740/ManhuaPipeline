SET NOCOUNT ON;
SET XACT_ABORT ON;

-- 「七曜诛圣阵」是七宗宗主合击阵，Stage 5/9 只有技能库里的技能才会被锁定，
-- 因此必须入库后分镜才不会把它丢成自由发挥。
IF OBJECT_ID(N'dbo.SkillLibrary', N'U') IS NULL
BEGIN
    THROW 51100, N'缺少 SkillLibrary 表，请先执行 Setup.sql。', 1;
END;

IF NOT EXISTS (SELECT 1 FROM SkillLibrary WHERE UserId=1 AND ProjectId=30 AND Name=N'七曜诛圣阵')
INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,ImageUrl,Tags,ProjectId)
VALUES(1,N'七曜诛圣阵',N'圣',5,N'七宗宗主',NULL,N'类型:终极大招；七根阵柱同时亮起，七道法光（赤红血火、幽蓝寒冰、紫金雷蛇、白光剑影、青灰风刃、漆黑暗雾、白金圣辉）自阵柱升起，交织封住天地；阵纹沿台面蔓延，抽取三城生机汇向献祭阵眼；七宗宗主立于柱侧同催阵纹，最后一道白金圣光自云层落下。镜头:大远景建立七柱环阵→仰拍七色法光冲天→阵纹/生机流线特写→七道法光同时压下；节奏:起阵（慢·宏大）→封天（快）→抽生（阴）→压落（重）。',NULL,N'镇世武圣,七曜,诛圣阵,合击,封天,七宗',30);

PRINT N'七曜诛圣阵已加入技能库。';
