using ManhuaPipeline.Models;
using ManhuaPipeline.Models.Combat;

namespace ManhuaPipeline.Services.Combat;

/// <summary>
/// Director V2 镜头密度升级：把 CombatBeat 展开成 MicroAction，
/// 再把存在连续因果关系的 MicroAction 组合成 ActionChain。
/// 关键原则：CombatBeat ≠ Shot，MicroAction ≠ Shot。
/// </summary>
public static class ActionChainBuilder
{
    public static List<ActionChain> Build(IReadOnlyList<CombatBeat> beats, DirectorActionPlan? plan = null)
    {
        if (beats == null || beats.Count == 0) return [];

        var chains = new List<ActionChain>();
        var microIndex = 0;
        var primary = FirstNonEmpty(plan?.PrimaryFighterId, beats.FirstOrDefault()?.AttackerId, "主角");
        var enemies = plan?.EnemyIds?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        if (enemies.Count == 0)
        {
            foreach (var beat in beats)
            {
                foreach (var target in beat.TargetIds ?? [])
                {
                    var t = target?.Trim();
                    if (!string.IsNullOrWhiteSpace(t) && !enemies.Contains(t, StringComparer.OrdinalIgnoreCase))
                        enemies.Add(t);
                }
            }
        }
        if (enemies.Count == 0) enemies.Add("敌方");
        var enemyText = string.Join("、", enemies.Take(3)) + (enemies.Count > 3 ? " 等" + enemies.Count + "人" : "");

        for (var i = 0; i < beats.Count; i++)
        {
            var beat = beats[i];
            var beatId = $"Beat{beat.Index:00}";
            var chain = new ActionChain
            {
                Id = $"AC{i + 1:00}",
                CombatBeatIds = [beatId],
                PrimarySubjectId = primary,
                ParticipantIds = new List<string> { primary, enemyText },
                FlowDirection = FirstNonEmpty(beat.SpatialRelation, "近身交错"),
                Intensity = FirstNonEmpty(beat.Intensity, "中"),
                StartState = i == 0 ? $"战斗开始，{primary} 与 {enemyText} 拉开距离" : chains[^1].EndState,
                CanPackTogether = true
            };

            foreach (var clause in SplitClauses(beat.ActionDescription))
            {
                chain.Actions.Add(new MicroAction
                {
                    Id = $"A{++microIndex:00}",
                    BeatId = beatId,
                    Actor = primary,
                    Action = clause,
                    Kind = "Action"
                });
            }
            foreach (var clause in SplitClauses(beat.DefenseResponse))
            {
                chain.Actions.Add(new MicroAction
                {
                    Id = $"A{++microIndex:00}",
                    BeatId = beatId,
                    Actor = enemyText,
                    Action = clause,
                    Kind = "OpponentReaction"
                });
            }
            foreach (var clause in SplitClauses(beat.Result))
            {
                chain.Actions.Add(new MicroAction
                {
                    Id = $"A{++microIndex:00}",
                    BeatId = beatId,
                    Actor = enemyText,
                    Action = clause,
                    Kind = "Environment"
                });
            }
            AddParallelEvents(chain, primary, beat.Intensity);

            chain.EndState = FirstNonEmpty(beat.Result, chain.EndState);
            chains.Add(chain);
        }

        return chains;
    }

    /// <summary>给每个 Beat 补充可并行发生的镜头/特效/环境反馈，提升单镜头信息密度。</summary>
    private static void AddParallelEvents(ActionChain chain, string primary, string intensity)
    {
        chain.Actions.Add(new MicroAction
        {
            Id = "P" + chain.Actions.Count.ToString("00"),
            BeatId = chain.CombatBeatIds[^1],
            Actor = primary,
            Action = "镜头跟随主体动作，保持连续机位",
            Kind = "Camera",
            IsParallel = true
        });
        chain.Actions.Add(new MicroAction
        {
            Id = "P" + (chain.Actions.Count + 1).ToString("00"),
            BeatId = chain.CombatBeatIds[^1],
            Actor = "环境",
            Action = intensity is "高" or "极高" or "High" or "Extreme"
                ? "气浪卷起尘土碎石，衣袍与发丝随冲击翻动"
                : "衣袍随动作翻动，地面轻微震动",
            Kind = "Environment",
            IsParallel = true
        });
    }

    private static List<string> SplitClauses(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text
            .Replace("→", "，", StringComparison.Ordinal)
            .Split(new[] { '，', '、', '；', '。', ',', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length >= 2)
            .ToList();
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
