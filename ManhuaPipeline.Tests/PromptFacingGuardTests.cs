using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class PromptFacingGuardTests
{
    private static readonly string[] Assets =
    {
        "前世岳沉天", "寒魄仙姥", "七宗宗主", "太虚圣主"
    };

    [Fact]
    public void CombatShot_WithTwoChars_MissingRelation_AppendsFacingConstraint()
    {
        var prompt = """
        【第1集】【单元1.6】【镜头1.6-3】
        类型:打斗/动作
        参考图:第一张(@图1)为[寒魄仙姥]形象参考，保持外貌、发型、服装、气质一致；最后一张(@图2)为画面风格参考（光影、色调、材质质感）
        [0-3s]莲印蓄势：寒魄仙姥位于画面中央偏右，周身寒息凝聚；前世岳沉天静立于画面右侧约二十丈外。
        [3-7s]法相凝天：寒魄仙姥冰蓝法相凝出，气势如渊；前世岳沉天目光锁定。
        [7-11s]俯冲定势：前世岳沉天面朝画面左前方，目光遥望寒魄仙姥方向。
        灯光：冷月夜光
        视频风格：写实3D国漫
        """;

        var result = PromptFacingGuard.Apply(prompt, Assets);

        Assert.Contains("站位朝向约束：寒魄仙姥与前世岳沉天保持正面相对、彼此相向", result);
        Assert.Contains("禁止背对背站立", result);
        Assert.DoesNotContain("七宗宗主", result);
    }

    [Fact]
    public void Shot_WithExistingRelation_NotDuplicated()
    {
        var prompt = """
        【第1集】【单元1.6】【镜头1.6-3】
        类型:打斗/动作
        [0-3s]寒魄仙姥与前世岳沉天相对而立，彼此对峙。
        """;

        var result = PromptFacingGuard.Apply(prompt, Assets);

        Assert.DoesNotContain("站位朝向约束", result);
    }

    [Fact]
    public void NonConfrontationDialogue_NoFacingConstraint()
    {
        var prompt = """
        【第1集】【单元1.5】【镜头1.5-1】
        类型:文戏/情感
        [0-3s]少年岳沉天独自立于破庙前，背对画面望向远方。
        """;

        var result = PromptFacingGuard.Apply(prompt, Assets);

        Assert.DoesNotContain("站位朝向约束", result);
    }

    [Fact]
    public void SingleCharacterInBody_NoConstraint()
    {
        var prompt = """
        【第1集】【单元1.6】【镜头1.6-3】
        类型:打斗/动作
        [0-3s]前世岳沉天蓄势凝拳。
        """;

        var result = PromptFacingGuard.Apply(prompt, Assets);

        Assert.DoesNotContain("站位朝向约束", result);
    }
}
