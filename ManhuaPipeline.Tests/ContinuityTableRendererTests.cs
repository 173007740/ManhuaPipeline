using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class ContinuityTableRendererTests
{
    [Fact]
    public void RenderSceneSpace_EmitsDirectionsAndForbiddenChanges()
    {
        var text = ContinuityTableRenderer.RenderSceneSpace(new List<SceneSpaceEntry>
        {
            new()
            {
                Scene = "青云客栈正厅",
                Left = "木质楼梯",
                Right = "长条柜台",
                Entrance = "正门",
                ActionAxis = "A 在左、B 在右",
                FixedProps = new List<string> { "柜台（右）" },
                ForbiddenChanges = new List<string> { "后厨小门不得消失" }
            }
        });

        Assert.Contains("【场景】青云客栈正厅", text);
        Assert.Contains("左侧：木质楼梯", text);
        Assert.Contains("右侧：长条柜台", text);
        Assert.Contains("动作轴线：A 在左、B 在右", text);
        Assert.Contains("禁止改变：后厨小门不得消失", text);
    }

    [Fact]
    public void RenderPropState_JoinsTimelineInOrder()
    {
        var text = ContinuityTableRenderer.RenderPropState(new List<PropStateTimelineEntry>
        {
            new()
            {
                Prop = "碎玉佩",
                States = new List<PropStatePoint>
                {
                    new() { At = "unit_1", State = "完整" },
                    new() { At = "unit_3", State = "碎裂落地" }
                },
                Forbidden = new List<string> { "碎后不得再出现完整形态" }
            }
        });

        Assert.Contains("【道具】碎玉佩", text);
        Assert.Contains("unit_1 完整 → unit_3 碎裂落地", text);
        Assert.Contains("禁止：碎后不得再出现完整形态", text);
    }

    [Fact]
    public void RenderClueReveal_OrdersByRevealOrder()
    {
        var text = ContinuityTableRenderer.RenderClueReveal(new List<ClueRevealEntry>
        {
            new() { Clue = "后揭纹样", RevealOrder = 2, RevealAt = "unit_5" },
            new() { Clue = "先揭印信", RevealOrder = 1, RevealAt = "unit_2" }
        });

        Assert.True(text.IndexOf("先揭印信", StringComparison.Ordinal) < text.IndexOf("后揭纹样", StringComparison.Ordinal));
        Assert.Contains("【线索1】先揭印信", text);
        Assert.Contains("【线索2】后揭纹样", text);
    }

    [Fact]
    public void Render_DispatchesByTableType_AndToleratesBrokenInput()
    {
        const string json = """[{"prop":"断岳","states":[{"at":"unit_2","state":"脱手"}],"mustAppear":["unit_2"],"forbidden":[]}]""";
        Assert.Contains("【道具】断岳", ContinuityTableRenderer.Render(ContinuityTableTypes.PropState, json));

        Assert.Equal("", ContinuityTableRenderer.Render(ContinuityTableTypes.PropState, "{不是合法 JSON"));
        Assert.Equal("", ContinuityTableRenderer.Render(ContinuityTableTypes.PropState, null));
        Assert.Equal("", ContinuityTableRenderer.Render(ContinuityTableTypes.PropState, "{}"));
    }

    [Fact]
    public void RenderActionCausality_SkipsEntriesWithoutContent()
    {
        var text = ContinuityTableRenderer.RenderActionCausality(new List<ActionCausalityEntry>
        {
            new() { UnitNumber = "1.1", NewInformation = "主角身份暴露" },
            new() { UnitNumber = "1.2" }
        });

        Assert.Contains("1.1  新信息：主角身份暴露", text);
        Assert.DoesNotContain("1.2", text);
    }

    [Fact]
    public void RenderTransitionMotive_UsesMotiveTypeAndDetail()
    {
        var text = ContinuityTableRenderer.RenderTransitionMotive(new List<TransitionMotiveEntry>
        {
            new() { FromUnit = "1.1", ToUnit = "1.2", MotiveType = "声音桥", Detail = "碎裂声延续" }
        });

        Assert.Contains("1.1 → 1.2", text);
        Assert.Contains("动机：声音桥", text);
        Assert.Contains("说明：碎裂声延续", text);
    }
}
