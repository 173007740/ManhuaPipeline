using System.Text.Json;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services.Combat;

namespace ManhuaPipeline.Services.Director;

/// <summary>
/// 校验并归一化 LLM 返回的 DirectorPlan JSON。解析失败时返回 null，
/// 由 DirectorService 回退到现有分镜流程，不阻塞 Stage 5。
/// </summary>
public static class DirectorValidator
{
    public static DirectorPlan? ValidateAndMap(int projectId, StageUnit unit, string raw, List<string>? characterNames = null, List<FightArcTemplate>? fightArcs = null)
    {
        var json = ExtractJsonObject(raw);
        if (json == null) return null;

        using var doc = ParseJson(json);
        if (doc == null) return null;
        var root = doc.RootElement;

        var dramaticPurpose = ReadText(root, "dramaticPurpose");
        var primarySubject = ReadText(root, "primarySubject");
        var conflictType = NormalizeConflictType(ReadText(root, "conflictType"));
        var knownCharacters = NormalizeNames(characterNames);
        // LLM 常把“高潮/对决/打斗”单元误判成 NonCombat，导致 V1.5 ActionPlan 整条链路丢失。
        // 单元类型本身已是战斗标记，这里强制纠正冲突关系并补结构化回退。
        if (unit.IsCombat && conflictType == "NonCombat")
            conflictType = IsSoloOrMovementCombat(unit) ? "1V1" : "1VN";
        var corePayoff = ReadText(root, "corePayoff");
        var emotionCurve = ReadText(root, "emotionCurve", " → ");
        var rhythmStrategy = ReadText(root, "rhythmStrategy");
        var cameraStrategy = ReadText(root, "cameraStrategy");
        var vfxStrategy = ReadText(root, "vfxStrategy");

        if (string.IsNullOrWhiteSpace(dramaticPurpose) ||
            string.IsNullOrWhiteSpace(primarySubject) ||
            string.IsNullOrWhiteSpace(conflictType) ||
            string.IsNullOrWhiteSpace(corePayoff) ||
            string.IsNullOrWhiteSpace(emotionCurve) ||
            string.IsNullOrWhiteSpace(rhythmStrategy) ||
            string.IsNullOrWhiteSpace(cameraStrategy) ||
            string.IsNullOrWhiteSpace(vfxStrategy))
        {
            return null;
        }

        var intensity = ReadInt(root, "intensityLevel", 3);
        intensity = Math.Clamp(intensity, 1, 10);

        var actionStrategy = ReadText(root, "actionStrategy");
        if (string.IsNullOrWhiteSpace(actionStrategy))
            actionStrategy = unit.IsCombat
                ? "按冲突关系组织攻防回合，禁止站桩、禁止只有特效没有攻防"
                : "按戏剧目的组织人物调度与站位，服务情绪和对话节奏";

        var performanceStrategy = ReadText(root, "performanceStrategy");
        if (string.IsNullOrWhiteSpace(performanceStrategy))
            performanceStrategy = "按情绪曲线设计表情、眼神、身体语言与说话状态，避免全程面无表情或过度表演";

        var actionPlan = ReadActionPlan(root, unit, knownCharacters);
        actionPlan ??= unit.IsCombat ? BuildFallbackActionPlan(unit, knownCharacters, conflictType) : null;
        var combatGrammarIds = actionPlan != null && (actionPlan.CombatGrammarIds?.Count ?? 0) > 0
            ? string.Join(",", actionPlan.CombatGrammarIds ?? new List<string>())
            : NormalizeIds(ReadText(root, "combatGrammarIds", ","));
        var roundCount = actionPlan?.RoundCount > 0
            ? Math.Clamp(actionPlan.RoundCount, 1, 20)
            : InferRoundCount(unit, combatGrammarIds);

        var fightSequence = ReadFightSequence(root);
        var fightArcType = FightArcCatalog.NormalizeArcTypeId(
            string.IsNullOrWhiteSpace(fightSequence?.ArcType) ? ReadText(root, "fightArcType") : fightSequence.ArcType);
        fightArcType = FightArcCatalog.ResolveCombatArcType(unit, fightArcType);
        if (string.IsNullOrWhiteSpace(fightArcType))
        {
            fightSequence = null;
        }
        else
        {
            if (fightSequence != null) fightSequence.ArcType = fightArcType;
            if (unit.IsCombat && fightSequence != null && !FightArcCatalog.HasValidPercentSum(fightSequence))
                fightSequence = FightArcCatalog.NormalizePercentSum(fightSequence);
            if (unit.IsCombat && (fightSequence == null || !FightArcCatalog.HasValidPercentSum(fightSequence)))
            {
                fightSequence = FightArcCatalog.BuildFallbackSequence(unit, fightArcType, fightArcs);
                fightArcType = fightSequence?.ArcType ?? "";
            }
            else if (!unit.IsCombat)
            {
                fightArcType = "";
                fightSequence = null;
            }
            fightSequence = FightArcCatalog.FitToDuration(fightSequence, unit.Duration);
        }
        return new DirectorPlan
        {
            ProjectId = projectId,
            EpisodeNumber = unit.EpisodeNumber > 0 ? unit.EpisodeNumber : ExtractEpisodeFromUnitNumber(unit.UnitNumber),
            UnitNumber = unit.UnitNumber,
            UnitType = unit.Type,
            DramaticPurpose = dramaticPurpose.Trim(),
            PrimarySubject = primarySubject.Trim(),
            SecondarySubject = ReadText(root, "secondarySubject")?.Trim() ?? "",
            ConflictType = conflictType,
            CorePayoff = corePayoff.Trim(),
            EmotionCurve = emotionCurve.Trim(),
            RhythmStrategy = rhythmStrategy.Trim(),
            ActionStrategy = actionStrategy.Trim(),
            ActionPlan = actionPlan == null ? "" : JsonSerializer.Serialize(actionPlan, JsonOptions),
            PerformanceStrategy = performanceStrategy.Trim(),
            CameraStrategy = cameraStrategy.Trim(),
            VfxStrategy = vfxStrategy.Trim(),
            IntensityLevel = intensity,
            CombatGrammarIds = combatGrammarIds,
            CombatRoundCount = roundCount,
            VfxPeakPhase = NormalizeVfxPeakPhase(ReadText(root, "vfxPeakPhase")),
            FightArcType = fightArcType,
            FightSequenceJson = fightSequence == null ? "" : JsonSerializer.Serialize(fightSequence, JsonOptions),
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
    }

    public static bool IsValid(DirectorPlan? plan) =>
        plan != null &&
        !string.IsNullOrWhiteSpace(plan.DramaticPurpose) &&
        !string.IsNullOrWhiteSpace(plan.PrimarySubject) &&
        !string.IsNullOrWhiteSpace(plan.ConflictType) &&
        !string.IsNullOrWhiteSpace(plan.CorePayoff) &&
        !string.IsNullOrWhiteSpace(plan.EmotionCurve) &&
        !string.IsNullOrWhiteSpace(plan.RhythmStrategy) &&
        !string.IsNullOrWhiteSpace(plan.CameraStrategy) &&
        !string.IsNullOrWhiteSpace(plan.VfxStrategy) &&
        plan.IntensityLevel is >= 1 and <= 10;

    private static DirectorActionPlan? ReadActionPlan(JsonElement root, StageUnit unit, List<string> knownCharacters)
    {
        if (!root.TryGetProperty("actionPlan", out var el)) return null;
        if (el.ValueKind != JsonValueKind.Object) return null;

        var conflict = NormalizeConflictType(ReadText(el, "conflictType"));
        var primary = ReadText(el, "primaryFighterId");
        if (string.IsNullOrWhiteSpace(primary))
            primary = string.IsNullOrWhiteSpace(ReadText(root, "primarySubject")) ? unit.CoreAction : ReadText(root, "primarySubject");
        var ids = ReadIds(el, "combatGrammarIds");
        var fromTopLevelFallback = false;
        if (ids.Count == 0)
        {
            ids = ParseIds(ReadText(root, "combatGrammarIds", ","));
            fromTopLevelFallback = true;
        }

        var known = ids.Where(CombatGrammarCatalog.IsKnown).ToList();
        var enemyIds = ReadStringList(el, "enemyIds");
        enemyIds = FilterKnownEnemies(enemyIds, knownCharacters);
        var plan = new DirectorActionPlan
        {
            ConflictType = conflict,
            PrimaryFighterId = primary?.Trim() ?? "",
            EnemyIds = enemyIds,
            CombatStyle = ReadText(el, "combatStyle")?.Trim() ?? "",
            RoundCount = 0,
            DominanceCurve = ReadText(el, "dominanceCurve")?.Trim() ?? "",
            CombatGrammarIds = known.Count > 0 ? known : (fromTopLevelFallback ? ids : new List<string>()),
            EndingState = ReadText(el, "endingState")?.Trim() ?? ""
        };
        var rawRoundCount = ReadInt(el, "roundCount", 0);
        if (rawRoundCount > 0) plan.RoundCount = Math.Clamp(rawRoundCount, 1, 20);
        if (plan.RoundCount <= 0) plan.RoundCount = InferRoundCount(unit, string.Join(",", plan.CombatGrammarIds));
        return plan;
    }

    private static List<string> ReadIds(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var el)) return [];
        return el.ValueKind == JsonValueKind.Array
            ? el.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String || x.ValueKind == JsonValueKind.Number)
                .Select(x => x.ValueKind == JsonValueKind.Number ? x.GetRawText() : x.GetString()?.Trim() ?? "")
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : ParseIds(ReadText(root, propertyName, ","));
    }

    /// <summary>位移/单人爆发单元不适用攻防回合校验，合规层与导演回退共用同一口径。</summary>
    public static bool IsSoloOrMovementCombat(StageUnit unit)
    {
        var text = string.Join(" ", unit.Type, unit.CoreAction, unit.StartState, unit.EndState, unit.RawText);
        if (text.Contains("踏天步", StringComparison.Ordinal) ||
            text.Contains("轻功", StringComparison.Ordinal) ||
            text.Contains("一步踏出", StringComparison.Ordinal) ||
            text.Contains("飞掠", StringComparison.Ordinal) ||
            text.Contains("越过", StringComparison.Ordinal) ||
            text.Contains("位移", StringComparison.Ordinal) ||
            text.Contains("赶路", StringComparison.Ordinal) ||
            text.Contains("追击", StringComparison.Ordinal) ||
            text.Contains("逃跑", StringComparison.Ordinal) ||
            text.Contains("逃离", StringComparison.Ordinal) ||
            text.Contains("撤退", StringComparison.Ordinal))
            return true;
        // 单人爆发经常不写“单人”，但会同时出现气血/记忆与爆发/觉醒特征，例如“三缕赤金气血依次爆发”。
        return (text.Contains("单人", StringComparison.Ordinal) &&
            (text.Contains("爆发", StringComparison.Ordinal) ||
             text.Contains("气血", StringComparison.Ordinal) ||
             text.Contains("觉醒", StringComparison.Ordinal) ||
             text.Contains("记忆", StringComparison.Ordinal))) ||
            (text.Contains("气血", StringComparison.Ordinal) && text.Contains("爆发", StringComparison.Ordinal)) ||
            (text.Contains("记忆", StringComparison.Ordinal) && text.Contains("觉醒", StringComparison.Ordinal));
    }

    private static DirectorActionPlan BuildFallbackActionPlan(StageUnit unit, List<string> knownCharacters, string conflictType)
    {
        var primary = PickPrimaryFighter(unit, knownCharacters);
        var enemies = PickEnemies(unit, knownCharacters, primary);
        var soloOrMovement = IsSoloOrMovementCombat(unit);
        var roundCount = unit.Duration switch
        {
            5 => 1,
            15 => 5,
            _ => 3
        };
        var grammarIds = soloOrMovement || enemies.Count == 0
            ? new List<string>()
            : new List<string> { "T3_SURROUND_ATTACK", "T2_CLOSE_COUNTER", "T4_AOE_BREAK" };
        return new DirectorActionPlan
        {
            ConflictType = conflictType == "1V1" ? conflictType : "1VN",
            PrimaryFighterId = primary,
            EnemyIds = enemies,
            CombatStyle = soloOrMovement ? "力量外放/位移展示" : "群敌合围后反击破局",
            RoundCount = roundCount,
            DominanceCurve = soloOrMovement ? "力量逐级释放" : "敌方压制→主角反击→范围破局",
            CombatGrammarIds = grammarIds,
            EndingState = unit.EndState
        };
    }

    private static string PickPrimaryFighter(StageUnit unit, List<string> knownCharacters)
    {
        var keys = string.Join(" ", unit.KeyElements, unit.RawText);
        var combatState = knownCharacters.FirstOrDefault(c => c.Contains("战斗态", StringComparison.Ordinal) && keys.Contains(c, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(combatState)) return combatState;
        return knownCharacters.FirstOrDefault(c => keys.Contains(c, StringComparison.Ordinal)) ?? "主角";
    }

    private static List<string> PickEnemies(StageUnit unit, List<string> knownCharacters, string primary)
    {
        var keys = string.Join(" ", unit.KeyElements, unit.RawText);
        if (IsSoloOrMovementCombat(unit)) return [];
        return knownCharacters
            .Where(c => !string.Equals(c, primary, StringComparison.OrdinalIgnoreCase))
            .Where(c => !primary.Contains(c, StringComparison.Ordinal))
            .Where(c => keys.Contains(c, StringComparison.Ordinal))
            .Take(3)
            .ToList();
    }

    private static List<string> FilterKnownEnemies(List<string> enemyIds, List<string> knownCharacters)
    {
        if (enemyIds.Count == 0 || knownCharacters.Count == 0) return enemyIds;
        return enemyIds
            .Where(id => knownCharacters.Any(c => string.Equals(c, id?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> NormalizeNames(List<string>? names)
    {
        if (names == null) return [];
        return names.Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ReadStringList(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var el)) return [];
        return el.ValueKind == JsonValueKind.Array
            ? el.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()?.Trim() ?? "")
                .Where(x => x.Length > 0)
                .ToList()
            : [];
    }

    private static List<string> ParseIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "[]") return [];
        return raw.Split(new[] { ',', '，', ' ', ';', '；', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int InferRoundCount(StageUnit unit, string grammarIds)
    {
        var ids = ParseIds(grammarIds);
        if (ids.Count > 0) return ids.Count;
        if (!unit.IsCombat) return 1;
        return unit.Duration switch
        {
            5 => 1,
            15 => 5,
            _ => 3
        };
    }

    private static string NormalizeVfxPeakPhase(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = raw.Trim().ToUpperInvariant();
        if (text.Contains("EARLY") || text.Contains("前") || text.Contains("开")) return "Early";
        if (text.Contains("MID") || text.Contains("中段")) return "Mid";
        if (text.Contains("LATE") || text.Contains("后") || text.Contains("末") || text.Contains("FIN") || text.Contains("收")) return "Late";
        if (text.Contains("NONE") || text.Contains("无") || text.Contains("不") || text.Contains("低")) return "None";
        return "";
    }

    private static string? ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return raw.Substring(start, end - start + 1);
    }

    private static FightSequencePlan? ReadFightSequence(JsonElement root)
    {
        if (root.TryGetProperty("fightSequence", out var seqEl) && seqEl.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var plan = JsonSerializer.Deserialize<FightSequencePlan>(seqEl.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (plan != null)
                {
                    plan.ArcType = FightArcCatalog.NormalizeArcTypeId(plan.ArcType);
                    return string.IsNullOrWhiteSpace(plan.ArcType) ? null : plan;
                }
            }
            catch
            {
                // 交给回退序列
            }
        }
        if (root.TryGetProperty("fightSequenceJson", out var jsonEl) && jsonEl.ValueKind == JsonValueKind.String)
            return FightArcCatalog.ParseSequence(jsonEl.GetString());
        return null;
    }

    private static JsonDocument? ParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadText(JsonElement root, string propertyName, string arraySeparator = "；")
    {
        if (!root.TryGetProperty(propertyName, out var el)) return null;
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Array:
                var values = el.EnumerateArray()
                    .Where(x => (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString())) || x.ValueKind == JsonValueKind.Number)
                    .Select(x => x.ValueKind == JsonValueKind.Number ? x.GetRawText() : x.GetString()!.Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
                return values.Count == 0 ? null : string.Join(arraySeparator, values);
            case JsonValueKind.Number:
                return el.GetRawText();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return el.GetBoolean().ToString();
            default:
                return null;
        }
    }

    private static int ReadInt(JsonElement root, string propertyName, int fallback)
    {
        if (!root.TryGetProperty(propertyName, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var parsed)) return parsed;
        return fallback;
    }

    private static string NormalizeConflictType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "NonCombat";
        var text = raw.ToUpperInvariant();
        if (text.Contains("1V1") || text.Contains("1对1") || text.Contains("一对一") || text.Contains("1VS1"))
            return "1V1";
        if (text.Contains("1VN") || text.Contains("1对N") || text.Contains("一打多") || text.Contains("以少敌多"))
            return "1VN";
        if (text.Contains("NVN") || text.Contains("N对N") || text.Contains("多人混战") || text.Contains("群战") || text.Contains("混战"))
            return "NVN";
        return "NonCombat";
    }

    private static string NormalizeIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "[]") return "";
        return string.Join(",",
            raw.Split(new[] { ',', '，', ' ', ';', '；', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static int ExtractEpisodeFromUnitNumber(string unitNumber)
    {
        if (string.IsNullOrWhiteSpace(unitNumber) || !unitNumber.Contains('.')) return 0;
        return int.TryParse(unitNumber.Split('.')[0], out var ep) ? ep : 0;
    }
}
