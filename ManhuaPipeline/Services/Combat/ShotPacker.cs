using System.Text;
using ManhuaPipeline.Models;
using ManhuaPipeline.Models.Combat;
using ManhuaPipeline.Services.Director;

namespace ManhuaPipeline.Services.Combat;

/// <summary>
/// Director V2 ShotPacker：把 ActionChain 打包成一个高密度视频镜头。
/// 不强制 5 秒——建议时长只作参考，最终 5/11/15 由 LLM 按内容密度自行决定。
/// 一个镜头内可以拆时间轴片段，但这些片段属于同一个视频，禁止拆成独立镜头。
/// </summary>
public static class ShotPacker
{
    public static ShotDensityProfile BuildProfile(StageUnit unit, DirectorPlan? plan)
    {
        var intensity = plan?.IntensityLevel ?? 3;
        var isCombat = unit.IsCombat;
        var profile = new ShotDensityProfile
        {
            ActionDensity = !isCombat ? "LOW" : intensity switch
            {
                >= 8 => "EXTREME",
                >= 6 => "HIGH",
                _ => "MEDIUM"
            },
            VisualDensity = !isCombat ? "MEDIUM" : intensity >= 7 ? "EXTREME" : "HIGH",
            VfxDensity = string.Equals(plan?.VfxPeakPhase, "None", StringComparison.OrdinalIgnoreCase)
                ? "LOW"
                : intensity >= 7 ? "HIGH" : "MEDIUM",
            ShotRhythm = !isCombat ? "Slow" : intensity switch
            {
                >= 8 => "Burst",
                >= 6 => "Fast",
                _ => "Normal"
            },
            ActionComplexity = Math.Clamp(intensity / 2 + (isCombat ? 1 : 0), 1, 5),
            SpatialChange = DirectorValidator.IsSoloOrMovementCombat(unit) ? 3 : 2,
            ParticipantCount = Math.Max(1, 1 + (string.IsNullOrWhiteSpace(plan?.SecondarySubject) ? 0 : 1))
        };
        return profile;
    }

    public static List<PackedShot> Pack(
        IReadOnlyList<ActionChain> chains,
        StageUnit unit,
        DirectorPlan? plan = null,
        ShotDensityProfile? profile = null)
    {
        if (chains == null || chains.Count == 0) return [];

        profile ??= BuildProfile(unit, plan);
        var suggestedDuration = unit.Duration is 5 or 11 or 15 ? unit.Duration : 11;
        var shot = new PackedShot
        {
            ShotId = "1",
            DurationSeconds = suggestedDuration,
            CombatBeatIds = chains.SelectMany(c => c.CombatBeatIds).Distinct().ToList(),
            ActionChainIds = chains.Select(c => c.Id).ToList(),
            StartState = chains[0].StartState,
            EndState = chains[^1].EndState
        };
        shot.Timeline = BuildTimeline(chains.SelectMany(c => c.Actions).ToList(), suggestedDuration, profile);
        return [shot];
    }

    /// <summary>把一个镜头内部的时间轴片段拼出来：顺序动作 + 并行事件，供分镜 LLM 参考。</summary>
    public static List<ShotTimelineSegment> BuildTimeline(
        IReadOnlyList<MicroAction> actions,
        int durationSeconds,
        ShotDensityProfile? profile = null)
    {
        var segments = new List<ShotTimelineSegment>();
        if (actions == null || actions.Count == 0) return segments;

        var duration = durationSeconds is 5 or 11 or 15 ? durationSeconds : 11;
        var boundaries = duration switch
        {
            5 => new[] { (0.0, 1.0), (1.0, 2.0), (2.0, 3.0), (3.0, 4.0), (4.0, 5.0) },
            11 => new[] { (0.0, 2.0), (2.0, 4.0), (4.0, 6.0), (6.0, 8.0), (8.0, 11.0) },
            _ => new[] { (0.0, 2.0), (2.0, 4.0), (4.0, 6.0), (6.0, 8.0), (8.0, 10.0), (10.0, 12.0), (12.0, 15.0) }
        };

        var sequential = actions.Where(a => !a.IsParallel).ToList();
        var parallel = actions.Where(a => a.IsParallel).ToList();
        var sequentialPerSegment = Math.Max(1, (int)Math.Ceiling(sequential.Count / (double)boundaries.Length));
        var parallelPerSegment = Math.Max(1, (int)Math.Ceiling(parallel.Count / (double)boundaries.Length));

        for (var i = 0; i < boundaries.Length; i++)
        {
            var segSequential = sequential
                .Skip(i * sequentialPerSegment)
                .Take(sequentialPerSegment)
                .Select(a => a.Actor + " " + a.Action)
                .ToList();
            var segParallel = parallel
                .Skip(i * parallelPerSegment)
                .Take(parallelPerSegment)
                .Select(a => a.Action)
                .ToList();
            segments.Add(new ShotTimelineSegment
            {
                SegmentIndex = i + 1,
                StartSecond = boundaries[i].Item1,
                EndSecond = boundaries[i].Item2,
                SequentialActions = segSequential,
                ParallelEvents = segParallel,
                EndState = segSequential.Count > 0 ? segSequential[^1] : ""
            });
        }

        return segments;
    }

    /// <summary>把打包结果转成给分镜 LLM 的文本：建议时长可调，时间轴是同一镜头内部片段。</summary>
    public static string BuildPlanText(IReadOnlyList<PackedShot> shots)
    {
        if (shots == null || shots.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var shot in shots)
        {
            sb.AppendLine($"Shot{shot.ShotId}（建议 {shot.DurationSeconds}秒；LLM 可按内容密度调整为 5/11/15）");
            sb.AppendLine("覆盖节拍: " + string.Join(",", shot.CombatBeatIds));
            sb.AppendLine("覆盖动作链: " + string.Join(",", shot.ActionChainIds));
            foreach (var seg in shot.Timeline)
            {
                sb.AppendLine($"{FormatSecond(seg.StartSecond)}-{FormatSecond(seg.EndSecond)}s:");
                if (seg.SequentialActions.Count > 0)
                    sb.AppendLine("  顺序动作: " + string.Join("；", seg.SequentialActions));
                if (seg.ParallelEvents.Count > 0)
                    sb.AppendLine("  并行事件: " + string.Join("；", seg.ParallelEvents));
            }
            sb.AppendLine("结束状态: " + shot.EndState);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatSecond(double value) => value == Math.Floor(value)
        ? value.ToString("0")
        : value.ToString("0.#");
}
