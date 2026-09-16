using System.Linq;
using ManhuaPipeline.Models;
using ManhuaPipeline.Models.Combat;
using ManhuaPipeline.Services.Combat;
using Xunit;

namespace ManhuaPipeline.Tests;

public class CombatGrammarEngineLockedTests
{
    private static CombatRequest Request() =>
        new()
        {
            Tier = "T2",
            Style = "BODY_CULTIVATOR",
            Intent = "Pressure",
            BeatCount = 6,
            InitialDistance = "Mid",
            EndMode = "Offensive"
        };

    [Fact]
    public void AvailableLockedIds_ArePlacedIntoChain()
    {
        var engine = new CombatGrammarEngine();
        var ids = new[] { "MOVE_DASH_IN_001", "ATK_JAB_001" };

        var sequence = engine.GenerateLocked(Request(), ids);

        var actionIds = sequence.Beats.Select(b => b.Action.Id).ToList();
        Assert.Contains("MOVE_DASH_IN_001", actionIds);
        Assert.Contains("ATK_JAB_001", actionIds);
        Assert.True(actionIds.Count >= ids.Length);
    }

    [Fact]
    public void UnknownAndUnavailableIds_AreSkippedWithoutError()
    {
        var engine = new CombatGrammarEngine();

        var sequence = engine.GenerateLocked(Request(), new[] { "NO_SUCH_ID", "ATK_JAB_001" });

        var actionIds = sequence.Beats.Select(b => b.Action.Id).ToList();
        Assert.DoesNotContain("NO_SUCH_ID", actionIds);
        Assert.Contains("ATK_JAB_001", actionIds);
    }

    [Fact]
    public void EmptyIds_FallsBackToGenerate()
    {
        var engine = new CombatGrammarEngine();

        var locked = engine.GenerateLocked(Request(), System.Array.Empty<string>());
        var plain = engine.Generate(Request());

        Assert.NotEmpty(locked.Beats);
        Assert.NotEmpty(plain.Beats);
    }

    [Fact]
    public void ExpandFromActionPlan_KnownIds_CreateOrderedBeats()
    {
        var engine = new CombatGrammarEngine();
        var plan = new DirectorActionPlan
        {
            PrimaryFighterId = "林烬",
            EnemyIds = ["弟子一", "弟子二"],
            CombatGrammarIds = ["T3_SURROUND_ATTACK", "T1_DODGE_COUNTER"]
        };

        var result = engine.ExpandFromActionPlan(plan);

        Assert.Equal(2, result.Beats.Count);
        Assert.Equal(1, result.Beats[0].Index);
        Assert.Equal(2, result.Beats[1].Index);
        Assert.Equal("T3_SURROUND_ATTACK", result.Beats[0].GrammarId);
        Assert.Contains("林烬", result.Beats[1].ActionDescription);
        Assert.Empty(result.InvalidGrammarIds);
    }

    [Fact]
    public void ExpandFromActionPlan_UnknownIds_ReportedAndSkipped()
    {
        var engine = new CombatGrammarEngine();
        var plan = new DirectorActionPlan
        {
            CombatGrammarIds = ["NO_SUCH_ID", "T4_AOE_BREAK"]
        };

        var result = engine.ExpandFromActionPlan(plan);

        Assert.Single(result.Beats);
        Assert.Equal("T4_AOE_BREAK", result.Beats[0].GrammarId);
        Assert.Contains("NO_SUCH_ID", result.InvalidGrammarIds);
    }
}
