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
            AddParallelEvents(chain, primary, beat);

            chain.EndState = FirstNonEmpty(beat.Result, chain.EndState);
            chains.Add(chain);
        }

        return chains;
    }

    /// <summary>给每个 Beat 补充可并行发生的镜头/特效/环境反馈，提升单镜头信息密度。</summary>
    private static void AddParallelEvents(ActionChain chain, string primary, CombatBeat beat)
    {
        chain.Actions.Add(new MicroAction
        {
            Id = "P" + chain.Actions.Count.ToString("00"),
            BeatId = chain.CombatBeatIds[^1],
            Actor = primary,
            Action = CameraCue(beat.ActionType, beat.Intensity),
            Kind = "Camera",
            IsParallel = true
        });
        chain.Actions.Add(new MicroAction
        {
            Id = "P" + (chain.Actions.Count + 1).ToString("00"),
            BeatId = chain.CombatBeatIds[^1],
            Actor = "环境",
            Action = AirFeedback(beat.Intensity),
            Kind = "Environment",
            IsParallel = true
        });
    }

    /// <summary>机位-动作语义绑定：运镜由动作类型决定，禁止所有 Beat 共用一句机位。</summary>
    private static string CameraCue(string actionType, string intensity)
    {
        var cue = actionType switch
        {
            "Surround" => "缓慢环绕并缓缓压低，营造压迫感",
            "QuickStrike" => "低角度极速推镜或贴地跟拍，跟随突进轨迹",
            "DodgeCounter" => "手持快切、甩镜跟随闪避与反击身形",
            "CloseCounter" => "环绕横移，中近景锁住贴身交锋",
            "GrappleCounter" => "近景跟随纠缠，锁扣瞬间轻微晃动",
            "PinDown" => "俯冲压镜跟拍，压住被压制方",
            "RetreatHold" => "后拉跟拍，维持撤离的距离感",
            "AmbushBreak" => "贴地跟拍突进，撕开缺口后拉高俯拍",
            "AoeBreak" => "径向拉远并镜头震颤，看冲击向四面扩散",
            "FinalBreak" => "径向拉远并镜头震颤，推进至接触点",
            _ => "环绕横移跟随交锋"
        };
        return intensity is "高" or "极高" or "High" or "Extreme"
            ? cue + "；命中瞬间推进至接触点顿帧 1~2 帧"
            : cue;
    }

    /// <summary>空气动力反馈按烈度分级，禁止所有 Beat 共用同一句环境描写。</summary>
    private static string AirFeedback(string intensity) => intensity switch
    {
        "极高" or "Extreme" => "环形冲击波自接触点横扫，地面放射状崩裂掀飞，周围物体被气浪掀翻",
        "高" or "High" => "接触点炸开音爆气刃与弧形气流，碎石与尘土震起，衣袍发丝被冲击掀飞",
        "中" or "Medium" => "接触点荡开可见空气波纹，衣袍发丝随动作翻动，地面轻微震动",
        _ => "拳风带起细微气流扰动，衣摆与发丝轻扬，脚下浮尘轻起"
    };

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
