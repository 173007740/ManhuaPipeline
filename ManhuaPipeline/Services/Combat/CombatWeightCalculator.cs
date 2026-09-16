using ManhuaPipeline.Models.Combat;

namespace ManhuaPipeline.Services.Combat;

public class CombatWeightCalculator
{
    public CombatAction SelectAction(
        IReadOnlyList<CombatAction> actions,
        CombatState state,
        CombatStyle? style,
        CombatAction? previous,
        CombatPreset? preset,
        CombatRequest request,
        int targetBeatCount)
    {
        if (actions.Count == 0)
            throw new InvalidOperationException("当前状态下没有可用动作");

        var weighted = actions
            .Select(a => new
            {
                Action = a,
                Weight = CalculateWeight(a, state, style, previous, preset, request, targetBeatCount)
            })
            .Where(x => x.Weight > 0)
            .ToList();

        if (weighted.Count == 0)
            return actions[Random.Shared.Next(actions.Count)];

        var total = weighted.Sum(x => x.Weight);
        var roll = Random.Shared.NextDouble() * total;

        foreach (var item in weighted)
        {
            roll -= item.Weight;
            if (roll <= 0)
                return item.Action;
        }

        return weighted[^1].Action;
    }

    private static int CalculateWeight(
        CombatAction action,
        CombatState state,
        CombatStyle? style,
        CombatAction? previous,
        CombatPreset? preset,
        CombatRequest request,
        int targetBeatCount)
    {
        // 终结技留到战斗后段，避免一上来就秒杀。
        if (action.Category == "Finisher" && state.BeatIndex < Math.Max(2, targetBeatCount - 2))
            return 0;

        var weight = action.Weight;

        // 收尾阶段更倾向终结。
        if (action.Category == "Finisher" && state.BeatIndex >= Math.Max(2, targetBeatCount - 2))
            weight += 50;

        if (style != null)
        {
            weight += style.CategoryWeights.GetValueOrDefault(action.Category);
            if (style.ActionWeights.TryGetValue(action.Id, out var actionWeight))
                weight += actionWeight;
        }

        if (preset != null)
            weight += preset.CategoryWeights.GetValueOrDefault(action.Category);

        if (previous != null && previous.NextActions.Contains(action.Id))
            weight += 30;

        // 开局拉近距离：中远距离更倾向突进，避免原地站桩。
        if (state.Distance is "Mid" or "Long" &&
            action.Category == "Move" &&
            action.ResultDistance is "Close" or "Clinch")
            weight += 35;

        // 避免同一动作连续复读。
        if (previous != null && previous.Id == action.Id)
            return 0;

        // 开局立刻后撤会破坏接战节奏。
        if (action.Id == "MOVE_BACK_001" && state.BeatIndex < 2)
            weight -= 40;

        // 局势奖励：优势局面更倾向终结，被攻时更倾向反击/控制。
        if (state.Enemy.State is "Knockdown" or "Airborne" or "Grabbed" or "Disabled")
        {
            if (action.Category == "Finisher") weight += 40;
            if (action.Category == "Throw") weight += 15;
        }
        if (state.Enemy.State is "Knockdown" or "Airborne" && action.Id == "ATK_DOWNED_PRESS_001")
            weight += 25;

        if (state.Enemy.State == "Attack")
        {
            if (action.Category is "Counter" or "Grab" or "Dodge") weight += 20;
            if (action.Category == "Defense") weight += 10;
        }

        if (state.Self.State == "Grabbed" && action.Category is "Counter" or "Grab")
            weight += 25;

        if (request.Tier is "T4" or "T5" && action.DestructionLevel >= 2)
            weight += 10;

        // 剑修 T4/T5 保持武器打法，少用徒手擒摔，终结只用剑招。
        if (style?.Id == "SWORD_CULTIVATOR" && request.Tier is "T4" or "T5")
        {
            if (action.WeaponType == "剑") weight += 25;
            if (action.Category is "Grab" or "Throw") weight -= 20;
            if (action.Category == "Finisher" && action.Id is not ("FIN_SWORD_SLASH_001" or "FIN_SWORD_FALL_001")) return 0;
        }

        // 法修贴身时优先护盾/拉开距离，避免徒手肉搏。
        if (style?.Id == "MAGE" && state.Distance is "Close" or "Clinch")
        {
            if (action.Id == "SKILL_MAGE_SHIELD_001") weight += 40;
            if (action.Category is "Grab" or "Throw") weight -= 50;
            if (action.Category == "Attack" && action.WeaponType != "剑") weight -= 35;
        }

        // 法修远距站桩，尽量少主动拉近距离。
        if (style?.Id == "MAGE" && state.Distance is "Mid" or "Long")
        {
            if (action.Category == "Move" && action.ResultDistance is "Close" or "Clinch") weight -= 45;
            if (action.Category == "Attack" && action.WeaponType != "剑") weight -= 25;
        }

        return weight;
    }
}
