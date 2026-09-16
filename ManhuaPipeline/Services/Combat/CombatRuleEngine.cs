using ManhuaPipeline.Models.Combat;

namespace ManhuaPipeline.Services.Combat;

public class CombatRuleEngine
{
    public CombatState CreateInitialState(CombatRequest request)
    {
        var selfState = string.IsNullOrWhiteSpace(request.SelfState) ? "Neutral" : request.SelfState;
        var enemyState = string.IsNullOrWhiteSpace(request.EnemyState) ? "Neutral" : request.EnemyState;
        var distance = NormalizeDistance(request.InitialDistance);

        return new CombatState
        {
            Distance = distance,
            Self = new FighterState
            {
                State = selfState,
                Style = request.Style,
                Weapon = "拳脚"
            },
            Enemy = new FighterState
            {
                State = enemyState,
                Style = "Enemy"
            },
            Advantage = "Even",
            BeatIndex = 0
        };
    }

    public List<CombatAction> GetAvailableActions(
        CombatState state,
        string tier,
        CombatPreset? preset = null,
        CombatStyle? style = null)
    {
        if (state == null) return [];

        return CombatActionCatalog.Actions
            .Where(a => !a.IsReaction)
            .Where(a => a.Tiers.Contains(tier))
            .Where(a => a.AllowedDistances.Count == 0 || a.AllowedDistances.Contains(state.Distance))
            .Where(a => a.RequireSelfStates.Count == 0 || a.RequireSelfStates.Contains(state.Self.State))
            .Where(a => a.RequireEnemyStates.Count == 0 || a.RequireEnemyStates.Contains(state.Enemy.State))
            .Where(a => preset == null || preset.ForbiddenCategories.Count == 0 ||
                        !preset.ForbiddenCategories.Contains(a.Category))
            .Where(a => style == null || style.ForbiddenSkills.Count == 0 ||
                        !style.ForbiddenSkills.Contains(a.Id))
            .ToList();
    }

    public CombatState ApplyAction(CombatState state, CombatAction action)
    {
        var next = new CombatState
        {
            Distance = string.IsNullOrWhiteSpace(action.ResultDistance)
                ? state.Distance
                : NormalizeDistance(action.ResultDistance),
            Self = Clone(state.Self),
            Enemy = Clone(state.Enemy),
            Advantage = state.Advantage,
            BeatIndex = state.BeatIndex + 1
        };

        next.Self.State = action.SelfResultState;
        next.Enemy.State = action.EnemyResultState;

        var cost = action.Category is "Skill" or "Finisher" ? 4 : 1;
        next.Self.Energy = Math.Max(0, next.Self.Energy - cost);

        if (action.DestructionLevel > 0)
            next.Enemy.Injury = Math.Min(10, next.Enemy.Injury + action.DestructionLevel);

        next.Advantage = EvaluateAdvantage(next.Self.State, next.Enemy.State);
        return next;
    }

    private static string EvaluateAdvantage(string selfState, string enemyState)
    {
        if (selfState is "Knockdown" or "Airborne" or "Grabbed" or "Disabled")
            return "Disadvantaged";
        if (enemyState is "Knockdown" or "Airborne" or "Grabbed" or "Disabled" or "Exposed")
            return "Dominant";
        if (selfState is "Stagger" or "OffBalance")
            return "Disadvantaged";
        if (enemyState is "Stagger" or "OffBalance")
            return "Dominant";
        return "Even";
    }

    private static string NormalizeDistance(string distance)
    {
        var value = distance?.Trim() ?? "";
        return value.ToLowerInvariant() switch
        {
            "long" or "远" or "远距离" => "Long",
            "close" or "近" or "近距离" => "Close",
            "clinch" or "贴身" or "纠缠" => "Clinch",
            _ => "Mid"
        };
    }

    private static FighterState Clone(FighterState source) =>
        new()
        {
            State = source.State,
            Weapon = source.Weapon,
            Style = source.Style,
            Energy = source.Energy,
            Injury = source.Injury
        };
}
