using System.Reflection;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class Stage9PostGuardTests
{
    private static string CallEnsureSkillRefImages(
        string prompt,
        List<SkillLibraryItem>? skills = null,
        List<SkillLibraryItem>? locked = null,
        List<string>? effects = null)
    {
        var method = typeof(AgentService).GetMethod("EnsureSkillRefImages", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object?[]
        {
            prompt,
            skills ?? new List<SkillLibraryItem>(),
            locked ?? new List<SkillLibraryItem>(),
            effects ?? new List<string>(),
            null
        })!;
    }

    private static string CallEnsureSkillRefImagesWithContext(
        string prompt,
        List<SkillLibraryItem>? skills = null,
        List<SkillLibraryItem>? locked = null,
        List<string>? effects = null,
        string? unitContext = null)
    {
        var method = typeof(AgentService).GetMethod("EnsureSkillRefImages", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object?[]
        {
            prompt,
            skills ?? new List<SkillLibraryItem>(),
            locked ?? new List<SkillLibraryItem>(),
            effects ?? new List<string>(),
            unitContext
        })!;
    }

    private static string CallEnsureDialogueLines(string prompt)
    {
        var method = typeof(AgentService).GetMethod("EnsureDialogueLines", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object[] { prompt })!;
    }

    private static string CallNormalizeTimeSegmentHeaders(string prompt)
    {
        var method = typeof(AgentService).GetMethod("NormalizeTimeSegmentHeaders", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object[] { prompt })!;
    }

    private static string CallNormalizeCompactTimeBlocks(string prompt)
    {
        var method = typeof(AgentService).GetMethod("NormalizeCompactTimeBlocks", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object[] { prompt })!;
    }

    private static string CallEnsureActionCameraEmbedded(string prompt)
    {
        var method = typeof(AgentService).GetMethod("EnsureActionCameraEmbedded", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object[] { prompt })!;
    }

    private static string CallBuildSeedanceSystemPrompt()
    {
        var method = typeof(AgentService).GetMethod("BuildSeedanceSystemPrompt", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object[] { "风格", "技能", "打斗", "角色" })!;
    }

    [Fact]
    public void BodyMentionedSkill_IsAddedToRefImages()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        参考图:第一张(@图1)为[前世岳沉天]形象参考，保持外貌、发型、服装、气质一致；第二张(@图2)为[前世岳沉天战斗态]形象参考，保持气血、战损、赤金纹状态一致；第三张(@图3)为[太虚圣主]形象参考，保持云上身影、白金圣袍、星辰纹饰一致；第四张(@图4)为[七宗宗主]形象参考，保持七脉宗主袍服、阵纹罩身、威严冷漠一致；第五张(@图5)为[七曜诛圣阵]特效参考，保持形态、色调、氛围一致；倒数第二张(@图6)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图7)为画面风格参考（光影、色调、材质质感）
        [组合:葬天台-夜-5s]
        0-5秒[1]
        时长:5秒
        动作:@前世岳沉天:赤金气血在衣袍下沿经脉隐约透光，一道法光被气机硬生生弹开数尺
        """;

        var result = CallEnsureSkillRefImages(
            prompt,
            skills: new List<SkillLibraryItem> { new SkillLibraryItem { Name = "赤金气血", ImageUrl = "http://img/skill/flag.png" } });

        Assert.Contains("[赤金气血]特效参考", result);
        Assert.Contains("倒数第二张(@图7)为[葬天台]场景参考", result);
        Assert.Contains("最后一张(@图8)为画面风格参考", result);
        var numbers = System.Text.RegularExpressions.Regex.Matches(result, @"@图(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
        Assert.Equal(8, numbers.Count);
        for (var i = 1; i < numbers.Count; i++)
            Assert.Equal(numbers[i - 1] + 1, numbers[i]);
    }

    [Fact]
    public void BodyMentionedEffect_IsAddedToRefImages()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        参考图:第一张(@图1)为[前世岳沉天]形象参考，保持外貌、发型、服装、气质一致；倒数第二张(@图2)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图3)为画面风格参考（光影、色调、材质质感）
        0-5秒[1]
        时长:5秒
        场景:赤金气血透出暖金色微光，冷暖对比
        """;

        var result = CallEnsureSkillRefImages(prompt, effects: new List<string> { "赤金气血" });

        Assert.Contains("[赤金气血]特效参考", result);
        Assert.Contains("倒数第二张(@图3)为[葬天台]场景参考", result);
        Assert.Contains("最后一张(@图4)为画面风格参考", result);
    }

    [Fact]
    public void UnusedUnitSkill_IsRemovedFromCompactAtImageLine()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-2】
        类型:打斗/动作
        @图1 [前世岳沉天]人物形象参考，保持外貌、发型、服装、气质一致；@图2 [前世岳沉天战斗态]战斗姿态参考，保持外貌、发型、服装、气质一致；@图3 [太虚圣主]人物形象参考，保持外貌、发型、服装、气质一致；@图4 [圣光诛邪]特效参考，保持白金圣光形态、垂落压迫氛围一致；@图5 [擒龙手]特效参考，保持赤金气劲环绕掌缘、近身切入氛围一致；@图6 [赤金气血]特效参考，保持形态、色调、氛围一致；@图7 [葬天台]场景参考，保持空间布局、黑石台面、阵柱位置、云海氛围一致；@图8 [光影质感]光影质感参考，保持冷月光、体积光、强明暗对比、电影级材质一致
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台·夜空。无台词，无对白。
        [0-2s]侧向环绕跟拍，太虚圣主立于云端，白金圣光自天门垂落；前世岳沉天贴地突进。
        [2-4s]贴身跟拍快甩，前世岳沉天肘击法光节点。
        [4-5s]环绕硬切，前世岳沉天回身架臂格挡，短促反手一拳将圣光震散。
        灯光：冷月光为底光铺满云海，白金圣光自天门垂落为主光源。
        约束：前世岳沉天突进需有起跳落地逻辑，落地双脚踩实。
        负向提示词：人物变形，手指畸形，画面闪烁。
        """;

        var skills = new List<SkillLibraryItem>
        {
            new SkillLibraryItem { Name = "圣光诛邪", OwnerCharacter = "太虚圣主", Element = "圣", Tier = 3, ImageUrl = "http://img/skill/sheng.png" },
            new SkillLibraryItem { Name = "擒龙手", OwnerCharacter = "岳沉天", Element = "体", Tier = 3, ImageUrl = "http://img/skill/qin.png" },
            new SkillLibraryItem { Name = "赤金气血", OwnerCharacter = "岳沉天", Element = "体", Tier = 2, Tags = "状态", ImageUrl = "http://img/skill/flag.png" }
        };
        var locked = skills;
        var unitContext = """
        - **镜头编号**: 1.2-2
        - **节拍**: Beat1, Beat2
        - **镜头描述**: 云层上太虚圣主再次开口，一道白金圣光自天门垂下直压阵心，岳沉天贴地突进，一记肘击砸在最近的法光节点上，随即回身格挡第二道圣光，短促反手一拳将其震散。
        - **镜头时长**: 5秒
        """;

        var result = CallEnsureSkillRefImagesWithContext(prompt, skills, locked, null, unitContext);

        Assert.DoesNotContain("[擒龙手]", result);
        Assert.Contains("[圣光诛邪]特效参考", result);
        Assert.Contains("[赤金气血]特效参考", result);
        Assert.Contains("[葬天台]场景参考", result);
        Assert.Contains("[光影质感]光影质感参考", result);
        var numbers = System.Text.RegularExpressions.Regex.Matches(result, @"@图(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
        Assert.Equal(7, numbers.Count);
        for (var i = 1; i < numbers.Count; i++)
            Assert.Equal(numbers[i - 1] + 1, numbers[i]);
    }
    [Fact]
    public void MissingDialogueLine_IsFilledWithNoDialogue()
    {
        var prompt = """
        【第1集】【单元1.8】【镜头1.8-1】
        参考图:第一张(@图1)为[少年岳沉天]形象参考，保持外貌、发型、服装、气质一致；倒数第二张(@图2)为[荒郊破庙]场景参考，保持空间布局、色调、氛围一致；最后一张(@图3)为画面风格参考（光影、色调、材质质感）
        0-5秒[1]
        时长:5秒
        主体:赤金气血自肩背升腾为贯穿画面的垂直光柱
        动作:少年岳沉天双手微垂，以意念引导赤金气血升腾
        姿态:少年岳沉天微仰头，目光平静锐利
        """;

        var result = CallEnsureDialogueLines(prompt);

        Assert.Contains("\n对话:无\n", result);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, @"对话:无"));
    }

    [Fact]
    public void ExistingDialogueLine_IsNotDuplicated()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        0-5秒[1]
        时长:5秒
        动作:太虚圣主居高临下开口
        对话:太虚圣主说“岳沉天，你一人再强，也强不过天下法统。”
        """;

        var result = CallEnsureDialogueLines(prompt);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, @"对话:"));
        Assert.Contains("对话:太虚圣主说", result);
    }

    [Fact]
    public void MismatchedSegmentHeader_IsRenormalized()
    {
        var prompt = """
        【第1集】【单元1.10】【镜头1.10-1】
        [组合:荒郊破庙-夜-5s]
        0-4秒[1]
        时长:5秒
        动作:少年岳沉天一步踏出破庙
        """;

        var result = CallNormalizeTimeSegmentHeaders(prompt);

        Assert.Contains("0-5秒[1]", result);
        Assert.DoesNotContain("0-4秒[1]", result);
    }

    [Fact]
    public void ConsistentSegmentHeaders_AreUnchanged()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        [组合:葬天台-夜-11s]
        0-2秒[1]
        时长:2秒
        动作:七宗宗主同时催动法光
        2-4秒[2]
        时长:2秒
        动作:太虚圣主一拂袖
        4-6秒[3]
        时长:2秒
        动作:赤金气血硬压血火
        6-8秒[4]
        时长:2秒
        动作:剑影交错
        8-11秒[5]
        时长:3秒
        动作:七色光幕压下
        """;

        var result = CallNormalizeTimeSegmentHeaders(prompt);

        Assert.Equal(prompt, result);
    }

    [Fact]
    public void SingleSegmentFullDurationHeader_IsUnchanged()
    {
        var prompt = """
        【第1集】【单元1.3】【镜头1.3-1】
        [组合:客栈后院-夜-11s]
        0-11秒[1]
        时长:11秒
        动作:0-1秒 A爆发灵力持剑突进；1-2秒 A连斩两剑，B侧身后仰连续闪过；2-3秒 A第三剑劈落，B侧步切入剑势内侧；3-4秒 B扣住A持剑手腕，右拳蓄力；4-5秒 B一拳轰中A胸口，气血炸开，A倒飞
        运镜:0-1秒 低角度极速推镜；1-2秒 贴身平行极速横移；2-3秒 快速推近；3-4秒 特写扣腕；4-5秒 镜头短促震动跟随冲击
        """;

        var result = CallNormalizeTimeSegmentHeaders(prompt);

        Assert.Equal(prompt, result);
    }

    [Fact]
    public void CompactTimeBlockGap_IsRenumberedContinuously()
    {
        var prompt = """
        【第1集】【单元1.13】【镜头1.13-2】
        类型:文戏/情感
        @图1 [少年岳沉天]人物；@图2 [光影质感]光影质感
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，枯井遗府+烛光摇曳。
        [0-1s]侧面低机位固定，少年岳沉天转身面向石台遗骨。
        [2-3s]侧面过肩低机位跟随，少年岳沉天双手合抱拳带抵于胸前。
        [4-5s]中全景横移缓拉，少年岳沉天单膝跪地垂首。
        灯光：暖烛光。
        约束：机位平滑缓拉。
        """;

        var result = CallNormalizeCompactTimeBlocks(prompt);

        Assert.Contains("[0-2s]侧面低机位固定", result);
        Assert.Contains("[2-4s]侧面过肩低机位跟随", result);
        Assert.Contains("[4-5s]中全景横移缓拉", result);
        Assert.DoesNotContain("[0-1s]", result);
        Assert.DoesNotContain("[2-3s]", result);
    }

    [Fact]
    public void CompactTimeBlock_AlreadyContiguous_IsUnchanged()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        类型:高潮/对决
        @图1 [前世岳沉天]人物；@图2 [光影质感]光影质感
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台+白天。
        [0-3s]大远景固定机位。
        [3-7s]镜头急推至中近景。
        [7-11s]持续仰拍。
        灯光：冷月光。
        约束：禁止站桩。
        """;

        var result = CallNormalizeCompactTimeBlocks(prompt);

        Assert.Equal(prompt, result);
    }

    [Fact]
    public void CompactTimeBlock_NonStandardTotal_IsUnchanged()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:文戏/情感
        @图1 [前世岳沉天]人物；@图2 [光影质感]光影质感
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台+白天。
        [0-1s]高空俯拍。
        [2-3s]硬切近景。
        [4-6s]特写收束。
        灯光：惨白天光。
        约束：语速从容。
        """;

        var result = CallNormalizeCompactTimeBlocks(prompt);

        Assert.Equal(prompt, result);
    }

    [Fact]
    public void CombatShotWithBattleState_ForcesOwnedStateSkillRef()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:高潮/对决
        参考图:第一张(@图1)为[前世岳沉天]形象参考，保持外貌、发型、服装、气质一致；第二张(@图2)为[前世岳沉天战斗态]形象参考，保持气血、战损、赤金纹状态一致；第三张(@图3)为[太虚圣主]形象参考，保持云上身影、白金圣袍、星辰纹饰一致；第四张(@图4)为[七宗宗主]形象参考，保持七脉宗主袍服、阵纹罩身、威严冷漠一致；第五张(@图5)为[七曜诛圣阵]特效参考，保持形态、色调、氛围一致；倒数第二张(@图6)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图7)为画面风格参考（光影、色调、材质质感）
        @角色引用:[前世岳沉天][前世岳沉天战斗态][太虚圣主][七宗宗主]
        0-5秒[1]
        时长:5秒
        动作:太虚圣主居高临下开口，七宗宗主同催阵纹，前世岳沉天挺立阵心暂不反击
        """;

        var result = CallEnsureSkillRefImagesWithContext(
            prompt,
            skills: new List<SkillLibraryItem>
            {
                new SkillLibraryItem { Name = "赤金气血", OwnerCharacter = "岳沉天", Tier = 2, PromptVideo = "类型:状态爆发；气血贴身流动", ImageUrl = "http://img/skill/flag.png" }
            },
            locked: new List<SkillLibraryItem>
            {
                new SkillLibraryItem { Name = "七曜诛圣阵", OwnerCharacter = "七宗宗主", Tier = 5, PromptVideo = "类型:终极大招；七柱法光封天", ImageUrl = "http://img/skill/zhen.png" }
            });

        Assert.Contains("[赤金气血]特效参考", result);
        Assert.Contains("倒数第二张(@图7)为[葬天台]场景参考", result);
        Assert.Contains("最后一张(@图8)为画面风格参考", result);
        var numbers = System.Text.RegularExpressions.Regex.Matches(result, @"@图(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
        Assert.Equal(8, numbers.Count);
        for (var i = 1; i < numbers.Count; i++)
            Assert.Equal(numbers[i - 1] + 1, numbers[i]);
    }

    [Fact]
    public void BattleStateShot_WithUnitCombatContext_ForcesStateSkillRef()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        参考图:第一张(@图1)为[前世岳沉天]形象参考，保持外貌、发型、服装、气质一致；第二张(@图2)为[前世岳沉天战斗态]形象参考，保持气血、战损、赤金纹状态一致；倒数第二张(@图3)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图4)为画面风格参考（光影、色调、材质质感）
        @角色引用:[前世岳沉天][前世岳沉天战斗态]
        0-5秒[1]
        时长:5秒
        动作:前世岳沉天挺立阵心暂不反击
        """;

        var result = CallEnsureSkillRefImagesWithContext(
            prompt,
            skills: new List<SkillLibraryItem>
            {
                new SkillLibraryItem { Name = "赤金气血", OwnerCharacter = "岳沉天", Tier = 2, PromptVideo = "类型:状态爆发；气血贴身流动", ImageUrl = "http://img/skill/flag.png" }
            },
            unitContext: "- **控制模式**: 打斗模板\n- **技能**: 七曜诛圣阵, 赤金气血");

        Assert.Contains("[赤金气血]特效参考", result);
        Assert.Contains("倒数第二张(@图4)为[葬天台]场景参考", result);
        Assert.Contains("最后一张(@图5)为画面风格参考", result);
    }

    [Fact]
    public void ForcedStateSkillRef_RemovesConflictingForbiddenClause()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:高潮/对决
        参考图:第一张(@图1)为[前世岳沉天]形象参考，保持外貌、发型、服装、气质一致；第二张(@图2)为[前世岳沉天战斗态]形象参考，保持气血、战损、赤金纹状态一致；第三张(@图3)为[太虚圣主]形象参考，保持云上身影、白金圣袍、星辰纹饰一致；第四张(@图4)为[七宗宗主]形象参考，保持七脉宗主袍服、阵纹罩身、威严冷漠一致；倒数第二张(@图5)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图6)为画面风格参考（光影、色调、材质质感）
        禁止:禁止前世岳沉天此时反击或移动（赤金气血不得出现）；禁止法光缺失七色中任何一道
        @角色引用:[前世岳沉天][前世岳沉天战斗态][太虚圣主][七宗宗主]
        0-5秒[1]
        时长:5秒
        动作:太虚圣主居高临下开口
        """;

        var result = CallEnsureSkillRefImagesWithContext(
            prompt,
            skills: new List<SkillLibraryItem>
            {
                new SkillLibraryItem { Name = "赤金气血", OwnerCharacter = "岳沉天", Tier = 2, PromptVideo = "类型:状态爆发；气血贴身流动", ImageUrl = "http://img/skill/flag.png" }
            },
            unitContext: "- **控制模式**: 打斗模板");

        Assert.Contains("[赤金气血]特效参考", result);
        Assert.DoesNotContain("赤金气血不得出现", result);
        Assert.Contains("禁止前世岳沉天此时反击或移动", result);
    }

    [Fact]
    public void NonStateSkill_IsNotForcedByBattleState()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:高潮/对决
        参考图:第一张(@图1)为[前世岳沉天]形象参考，保持外貌、发型、服装、气质一致；第二张(@图2)为[前世岳沉天战斗态]形象参考，保持气血、战损、赤金纹状态一致；倒数第二张(@图3)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图4)为画面风格参考（光影、色调、材质质感）
        @角色引用:[前世岳沉天][前世岳沉天战斗态]
        0-5秒[1]
        时长:5秒
        动作:前世岳沉天挺立阵心暂不反击
        """;

        var result = CallEnsureSkillRefImagesWithContext(
            prompt,
            skills: new List<SkillLibraryItem>
            {
                new SkillLibraryItem { Name = "踏天步", OwnerCharacter = "岳沉天", Tier = 2, PromptVideo = "类型:身法位移；脚下白环炸开" }
            },
            unitContext: "- **控制模式**: 打斗模板");

        Assert.DoesNotContain("[踏天步]特效参考", result);
    }

    [Fact]
    public void NonCombatShot_DoesNotForceStateSkill()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:文戏/情感
        参考图:第一张(@图1)为[前世岳沉天]形象参考，保持外貌、发型、服装、气质一致；倒数第二张(@图2)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图3)为画面风格参考（光影、色调、材质质感）
        @角色引用:[前世岳沉天]
        0-5秒[1]
        时长:5秒
        动作:前世岳沉天垂目扫视阵纹
        """;

        var result = CallEnsureSkillRefImagesWithContext(
            prompt,
            skills: new List<SkillLibraryItem>
            {
                new SkillLibraryItem { Name = "赤金气血", OwnerCharacter = "岳沉天", Tier = 2, PromptVideo = "类型:状态爆发；气血贴身流动", ImageUrl = "http://img/skill/flag.png" }
            },
            unitContext: "- **单元类型**: 文戏/情感");

        Assert.DoesNotContain("[赤金气血]特效参考", result);
    }

    [Fact]
    public void MissingActionCamera_GetsEmbeddedFromShotCameraPlan()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-2】
        类型:高潮/对决
        0-8秒[3]
        时长:3秒
        动作:5-5.5秒 甩镜切换，白光万剑自四面八方合围切割；5.5-6秒 青灰风刃封死所有退路；6-7秒 漆黑暗雾自地面升起裹缠向前世岳沉天神魂；7-8秒 前世岳沉天双拳紧握、双臂微展，赤金气血在背脊涌动
        运镜:手持快切+甩镜跟随前世岳沉天身形，跳切压缩属性轮攻时间
        """;

        var result = CallEnsureActionCameraEmbedded(prompt);

        Assert.Contains("5.5-6秒 全景仰拍，青灰风刃", result);
        Assert.Contains("7-8秒 手持快切+甩镜跟随前世岳沉天身形，前世岳沉天双拳紧握", result);
        Assert.Contains("5-5.5秒 甩镜切换", result);
    }

    [Fact]
    public void ActionSegmentWithExistingCamera_IsNotDuplicated()
    {
        var prompt = """
        【第1集】【单元1.3】【镜头1.3-3】
        类型:高潮/对决
        0-6秒[1]
        时长:6秒
        动作:0-1秒 全景固定，前世岳沉天立于阵心；1-2秒 低角度极速推镜，拳劲撕开沿途阵纹
        运镜:全景固定→低角度极速推镜→推至阵眼前静止
        """;

        var result = CallEnsureActionCameraEmbedded(prompt);

        Assert.Contains("0-1秒 全景固定，", result);
        Assert.Contains("1-2秒 低角度极速推镜，", result);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(result, @"低角度极速推镜").Count);
    }

    [Fact]
    public void DramaShot_IsNotModifiedByCameraGuard()
    {
        var prompt = """
        【第1集】【单元1.9】【镜头1.9-2】
        类型:文戏/情感
        0-11秒[1]
        时长:11秒
        动作:1-3秒 他缓缓抬起右手从腰间取出一圈陈旧的布质拳带；3-5秒 手指将拳带一端绕上左手腕，目光沉静
        """;

        var result = CallEnsureActionCameraEmbedded(prompt);

        Assert.Equal(prompt, result);
    }

    [Fact]
    public void CompactSystemPrompt_DialogueExample_UsesVerbatimScriptName()
    {
        var prompt = CallBuildSeedanceSystemPrompt();

        Assert.Contains("太虚圣主说\"岳沉天，你一人再强，也强不过天下法统。\"", prompt);
        Assert.DoesNotContain("前世岳沉天，你一人再强，也强不过天下法统", prompt);
        Assert.Contains("台词正文一个字都不能改", prompt);
    }
}
