using ManhuaPipeline.Models;
using ManhuaPipeline.Models.Combat;

namespace ManhuaPipeline.Services.Combat;

public sealed record ExpansionResult(List<CombatBeat> Beats, List<string> InvalidGrammarIds);

public class CombatGrammarEngine
{
    private readonly CombatRuleEngine _rules = new();
    private readonly CombatWeightCalculator _weights = new();

    public CombatSequence Generate(CombatRequest request)
    {
        request ??= new CombatRequest();
        request.BeatCount = Math.Clamp(request.BeatCount, 1, 60);
        var tier = NormalizeTier(request.Tier);
        var style = ResolveStyle(request.Style);
        var preset = ResolvePreset(request, tier);
        var state = _rules.CreateInitialState(request);
        var sequence = CreateSequence(request, tier, style, state);

        FillBeats(sequence, state, request, tier, preset, style, null);
        sequence.FinalState = state;
        return sequence;
    }

    /// <summary>
    /// 按导演点名的战斗语法 ID 锁定动作链：先逐拍应用可用的指定动作，
    /// 不足部分再由引擎按规则补齐。未知或当前状态不可用的 ID 自动跳过。
    /// </summary>
    public CombatSequence GenerateLocked(CombatRequest request, IReadOnlyCollection<string> grammarIds)
    {
        request ??= new CombatRequest();
        request.BeatCount = Math.Clamp(request.BeatCount, 1, 60);

        var ids = (grammarIds ?? Array.Empty<string>())
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0) return Generate(request);

        var tier = NormalizeTier(request.Tier);
        var style = ResolveStyle(request.Style);
        var preset = ResolvePreset(request, tier);
        var state = _rules.CreateInitialState(request);
        var sequence = CreateSequence(request, tier, style, state);

        var lockedSkillIds = CombatActionCatalog.Actions
            .Where(a => a.Category == "Skill" && ids.Contains(a.Id, StringComparer.OrdinalIgnoreCase))
            .Select(a => a.Id)
            .ToList();
        if (lockedSkillIds.Count > 0)
        {
            request.AllowedSkillActionIds = request.AllowedSkillActionIds
                .Concat(lockedSkillIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        CombatAction? previous = null;
        foreach (var id in ids)
        {
            var action = CombatActionCatalog.Actions.FirstOrDefault(a =>
                string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            if (action == null) continue;

            var available = FilterActionsForRequest(
                _rules.GetAvailableActions(state, tier, preset, style), request);
            if (!available.Any(a => string.Equals(a.Id, action.Id, StringComparison.OrdinalIgnoreCase)))
                continue;

            state = ApplyBeat(sequence, state, action);
            previous = action;
            if (action.Category == "Finisher" && state.Enemy.State is "Knockdown" or "Airborne")
                break;
        }

        if (sequence.Beats.Count == 0) return Generate(request);

        FillBeats(sequence, state, request, tier, preset, style, previous);
        sequence.FinalState = state;
        return sequence;
    }

    /// <summary>V1.5：把导演 ActionPlan 展开成逐拍 CombatBeat，未知 Grammar 自动跳过。</summary>
    public ExpansionResult ExpandFromActionPlan(DirectorActionPlan plan)
    {
        if (plan == null) return new ExpansionResult(new List<CombatBeat>(), new List<string>());

        var ids = (plan.CombatGrammarIds ?? new List<string>())
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var primary = string.IsNullOrWhiteSpace(plan.PrimaryFighterId) ? "主角" : plan.PrimaryFighterId.Trim();
        var enemies = (plan.EnemyIds ?? new List<string>())
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .ToList();
        if (enemies.Count == 0) enemies = ["敌方"];

        var beats = new List<CombatBeat>();
        var invalid = new List<string>();
        foreach (var id in ids)
        {
            var pattern = CombatGrammarCatalog.Get(id);
            if (pattern == null)
            {
                invalid.Add(id);
                continue;
            }

            var enemyText = string.Join("、", enemies.Take(3)) + (enemies.Count > 3 ? " 等" + enemies.Count + "人" : "");
            var actionText = pattern.ActionDescription.Replace("敌方", enemyText, StringComparison.Ordinal)
                .Replace("主角", primary, StringComparison.Ordinal);
            var beatIndex = beats.Count + 1;
            beats.Add(new CombatBeat
            {
                Index = beatIndex,
                GrammarId = pattern.Id,
                AttackerId = primary,
                TargetIds = enemies.ToList(),
                ActionType = pattern.ActionType,
                ActionDescription = actionText,
                DefenseResponse = pattern.DefenseResponse.Replace("敌方", enemyText, StringComparison.Ordinal)
                    .Replace("主角", primary, StringComparison.Ordinal),
                Result = pattern.Result.Replace("敌方", enemyText, StringComparison.Ordinal),
                SpatialRelation = pattern.SpatialRelation,
                Intensity = pattern.Intensity,
                IsClimaxBeat = pattern.IsClimax,
                Description = $"Beat{beatIndex:00} {pattern.Name}：{actionText}"
            });
        }

        return new ExpansionResult(beats, invalid);
    }

    public List<CombatAction> GetAvailableActions(CombatRequest request)
    {
        request ??= new CombatRequest();
        var tier = NormalizeTier(request.Tier);
        var preset = ResolvePreset(request, tier);
        var style = ResolveStyle(request.Style);
        var state = _rules.CreateInitialState(request);
        return FilterActionsForRequest(_rules.GetAvailableActions(state, tier, preset, style), request);
    }

    private void FillBeats(
        CombatSequence sequence,
        CombatState state,
        CombatRequest request,
        string tier,
        CombatPreset? preset,
        CombatStyle? style,
        CombatAction? previous)
    {
        for (var i = sequence.Beats.Count + 1; i <= request.BeatCount; i++)
        {
            var available = FilterActionsForRequest(
                _rules.GetAvailableActions(state, tier, preset, style), request);
            if (available.Count == 0) break;

            if (i == request.BeatCount)
            {
                var finishers = available.Where(a => a.Category == "Finisher").ToList();
                if (finishers.Count > 0)
                    available = finishers;
            }

            var action = _weights.SelectAction(available, state, style, previous, preset, request, request.BeatCount);
            state = ApplyBeat(sequence, state, action);
            previous = action;

            if (action.Category == "Finisher" &&
                state.Enemy.State is "Knockdown" or "Airborne")
                break;
        }
    }

    private CombatState ApplyBeat(CombatSequence sequence, CombatState state, CombatAction action)
    {
        var beforeDistance = state.Distance;
        var selfBefore = state.Self.State;
        var enemyBefore = state.Enemy.State;

        var next = _rules.ApplyAction(state, action);
        sequence.Beats.Add(new CombatBeat
        {
            Index = sequence.Beats.Count + 1,
            Action = action,
            BeforeDistance = beforeDistance,
            AfterDistance = next.Distance,
            SelfBefore = selfBefore,
            SelfAfter = next.Self.State,
            EnemyBefore = enemyBefore,
            EnemyAfter = next.Enemy.State,
            Description = $"{action.Name} {beforeDistance}→{next.Distance}"
        });
        return next;
    }

    private static CombatSequence CreateSequence(CombatRequest request, string tier, CombatStyle style, CombatState initialState) =>
        new()
        {
            Tier = tier,
            Intent = request.Intent,
            Style = style.Id,
            InitialState = CloneState(initialState)
        };

    private static List<CombatAction> FilterActionsForRequest(IEnumerable<CombatAction> actions, CombatRequest request)
    {
        return actions
            .Where(a => a.Category != "Skill" || request.AllowedSkillActionIds.Contains(a.Id))
            .Where(a => request.EndMode != "Standoff" || a.Category is "Move" or "Dodge" or "Defense")
            .Where(a => request.EndMode != "Defensive" ||
                        (a.Category != "Finisher" && a.EnemyResultState is not ("Knockdown" or "Airborne" or "Grabbed" or "Disabled")))
            .ToList();
    }

    private static CombatStyle ResolveStyle(string? styleId) =>
        CombatActionCatalog.GetStyle(styleId ?? "") ??
        CombatActionCatalog.GetStyle("BODY_CULTIVATOR")!;

    private static CombatPreset? ResolvePreset(CombatRequest request, string tier)
    {
        if (!string.IsNullOrWhiteSpace(request.CombatType) &&
            request.CombatType.Contains("Group", StringComparison.OrdinalIgnoreCase))
            return CombatActionCatalog.GetPreset("T3_GROUP_MELEE");

        return tier switch
        {
            "T1" => CombatActionCatalog.GetPreset("T1_QUICK_COMBO"),
            "T2" => CombatActionCatalog.GetPreset("T2_BODY_PRESSURE"),
            "T3" => CombatActionCatalog.GetPreset("T3_GROUP_MELEE"),
            "T4" => CombatActionCatalog.GetPreset("T4_SKILL_EXPLOSION"),
            "T5" => CombatActionCatalog.GetPreset("T5_FINALE_CLASH"),
            _ => null
        };
    }

    private static string NormalizeTier(string? tier)
    {
        var value = tier?.Trim().ToUpperInvariant() ?? "T2";
        return value.StartsWith("T") && value.Length == 2 ? value : "T2";
    }

    private static CombatState CloneState(CombatState source) =>
        new()
        {
            Distance = source.Distance,
            Self = new FighterState
            {
                State = source.Self.State,
                Weapon = source.Self.Weapon,
                Style = source.Self.Style,
                Energy = source.Self.Energy,
                Injury = source.Self.Injury
            },
            Enemy = new FighterState
            {
                State = source.Enemy.State,
                Weapon = source.Enemy.Weapon,
                Style = source.Enemy.Style,
                Energy = source.Enemy.Energy,
                Injury = source.Enemy.Injury
            },
            Advantage = source.Advantage,
            BeatIndex = source.BeatIndex
        };
}
