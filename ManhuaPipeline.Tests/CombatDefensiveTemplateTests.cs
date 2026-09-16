using ManhuaPipeline.Models;
using ManhuaPipeline.Models.Combat;
using ManhuaPipeline.Services;
using ManhuaPipeline.Services.Combat;
using Xunit;

namespace ManhuaPipeline.Tests;

public class CombatDefensiveTemplateTests
{
    [Fact]
    public void DefensiveContext_SetsDefensiveEndMode()
    {
        var intent = new CombatIntent
        {
            CombatForm = "被围困",
            Objective = "硬抗围攻",
            Result = "被击退",
            Intensity = 4,
            Duration = 15
        };

        var request = CombatIntentMapper.ToCombatRequest(intent);

        Assert.Equal("Defensive", request.EndMode);
    }

    [Fact]
    public void StandoffContext_SetsStandoffEndMode()
    {
        var intent = new CombatIntent { CombatForm = "对峙", Result = "蓄势", Duration = 11 };

        var request = CombatIntentMapper.ToCombatRequest(intent);

        Assert.Equal("Standoff", request.EndMode);
        Assert.True(request.BeatCount <= 4);
    }

    [Fact]
    public void OffensiveContext_KeepsOffensiveEndMode()
    {
        var intent = new CombatIntent { CombatForm = "拳脚对拼", Result = "压制", Duration = 11 };

        var request = CombatIntentMapper.ToCombatRequest(intent);

        Assert.Equal("Offensive", request.EndMode);
    }

    [Fact]
    public void DefensiveContext_SelectsBuiltInDefensiveTemplate_EvenWithEmptyLibrary()
    {
        var intent = new CombatIntent
        {
            CombatForm = "被围",
            Objective = "突围",
            Duration = 15,
            Intensity = 4
        };

        var template = CombatPlanSelector.SelectFightTemplate(
            new List<FightTemplateItem>(), intent,
            unitText: "岳沉天被七宗弟子围困，护体硬抗，撑住后突围");

        Assert.NotNull(template);
        Assert.Equal(0, template!.FightTemplateId);
        Assert.Equal(15, template.Duration);
        Assert.Contains("突围", template.ActionPrompt);
        Assert.Contains("顿帧", template.ConstraintPrompt);
    }

    [Fact]
    public void DefensiveTemplate_CanBeResolvedByName_ForStageNineLocking()
    {
        var template = CombatPlanSelector.TryResolveDefensiveTemplateByName("被围硬抗 · 围杀突围");

        Assert.NotNull(template);
        Assert.Equal("被围硬抗 · 围杀突围", template!.Name);
        Assert.Equal(0, template.FightTemplateId);
    }

    [Fact]
    public void DefensiveGrammar_ExcludesFinishers()
    {
        var request = new CombatRequest
        {
            Tier = "T4",
            Style = "BODY_CULTIVATOR",
            BeatCount = 8,
            EndMode = "Defensive",
            InitialDistance = "Close"
        };

        var actions = new CombatGrammarEngine().GetAvailableActions(request);

        Assert.NotEmpty(actions);
        Assert.All(actions, a => Assert.NotEqual("Finisher", a.Category));
        Assert.All(actions, a => Assert.NotEqual("Knockdown", a.EnemyResultState));
    }
}
