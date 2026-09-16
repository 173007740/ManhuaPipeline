using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class PromptNameNormalizerTests
{
    private static readonly string[] Assets =
    {
        "前世岳沉天", "前世岳沉天战斗态", "太虚圣主", "七宗宗主",
        "顾残山", "少年岳沉天", "少年岳沉天战斗态"
    };

    [Fact]
    public void BareName_MapsToPastForm_WhenUnitContextIsFlashback()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        参考图:第一张(@图1)为[岳沉天]形象参考，保持外貌、发型、服装、气质一致；倒数第二张(@图2)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图3)为画面风格参考（光影、色调、材质质感）
        @角色引用:[岳沉天]
        主体:岳沉天立于阵心
        """;

        var result = PromptNameNormalizer.Normalize(
            prompt, "【单元1.1】前世岳沉天独立阵心", Assets, ref current);

        Assert.Contains("[前世岳沉天]形象参考", result);
        Assert.Contains("@角色引用:[前世岳沉天]", result);
        Assert.Contains("主体:前世岳沉天立于阵心", result);
        Assert.DoesNotContain("[岳沉天]", result);
    }

    [Fact]
    public void CurrentForm_PersistsUntilPresentMarkerResetsIt()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        var firstPrompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        @角色引用:[岳沉天]
        """;
        PromptNameNormalizer.Normalize(firstPrompt, "【单元1.1】前世岳沉天独立阵心", Assets, ref current);
        var pastPrompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        参考图:第一张(@图1)为[岳沉天]形象参考，保持外貌、发型、服装、气质一致；最后一张(@图2)为画面风格参考（光影、色调、材质质感）
        @角色引用:[岳沉天]
        """;

        var pastResult = PromptNameNormalizer.Normalize(
            pastPrompt, "【单元1.2】岳沉天看穿阵纹", Assets, ref current);
        Assert.Contains("[前世岳沉天]形象参考", pastResult);

        var presentPrompt = """
        【第1集】【单元1.5】【镜头1.5-1】
        参考图:第一张(@图1)为[岳沉天]形象参考，保持外貌、发型、服装、气质一致；最后一张(@图2)为画面风格参考（光影、色调、材质质感）
        @角色引用:[岳沉天]
        """;

        var presentResult = PromptNameNormalizer.Normalize(
            presentPrompt, "【单元1.5】少年岳沉天", Assets, ref current);
        Assert.Contains("[少年岳沉天]形象参考", presentResult);
        Assert.Contains("@角色引用:[少年岳沉天]", presentResult);
    }

    [Fact]
    public void Aliases_AreMappedToCanonicalNames()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        var prompt = """
        【第1集】【单元1.8】【镜头1.8-1】
        参考图:第一张(@图1)为[倒影中少年岳沉天]形象参考，保持外貌、发型、服装、气质一致；第二张(@图2)为[顾残山留音]形象参考，保持外貌、发型、服装、气质一致；最后一张(@图3)为画面风格参考（光影、色调、材质质感）
        @角色引用:[倒影中少年岳沉天][顾残山留音]
        主体:顾残山留音在倒影中说话
        """;

        var result = PromptNameNormalizer.Normalize(
            prompt, "【单元1.8】镜中旧誓 少年岳沉天", Assets, ref current);

        Assert.Contains("[少年岳沉天]形象参考", result);
        Assert.Contains("[顾残山]形象参考", result);
        Assert.Contains("@角色引用:[少年岳沉天][顾残山]", result);
        Assert.Contains("主体:顾残山在倒影中说话", result);
        Assert.DoesNotContain("留音", result);
    }

    [Fact]
    public void NoRoleReference_IsRemoved()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        var prompt = """
        【第1集】【单元1.4】【镜头1.4-3】
        @角色引用:[无角色正面出镜]
        场景锚定引用:葬天台
        """;

        var result = PromptNameNormalizer.Normalize(
            prompt, "【单元1.4】前世岳沉天", Assets, ref current);

        Assert.DoesNotContain("@角色引用", result);
        Assert.DoesNotContain("无角色正面出镜", result);
        Assert.Contains("场景锚定引用:葬天台", result);
    }

    [Fact]
    public void NoRoleRefImage_IsRemovedFromReferenceLine()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        var prompt = """
        【第1集】【单元1.4】【镜头1.4-3】
        参考图:第一张(@图1)为[无角色正面出镜]形象参考，保持外貌、发型、服装、气质一致；倒数第二张(@图2)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图3)为画面风格参考（光影、色调、材质质感）
        """;

        var result = PromptNameNormalizer.Normalize(
            prompt, "【单元1.4】前世岳沉天", Assets, ref current);

        Assert.DoesNotContain("[无角色正面出镜]形象参考", result);
        Assert.Contains("[葬天台]场景参考", result);
        Assert.Contains("最后一张(@图3)为画面风格参考", result);
    }

    [Fact]
    public void CombatStateName_UsesCurrentForm()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        var prompt = """
        【第1集】【单元1.3】【镜头1.3-1】
        参考图:第一张(@图1)为[岳沉天战斗态]形象参考，保持外貌、发型、服装、气质一致；最后一张(@图2)为画面风格参考（光影、色调、材质质感）
        """;

        var result = PromptNameNormalizer.Normalize(
            prompt, "【单元1.3】前世战斗态", Assets, ref current);

        Assert.Contains("[前世岳沉天战斗态]形象参考", result);
    }

    [Fact]
    public void BareName_MapsToPresent_WhenUnitContextUsesPresentClues()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        PromptNameNormalizer.Normalize(
            "【第1集】【单元1.1】【镜头1.1-1】\n@角色引用:[岳沉天]",
            "【单元1.1】前世岳沉天独立阵心", Assets, ref current);
        var prompt = """
        【第1集】【单元1.5】【镜头1.5-1】
        参考图:第一张(@图1)为[岳沉天]形象参考，保持外貌、发型、服装、气质一致；最后一张(@图2)为画面风格参考（光影、色调、材质质感）
        @角色引用:[岳沉天]
        """;

        var result = PromptNameNormalizer.Normalize(
            prompt, "【单元1.5】荒郊破庙·破庙旧带 岳沉天被逐出青云宗", Assets, ref current);

        Assert.Contains("[少年岳沉天]形象参考", result);
        Assert.Contains("@角色引用:[少年岳沉天]", result);
        Assert.DoesNotContain("[前世岳沉天]形象参考", result);
    }

    [Fact]
    public void ParenthesizedYoungForm_ResetsCurrentFormToPresent()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        PromptNameNormalizer.Normalize(
            "【第1集】【单元1.1】【镜头1.1-1】\n@角色引用:[岳沉天]",
            "【单元1.1】前世岳沉天独立阵心", Assets, ref current);
        var prompt = """
        【第1集】【单元1.12】【镜头1.12-1】
        参考图:第一张(@图1)为[岳沉天]形象参考，保持外貌、发型、服装、气质一致；最后一张(@图2)为画面风格参考（光影、色调、材质质感）
        @角色引用:[岳沉天]
        """;

        var result = PromptNameNormalizer.Normalize(
            prompt, "【单元1.12】岳沉天（少年）双手捧起镇岳拳带", Assets, ref current);

        Assert.Contains("[少年岳沉天]形象参考", result);
        Assert.DoesNotContain("[前世岳沉天]形象参考", result);
    }

    [Fact]
    public void BareNameInjectedIntoAssetList_StillMapsToCurrentForm()
    {
        // 分镜自动补全曾把“岳沉天”“顾残山留音”当成独立资产混进名单，导致裸名精确命中后短路。
        var assetsWithBare = Assets.Concat(new[] { "岳沉天", "顾残山留音", "倒影中少年岳沉天" }).ToArray();
        var current = PromptNameNormalizer.DefaultCurrentForm(assetsWithBare);
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        参考图:第一张(@图1)为[岳沉天]形象参考，保持外貌、发型、服装、气质一致；第二张(@图2)为[顾残山留音]形象参考，保持外貌、发型、服装、气质一致；最后一张(@图3)为画面风格参考（光影、色调、材质质感）
        @角色引用:[岳沉天][顾残山留音]
        """;

        var result = PromptNameNormalizer.Normalize(prompt, "【单元1.2】葬天台 高潮/对决", assetsWithBare, ref current);

        Assert.Contains("[前世岳沉天]形象参考", result);
        Assert.Contains("[顾残山]形象参考", result);
        Assert.Contains("@角色引用:", result);
        Assert.Contains("[前世岳沉天]", result);
        Assert.Contains("[顾残山]", result);
        Assert.DoesNotMatch("\\[岳沉天\\](?![前少战])", result);
        Assert.DoesNotContain("[顾残山留音]", result);
    }

    [Fact]
    public void DialogueSpeech_KeepsOriginalName_WhileSpeakerLabelIsNormalized()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        var prompt = """
        【第1集】【单元1.11】【镜头1.11-1】
        对话:顾残山留音说"岳沉天，你一人再强，也强不过天下法统。"
        """;

        var result = PromptNameNormalizer.Normalize(prompt, "【单元1.11】七宗血契", Assets, ref current);

        Assert.Contains("对话:顾残山说", result);
        Assert.Contains("岳沉天，你一人再强", result);
        Assert.DoesNotContain("前世岳沉天，你一人再强", result);
    }

    [Fact]
    public void DialogueQuotedText_IsNeverRewritten_ForExactMainCharacterName()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-3】
        对话:太虚圣主说"岳沉天，你一人再强，也强不过天下法统。"
        """;

        var result = PromptNameNormalizer.Normalize(prompt, "【单元1.1】前世岳沉天 高潮/对决 葬天台", Assets, ref current);

        Assert.Contains("对话:太虚圣主说\"岳沉天，你一人再强，也强不过天下法统。\"", result);
        Assert.DoesNotContain("前世岳沉天，你一人再强", result);
    }

    [Fact]
    public void AtImageLine_UsesPresentForm_InPresentUnit()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        var prompt = """
        【第1集】【单元1.11】【镜头1.11-1】
        类型:悬疑/惊悚
        @图1 前世岳沉天人物；@图2 武圣遗骨道具；@图3 枯井遗府场景；@图4 光影质感。
        写实3D动画电影，4K，24fps。
        [0-2s]少年岳沉天双脚落地。
        """;

        var result = PromptNameNormalizer.Normalize(prompt, "【单元1.11】枯井遗府", Assets, ref current);

        Assert.Contains("@图1 [少年岳沉天]人物形象参考", result);
        Assert.DoesNotContain("@图1 [前世岳沉天]人物形象参考", result);
        Assert.Contains("@图2 [武圣遗骨]道具参考", result);
    }

    [Fact]
    public void BodyText_UsesPastForm_InPastUnit()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        var prompt = """
        【第1集】【单元1.5】【镜头1.5-1】
        类型:悬疑/惊悚
        [0-1s]低机位横移扫过少年岳沉天尸体。
        """;

        var result = PromptNameNormalizer.Normalize(prompt, "【单元1.5】断腿护印 岳沉天尸体", Assets, ref current);

        Assert.Contains("前世岳沉天尸体", result);
        Assert.DoesNotContain("少年岳沉天尸体", result);
    }

    [Fact]
    public void CompactTimeBlockDialogue_KeepsQuotedScriptVerbatim()
    {
        var current = PromptNameNormalizer.DefaultCurrentForm(Assets);
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:高潮/对决
        @图1 [前世岳沉天]人物；@图2 [葬天台]场景；@图3 [光影质感]光影质感
        [0-2s]大远景俯拍，太虚圣主立于云端说"岳沉天，你一人再强，也强不过天下法统。"。
        """;

        var result = PromptNameNormalizer.Normalize(prompt, "【单元1.2】太虚圣主", Assets, ref current);

        Assert.Contains("说\"岳沉天，你一人再强，也强不过天下法统。\"", result);
        Assert.DoesNotContain("前世岳沉天，你一人再强", result);
    }
}
