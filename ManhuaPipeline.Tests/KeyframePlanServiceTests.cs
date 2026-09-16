using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

/// <summary>
/// L3 关键帧层的「选点」是系统确定性行为（不调 LLM、不碰数据库），
/// 因此这里直接测选点规则：8-16 张/集、覆盖开场与收尾、打斗取中段、重跑稳定。
/// </summary>
public class KeyframePlanServiceTests
{
    // 选点不依赖任何注入的服务（只在生成/落库时用到），因此依赖传 null 即可
    private static KeyframePlanService NewService() => new(null!, null!, null!, null!);

    private static StoryboardFrame Frame(
        int frameId, int episode, string unit, int unitOrder, string shotNumber,
        string? unitType = null, string? newInformation = null, string? startState = null,
        string? combatBeatIds = null)
        => new()
        {
            FrameId = frameId,
            ProjectId = 1,
            EpisodeNumber = episode,
            UnitNumber = unit,
            UnitOrder = unitOrder,
            SortOrder = frameId,
            FrameNumber = frameId,
            ShotNumber = shotNumber,
            UnitType = unitType,
            NewInformation = newInformation,
            StartState = startState,
            CombatBeatIds = combatBeatIds,
            Scene = "葬天台",
            Description = "镜头" + shotNumber + "的画面描述"
        };

    [Fact]
    public void SelectCandidates_EmptyInput_ReturnsEmpty()
    {
        var service = NewService();

        Assert.Empty(service.SelectCandidates(new List<StoryboardFrame>()));
        Assert.Empty(service.SelectCandidates(null!));
    }

    [Fact]
    public void SelectCandidates_CoversFirstAndLastShotOfEachEpisode()
    {
        var frames = Enumerable.Range(1, 10)
            .Select(i => Frame(i, 1, "1." + i, i, "1." + i + "-1"))
            .ToList();

        var result = NewService().SelectCandidates(frames);
        var labels = result.Select(c => c.ShotLabel).ToList();

        Assert.Contains("1.1-1", labels);
        Assert.Contains("1.10-1", labels);
        Assert.All(result, c => Assert.Equal(1, c.EpisodeNumber));
    }

    [Fact]
    public void SelectCandidates_SingleEpisode_PadsUpToMinimumEight()
    {
        // 12 镜分 3 个单元、无状态字段：规则只命中开场/收尾/单元首镜（3 张），需要靠均匀补点到 8 张
        var frames = new List<StoryboardFrame>();
        for (var i = 1; i <= 12; i++)
        {
            var unitIndex = (i - 1) / 4 + 1;
            var unit = "1." + unitIndex;
            frames.Add(Frame(i, 1, unit, unitIndex, unit + "-" + i));
        }

        var result = NewService().SelectCandidates(frames);

        Assert.Equal(KeyframePlanService.MinPerEpisode, result.Count);
        Assert.Equal(result.Select(c => c.ShotLabel).Distinct().Count(), result.Count);
    }

    [Fact]
    public void SelectCandidates_TooFewFrames_ReturnsAllFramesWithoutPadding()
    {
        var frames = Enumerable.Range(1, 3)
            .Select(i => Frame(i, 1, "1." + i, i, "1." + i + "-1"))
            .ToList();

        var result = NewService().SelectCandidates(frames);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void SelectCandidates_DenseEpisode_TrimsToMaximumSixteen()
    {
        // 40 镜每镜都有新信息 → 命中 40 个候选，必须裁剪到 16 张以内
        var frames = Enumerable.Range(1, 40)
            .Select(i => Frame(i, 1, "1." + i, i, "1." + i + "-1", newInformation: "新信息" + i))
            .ToList();

        var result = NewService().SelectCandidates(frames);

        Assert.True(result.Count <= KeyframePlanService.MaxPerEpisode, "每集不得超过 16 张");
        Assert.Contains(result, c => c.ShotLabel == "1.1-1");
        Assert.Contains(result, c => c.ShotLabel == "1.40-1");
    }

    [Fact]
    public void SelectCandidates_CombatUnit_PicksMiddleShotInsteadOfFirst()
    {
        var frames = new List<StoryboardFrame>
        {
            Frame(1, 1, "1.1", 1, "1.1-1"),
            Frame(2, 1, "1.2", 2, "1.2-1", unitType: "打斗/对决"),
            Frame(3, 1, "1.2", 2, "1.2-2", unitType: "打斗/对决"),
            Frame(4, 1, "1.2", 2, "1.2-3", unitType: "打斗/对决"),
            Frame(5, 1, "1.2", 2, "1.2-4", unitType: "打斗/对决"),
            Frame(6, 1, "1.2", 2, "1.2-5", unitType: "打斗/对决"),
            Frame(7, 1, "1.3", 3, "1.3-1")
        };

        var result = NewService().SelectCandidates(frames);
        var combat = result.Where(c => c.UnitNumber == "1.2").ToList();

        Assert.Contains(combat, c => c.Reason.Contains("打斗", StringComparison.Ordinal));
        Assert.Contains("1.2-3", combat.Select(c => c.ShotLabel));
    }

    [Fact]
    public void SelectCandidates_StateAndInformationFields_RaiseAnchors()
    {
        var frames = Enumerable.Range(1, 10)
            .Select(i => Frame(i, 1, "1." + i, i, "1." + i + "-1"))
            .ToList();
        frames[4].StartState = "玉佩完整挂在腰间";
        frames[4].EndState = "玉佩碎裂落地";
        frames[6].NewInformation = "主角身份暴露";

        var result = NewService().SelectCandidates(frames);

        Assert.Contains(result, c => c.ShotLabel == "1.5-1" && c.Reason.Contains("状态"));
        Assert.Contains(result, c => c.ShotLabel == "1.7-1" && c.Reason.Contains("信息增量"));
    }

    [Fact]
    public void SelectCandidates_MultipleEpisodes_KeepsEpisodeOrderAndBounds()
    {
        var frames = new List<StoryboardFrame>();
        for (var ep = 1; ep <= 3; ep++)
            for (var i = 1; i <= 12; i++)
                frames.Add(Frame(ep * 100 + i, ep, ep + "." + i, i, ep + "." + i + "-1"));

        var result = NewService().SelectCandidates(frames);
        var episodes = result.Select(c => c.EpisodeNumber).ToList();

        Assert.Equal(episodes.OrderBy(e => e), episodes);
        for (var ep = 1; ep <= 3; ep++)
        {
            var count = result.Count(c => c.EpisodeNumber == ep);
            Assert.True(count >= KeyframePlanService.MinPerEpisode && count <= KeyframePlanService.MaxPerEpisode,
                "第" + ep + "集应为 8-16 张，实际 " + count);
        }
    }

    [Fact]
    public void SelectCandidates_IsStableAcrossRuns()
    {
        var frames = Enumerable.Range(1, 20)
            .Select(i => Frame(i, 1, "1." + (i % 5 + 1), i, "1." + (i % 5 + 1) + "-" + i))
            .ToList();

        var service = NewService();
        var first = service.SelectCandidates(frames).Select(c => c.EpisodeNumber + "|" + c.ShotLabel).ToList();
        var second = service.SelectCandidates(frames).Select(c => c.EpisodeNumber + "|" + c.ShotLabel).ToList();

        Assert.Equal(first, second);
    }
}
