using System.Text.Json;
using System.Text.RegularExpressions;
using ManhuaPipeline.Models;
using ManhuaPipeline.Models.Combat;
using ManhuaPipeline.Services.Combat;

namespace ManhuaPipeline.Services.Director;

/// <summary>
/// Director V2 RuleValidator：程序规则校验分镜是否执行导演要求。
/// 程序能查的（主体、节拍覆盖/顺序、回合数、结局、语法 ID、技能归属、镜头碎片化）
/// 一律不交给 LLM；DramaticPurpose / CorePayoff 等语义项由 SemanticValidator 负责。
/// </summary>
public static class DirectorRuleValidator
{
    // 100 分制权重
    public const int BeatCoverageWeight = 30;
    public const int PrimarySubjectWeight = 15;
    public const int CombatLogicWeight = 20;
    public const int DramaticPurposeWeight = 10;
    public const int CorePayoffWeight = 10;
    public const int VfxRhythmWeight = 5;
    public const int DialogueWeight = 5;
    public const int ContinuityWeight = 5;

    private static readonly string[] PrefixTokens =
    {
        "前世", "少年", "少女", "青年", "中年", "老年", "幼年", "小时候", "童年",
        "倒影中", "镜像中", "回忆中", "记忆中的"
    };

    private static readonly string[] NameNoiseTokens =
    {
        "战斗态", "基础卡", "普通态", "状态卡", "常态", "留音", "本体", "虚影",
        "倒影中", "镜像中", "回忆中", "记忆中的"
    };

    private static readonly string[] RhythmMarkers =
    {
        "回合", "攻防", "命中", "对撞", "格挡", "反打", "闪避", "突进",
        "受击", "反击", "交手", "压制", "震退", "轰退", "硬抗"
    };

    private static readonly string[] ActionEventMarkers =
    {
        "突进", "挥剑", "横斩", "追斩", "劈砍", "斩", "刺", "拳", "掌", "腿", "踢", "肘",
        "闪避", "侧闪", "后仰", "格挡", "招架", "扣腕", "切入", "反击", "反打", "命中",
        "轰中", "震退", "倒飞", "压制", "对撞", "爆发", "蓄力", "出手", "连击", "交手",
        "受击", "压进", "夹击", "封路", "合围", "围杀", "催动", "硬抗", "崩碎", "炸开",
        "震开", "砸落", "轰出", "破空", "踏出", "斩落", "四溅", "贯穿", "扑", "砸"
    };

    private static readonly Regex ShotDurationExplicitRegex = new(
        @"镜头时长\s*[:：]\s*(\d+)\s*秒",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShotDurationBareRegex = new(
        @"^\s*[-*]*\s*(?:\*\*)?时长(?:\*\*)?\s*[:：]\s*(\d+)\s*秒",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TimelineFieldRegex = new(
        @"镜头时间轴|时间轴|Timeline",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TimelineRangeRegex = new(
        @"(\d+(?:\.\d+)?)\s*[-–—~至]\s*(\d+(?:\.\d+)?)\s*(?:秒|s)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static DirectorValidationResult Validate(
        DirectorPlan? plan,
        StageUnit unit,
        string result,
        IReadOnlyList<SkillLibraryItem>? skills = null,
        EpisodeDirectorPlan? episodePlan = null,
        EpisodeDirectorState? episodeState = null,
        EpisodeUnitStateSnapshot? previousSnapshot = null,
        IReadOnlyList<CharacterAsset>? characters = null)
    {
        if (plan == null || string.IsNullOrWhiteSpace(result)) return DirectorValidationResult.Pass();

        var shots = SplitShots(result);
        var violations = new List<DirectorViolation>();
        var hardFail = false;

        var beatScore = BeatCoverageWeight;
        var primaryScore = PrimarySubjectWeight;
        var logicScore = CombatLogicWeight;
        var dramaticScore = DramaticPurposeWeight;
        var coreScore = CorePayoffWeight;
        var vfxScore = VfxRhythmWeight;
        var dialogueScore = DialogueWeight;
        var continuityScore = ContinuityWeight;

        // ---- 主体一致性（硬失败）----
        if (!ContainsSubject(result, plan.PrimarySubject, characters))
        {
            violations.Add(MakeViolation(
                "PRIMARY_SUBJECT_MISSING", "Error",
                $"缺少核心主体「{plan.PrimarySubject}」",
                plan.PrimarySubject, "(未出现)",
                $"在镜头描述、出镜角色或画面中补回核心主体「{plan.PrimarySubject}」，禁止用其他角色顶替。"));
            primaryScore = 0;
            hardFail = true;
        }

        // 文戏的次要主体常是道具/背景元素，分镜里用简称即可，不按角色主体强校验。
        var isCombat = unit.IsCombat;
        if (isCombat &&
            !string.IsNullOrWhiteSpace(plan.SecondarySubject) &&
            !ContainsSubject(result, plan.SecondarySubject, characters))
        {
            violations.Add(MakeViolation(
                "SECONDARY_SUBJECT_MISSING", "Warning",
                $"缺少次要主体「{plan.SecondarySubject}」",
                plan.SecondarySubject, "(未出现)",
                $"在对应镜头中补回次要主体「{plan.SecondarySubject}」，不要改变核心主体的戏份。"));
            logicScore = Deduct(logicScore, 5);
        }

        var soloOrMovement = DirectorValidator.IsSoloOrMovementCombat(unit);
        var singleStrike = IsSingleStrikeTemplate(result);

        // ---- 镜头数与节奏（单元总时长决定可承载镜头数；时长已知时不机械要求多镜）----
        var minShots = unit.Duration == 5 || unit.Duration == 11 || unit.Duration == 15 ? 1 : unit.IsCombat ? 3 : 2;
        if (shots.Count < minShots)
        {
            violations.Add(MakeViolation(
                "SHOT_COUNT_LOW", "Error",
                $"镜头数不足（{shots.Count}/{minShots}）",
                minShots.ToString(), shots.Count.ToString(),
                $"增加镜头至至少 {minShots} 个，保持现有节拍顺序，禁止为凑数添加无关内容。"));
            logicScore = Deduct(logicScore, 6);
            hardFail = true;
        }

        // ---- 单元时长守恒：镜头时长总和必须等于（非打斗）/ 不得超过（打斗）单元时长 ----
        if ((unit.Duration == 5 || unit.Duration == 11 || unit.Duration == 15) && shots.Count > 0)
        {
            var shotTotal = shots.Sum(s => ReadShotDurationSeconds(s) ?? 0);
            var mismatch = isCombat ? shotTotal > unit.Duration : shotTotal != unit.Duration;
            if (mismatch)
            {
                violations.Add(MakeViolation(
                    "DURATION_SUM", "Error",
                    $"镜头时长总和 {shotTotal} 秒与单元时长 {unit.Duration} 秒不一致",
                    unit.Duration.ToString(), shotTotal.ToString(),
                    isCombat
                        ? $"把镜头时长总和压到 ≤ {unit.Duration} 秒（尽量等于）：合并连续镜头或把多拍压进同一镜头，禁止靠超出单元总时长堆镜头。"
                        : $"把镜头时长总和改到恰好等于 {unit.Duration} 秒（D={unit.Duration}：只允许 1 条 {unit.Duration} 秒镜头，D=15 也可拆 3 条 5 秒镜头），禁止用超出/小于 D 的组合堆镜头。"));
                logicScore = Deduct(logicScore, 8);
                hardFail = true;
            }
        }

        if (isCombat && plan.IntensityLevel >= 7 && !soloOrMovement && !singleStrike && !HasRhythmMarkers(result))
        {
            violations.Add(MakeViolation(
                "COMBAT_RHYTHM_MISSING", "Error",
                "缺少攻防回合/命中反馈表达",
                "至少一轮完整的 攻→防/反→变招→命中/压制",
                "(未检测到攻防反馈)",
                "补足攻防回合与命中反馈：A攻→B防/反→A变招→命中/压制，命中瞬间写清受击反馈。"));
            logicScore = Deduct(logicScore, 6);
        }

        // ---- CombatBeat 覆盖（硬失败）----
        var expansion = new CombatGrammarEngine().ExpandFromActionPlan(ParseActionPlan(plan));
        // CombatBeat 不等于 Shot：允许一个镜头覆盖多个连续 Beat，每条 Beat 至少被一个镜头覆盖。
        var shotBeatIndexes = shots.Select(ReadBeatIndexes).ToList();
        var coveredBeats = shotBeatIndexes.SelectMany(x => x).ToHashSet();
        var beatOrder = shotBeatIndexes.SelectMany(x => x).ToList();
        foreach (var beat in expansion.Beats)
        {
            var indexCovered = coveredBeats.Contains(beat.Index);
            var textCovered = ContainsBeatText(result, beat);
            if (indexCovered || textCovered) continue;

            var beatId = $"Beat{beat.Index:00}";
            violations.Add(MakeViolation(
                "COMBAT_BEAT_MISSING", "Error",
                $"漏拍 {beatId}（{beat.GrammarId} {beat.ActionDescription}）",
                $"{beatId} {beat.ActionDescription}", "(未出现)",
                $"在相邻节拍之间补充 1-2 个镜头完整覆盖「{beat.ActionDescription}」，不得修改其他 Beat 的顺序。",
                beatId));
            beatScore = Deduct(beatScore, 10);
            hardFail = true;
        }

        for (var i = 1; i < beatOrder.Count; i++)
        {
            if (beatOrder[i] >= beatOrder[i - 1]) continue;
            violations.Add(MakeViolation(
                "COMBAT_BEAT_ORDER", "Error",
                $"节拍顺序错误（{beatOrder[i - 1]} 后出现 {beatOrder[i]}）",
                string.Join(" → ", beatOrder.OrderBy(x => x)),
                string.Join(" → ", beatOrder),
                "按 Beat1→Beat2→Beat3 的递增顺序重排镜头，禁止回跳或倒序。"));
            logicScore = Deduct(logicScore, 6);
            break;
        }

        // ---- 镜头碎片化检测：高速战斗禁止单拍单动作成镜 ----
        if (isCombat && plan.IntensityLevel >= 6 && !soloOrMovement && !singleStrike)
        {
            foreach (var shot in shots)
            {
                if (ReadBeatIndexes(shot).Count != 1) continue;
                if (CountActionEvents(shot) >= 2) continue;
                var shotId = ReadShotId(shot);
                violations.Add(MakeViolation(
                    "SHOT_FRAGMENTATION", "Error",
                    $"战斗镜头过度碎片化（{shotId ?? "未编号"}）",
                    "一个镜头内连续多个动作/回合（动作链+运镜+特效+环境反馈）",
                    "1 个 Beat 且只有 1 个简单动作",
                    "将连续攻防动作合并为完整 ActionChain：一个 5/11/15 秒镜头应表现一段连续交锋，包含出手、对手响应、运镜、表情、VFX 与环境反馈，禁止单动作成镜。", "", shotId ?? ""));
                logicScore = Deduct(logicScore, 6);
                break;
            }
        }

        // ---- 时间预算：镜头时间轴必须连续铺满镜头时长 ----
        if (isCombat)
        {
            foreach (var shot in shots)
            {
                var declared = ReadShotDurationSeconds(shot);
                if (declared is not (5 or 11 or 15)) continue;
                var coverage = ReadTimelineCoverageSeconds(shot);
                if (coverage == null) continue;
                if (coverage.Value >= declared - 0.6 && coverage.Value <= declared + 0.6) continue;
                var shotId = ReadShotId(shot);
                violations.Add(MakeViolation(
                    "TIME_BUDGET_MISMATCH", "Warning",
                    $"镜头时间轴未铺满镜头时长（时间轴覆盖 {coverage.Value:0.#} 秒/{declared} 秒）",
                    $"0-{declared} 秒连续覆盖",
                    coverage.Value.ToString("0.#") + " 秒",
                    $"将「镜头时间轴」补成从 0 秒连续铺满 {declared} 秒；慢动作/顿帧计入总时长，禁止只写前半段。",
                    "", shotId ?? ""));
                logicScore = Deduct(logicScore, 6);
            }
        }

        // ---- 回合数 ----
        if (isCombat && plan.CombatRoundCount > 0 && !soloOrMovement && !singleStrike)
        {
            var roundMarkers = CountRhythmRounds(result);
            if (roundMarkers < plan.CombatRoundCount)
            {
                violations.Add(MakeViolation(
                    "ROUND_COUNT_MISMATCH", "Error",
                    $"回合数不足（{roundMarkers}/{plan.CombatRoundCount}）",
                    plan.CombatRoundCount.ToString(), roundMarkers.ToString(),
                    $"补齐攻防回合至至少 {plan.CombatRoundCount} 回合（A攻→B防/反→A变招→命中/压制），不要只写特效。"));
                logicScore = Deduct(logicScore, 8);
            }
        }

        // ---- 语法 ID 合法性（警告）----
        if (expansion.InvalidGrammarIds.Count > 0)
        {
            violations.Add(MakeViolation(
                "GRAMMAR_ID_INVALID", "Warning",
                $"ActionPlan 含非法 CombatGrammarId：{string.Join("、", expansion.InvalidGrammarIds)}",
                "仅使用战斗语法库中的 ID", string.Join("、", expansion.InvalidGrammarIds),
                "导演层修正 ActionPlan，只保留 CombatGrammarCatalog 中存在的 ID。"));
            logicScore = Deduct(logicScore, 4);
        }

        // ---- 结局状态（硬失败）----
        var endingState = ParseActionPlan(plan).EndingState;
        if (isCombat && !string.IsNullOrWhiteSpace(endingState) && !ContainsEndingState(result, endingState))
        {
            violations.Add(MakeViolation(
                "ENDING_STATE_MISMATCH", "Error",
                $"胜负/结局状态未落镜（期望「{endingState}」）",
                endingState, "(未出现)",
                $"在结束画面明确写出结局「{endingState}」，不得提前结束或改写成其他结果。"));
            logicScore = Deduct(logicScore, 10);
            hardFail = true;
        }

        // ---- 能力越权（硬失败）：技能归属角色必须在对应镜头出现 ----
        if (skills != null)
        {
            foreach (var skill in skills)
            {
                if (string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.OwnerCharacter))
                    continue;
                foreach (var shot in shots)
                {
                    if (!shot.Contains(skill.Name.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    if (ContainsSubject(shot, skill.OwnerCharacter, characters)) continue;
                    var shotId = ReadShotId(shot);
                    violations.Add(MakeViolation(
                        "SKILL_OWNER_MISMATCH", "Error",
                        $"技能「{skill.Name}」越权使用（归属「{skill.OwnerCharacter}」，镜头未出现该角色）",
                        skill.OwnerCharacter, "(镜头中未出现)",
                        $"技能「{skill.Name}」只归「{skill.OwnerCharacter}」使用；要么在镜头中补回该角色，要么删除该技能。",
                        "", shotId ?? ""));
                    logicScore = Deduct(logicScore, 10);
                    hardFail = true;
                }
            }
        }

        // ---- 技能名一致性（硬失败）：禁止用别名/缩写/加字改写技能库正式名 ----
        if (skills != null)
        {
            foreach (var skill in skills)
            {
                var formalName = skill.Name?.Trim();
                if (string.IsNullOrWhiteSpace(formalName)) continue;
                var coreName = ExtractSkillCoreName(formalName);
                if (coreName == null) continue; // 无「·」前缀的技能按整名匹配，不适用本条
                if (result.Contains(formalName, StringComparison.OrdinalIgnoreCase)) continue; // 正式名已在用
                if (!result.Contains(coreName, StringComparison.OrdinalIgnoreCase)) continue; // 核心名未出现，无关

                string? shotId = null;
                foreach (var shot in shots)
                {
                    if (shot.Contains(coreName, StringComparison.OrdinalIgnoreCase))
                    {
                        shotId = ReadShotId(shot);
                        break;
                    }
                }
                violations.Add(MakeViolation(
                    "SKILL_NAME_MISMATCH", "Error",
                    $"技能「{formalName}」被改写为「{coreName}…」（须与技能库完全一致）",
                    formalName, coreName + "…",
                    $"将分镜中「{coreName}…」统一改为技能库正式名「{formalName}」，禁止用别名、缩写或加字（如“法相”“形态”“之威”）；若该技能未出场则删除相关描述。",
                    "", shotId ?? ""));
                logicScore = Deduct(logicScore, 10);
                hardFail = true;
            }
        }


        // ---- 关键对白 ----
        if (!string.IsNullOrWhiteSpace(unit.Dialogue) &&
            unit.Dialogue.Trim() != "无" &&
            !ContainsDialogue(result, unit.Dialogue))
        {
            violations.Add(MakeViolation(
                "DIALOGUE_MISSING", "Error",
                "关键对白未逐字保留",
                unit.Dialogue.Trim(), "(未出现)",
                $"在对应镜头「对话/台词」行逐字保留台词「{unit.Dialogue.Trim()}」，禁止改写、删减或新增。"));
            dialogueScore = 0;
        }

        // ---- 连续性（警告，不阻塞）----
        // ---- Director V3：整集强度、保留爽点、重复套路 ----
        var v3Violations = EpisodeDirectorRuleValidator.Check(plan, unit, result, episodePlan, episodeState);
        violations.AddRange(v3Violations);
        var v3ErrorCount = v3Violations.Count(v => string.Equals(v.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        if (v3ErrorCount > 0)
            logicScore = Deduct(logicScore, v3ErrorCount * 5);

        if (shots.Any(s => !ContainsField(s, "起始画面") || !ContainsField(s, "结束画面")))
        {
            violations.Add(MakeViolation(
                "CONTINUITY_INCOMPLETE", "Warning",
                "部分镜头缺少「起始画面」或「结束画面」",
                "每个镜头都含起始画面与结束画面",
                "(部分镜头缺失)",
                "为缺失镜头补充「起始画面」「结束画面」，保证前一镜头结束画面能接后一镜头起始画面；不要新增/删除节拍。"));
            continuityScore = Deduct(continuityScore, 5);
        }

        // ---- Director V4：上一 Unit 落镜状态连续性（防止重新开局/瞬移/伤势自愈）----
        var continuityViolations = EpisodeContinuityValidator.Validate(previousSnapshot, episodePlan, unit, plan, result);
        violations.AddRange(continuityViolations);
        foreach (var violation in continuityViolations)
        {
            var isError = string.Equals(violation.Severity, "Error", StringComparison.OrdinalIgnoreCase);
            continuityScore = Deduct(continuityScore, isError ? 8 : 4);
        }

        var score = Math.Clamp(
            beatScore + primaryScore + logicScore + dramaticScore + coreScore + vfxScore + dialogueScore + continuityScore,
            0, 100);
        var hasError = violations.Any(v => v.Severity == "Error");
        return new DirectorValidationResult
        {
            Passed = !hasError && !hardFail && score >= 80,
            HasHardFailure = hardFail,
            Score = score,
            Verdict = BuildVerdict(hasError || hardFail, score),
            Violations = violations
        };
    }

    /// <summary>把 RuleValidator 结果与 LLM SemanticValidator 结果合并成最终校验结果。</summary>
    public static DirectorValidationResult Merge(DirectorValidationResult rule, DirectorValidationResult semantic)
    {
        if (rule == null) return semantic ?? DirectorValidationResult.Pass();
        if (semantic == null || semantic.Violations.Count == 0) return rule;

        var violations = rule.Violations.Concat(semantic.Violations).ToList();
        var score = Math.Clamp(rule.Score - semantic.Violations.Sum(DeductForSemantic), 0, 100);
        var hasError = rule.HasHardFailure || violations.Any(v => v.Severity == "Error");
        return new DirectorValidationResult
        {
            Passed = !hasError && score >= 80,
            HasHardFailure = rule.HasHardFailure,
            Score = score,
            Verdict = BuildVerdict(hasError, score),
            Violations = violations
        };
    }

    internal static int DeductForSemantic(DirectorViolation violation)
    {
        var code = violation.Code.ToUpperInvariant();
        var isError = string.Equals(violation.Severity, "Error", StringComparison.OrdinalIgnoreCase);
        return code switch
        {
            "DRAMATIC_PURPOSE" or "EMOTION_CURVE" => isError ? DramaticPurposeWeight : DramaticPurposeWeight / 2,
            "CORE_PAYOFF" => isError ? CorePayoffWeight : CorePayoffWeight / 2,
            "CAMERA_STRATEGY" => isError ? PrimarySubjectWeight : Math.Max(1, PrimarySubjectWeight / 2),
            "VFX_STRATEGY" => isError ? VfxRhythmWeight : Math.Max(1, VfxRhythmWeight / 2),
            _ => isError ? CombatLogicWeight / 2 : CombatLogicWeight / 4
        };
    }

    public static string BuildVerdict(bool hasError, int score)
    {
        if (hasError || score < 80) return "FAIL";
        return score >= 90 ? "PASS" : "PASS WITH WARNING";
    }

    private static int Deduct(int current, int amount) => Math.Max(0, current - amount);

    private static DirectorViolation MakeViolation(
        string code,
        string severity,
        string message,
        string expected,
        string actual,
        string repairInstruction,
        string combatBeatId = "",
        string shotId = "") =>
        new()
        {
            Code = code,
            Severity = severity,
            Message = message,
            Expected = expected,
            Actual = actual,
            CombatBeatId = combatBeatId,
            ShotId = shotId,
            RepairInstruction = repairInstruction
        };

    private static DirectorActionPlan ParseActionPlan(DirectorPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.ActionPlan))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<DirectorActionPlan>(plan.ActionPlan);
                if (parsed != null && (parsed.CombatGrammarIds?.Count ?? 0) > 0)
                    return parsed;
            }
            catch
            {
                // 旧数据无 ActionPlan 时按顶层字段回退
            }
        }

        return new DirectorActionPlan
        {
            PrimaryFighterId = plan.PrimarySubject,
            EnemyIds = string.IsNullOrWhiteSpace(plan.SecondarySubject) ? [] : [plan.SecondarySubject.Trim()],
            RoundCount = plan.CombatRoundCount,
            CombatGrammarIds = (plan.CombatGrammarIds ?? "")
                .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => CombatGrammarCatalog.IsKnown(x))
                .ToList()
        };
    }

    private static bool ContainsSubject(string result, string subject, IReadOnlyList<CharacterAsset>? characters = null)
    {
        if (string.IsNullOrWhiteSpace(subject)) return true;

        var candidates = BuildSubjectCandidates(subject, characters);
        foreach (var candidate in candidates)
        {
            if (candidate.Length >= 2 && result.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var normalizedResult = NormalizeNameContext(result);
        return candidates.Any(candidate =>
            candidate.Length >= 2 &&
            normalizedResult.Contains(NormalizeNameContext(candidate), StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> BuildSubjectCandidates(string subject, IReadOnlyList<CharacterAsset>? characters = null)
    {
        var candidates = new List<string>();
        void AddCandidate(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (name.Length >= 2 && !candidates.Contains(name)) candidates.Add(name);
        }

        AddCandidate(subject);
        // 组合主体（如“七宗宗主与[七曜诛圣阵]”）按连接词拆分，任一段出现即可。
        foreach (var part in subject.Split(new[] { "与", "和", "、", "及", "/" }, StringSplitOptions.RemoveEmptyEntries))
            AddCandidate(part);

        var stripped = Regex.Replace(subject.Trim(), "战斗态|基础卡|普通态|状态卡|常态|留音", "");
        AddCandidate(stripped);
        foreach (var prefix in PrefixTokens)
        {
            if (!stripped.StartsWith(prefix, StringComparison.Ordinal) || stripped.Length <= prefix.Length)
                continue;
            AddCandidate(stripped.Substring(prefix.Length));
        }
        // 群像/别名：归属「七宗宗主」的技能，镜头出现太虚圣主或任一宗主即视为在场。
        foreach (var alias in CharacterAliasCatalog.GetOwnerAliases(subject, characters))
            AddCandidate(alias);

        return candidates;
    }

    private static string NormalizeNameContext(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = Regex.Replace(text, @"[（(][^）)]*[)）]", "");
        t = Regex.Replace(t, @"[「」『』“”""'、，。！？：:；;\s]", "");
        foreach (var token in NameNoiseTokens)
            t = t.Replace(token, "");
        foreach (var prefix in PrefixTokens)
            t = t.Replace(prefix, "");
        return t;
    }

    private static bool ContainsEndingState(string result, string endingState)
    {
        var expected = endingState.Trim();
        if (string.IsNullOrWhiteSpace(expected)) return true;
        if (result.Contains(expected, StringComparison.OrdinalIgnoreCase)) return true;

        var normalizedResult = NormalizeEndingText(result);
        var normalizedExpected = NormalizeEndingText(expected);
        if (normalizedExpected.Length == 0) return true;
        if (normalizedResult.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase)) return true;

        // 允许“完全/整个”“持续扩大/扩大”等表达差异：按双字窗口重叠度判断，
        // 但仍要求关键短语基本齐全，避免只匹配到一两个通用词就通过。
        if (normalizedResult.Length < 4 || normalizedExpected.Length < 4) return false;
        var expectedGrams = new HashSet<string>();
        for (var i = 0; i <= normalizedExpected.Length - 2; i++)
            expectedGrams.Add(normalizedExpected.Substring(i, 2));
        if (expectedGrams.Count == 0) return false;
        var matched = expectedGrams.Count(gram => normalizedResult.Contains(gram, StringComparison.OrdinalIgnoreCase));
        return (double)matched / expectedGrams.Count >= 0.55;
    }

    private static string NormalizeEndingText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = Regex.Replace(text, @"[\[【（(][^\]】）)]*[\]】）)]", "");
        t = Regex.Replace(t, @"[「」『』“”""'、，。！？：:；;\s]", "");
        return t;
    }

    private static bool ContainsDialogue(string result, string dialogue)
    {
        // 多行对白按行/句逐条校验，允许同一句台词拆到多个镜头。
        var expectedFragments = SplitDialogueFragments(dialogue);
        if (expectedFragments.Count == 0) return true;

        var resultLines = result
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeDialogueText)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var resultWhole = string.Join("", resultLines);

        return expectedFragments.All(fragment =>
            resultWhole.Contains(fragment, StringComparison.OrdinalIgnoreCase) ||
            resultLines.Any(line => line.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> SplitDialogueFragments(string text)
    {
        var fragments = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var clause in Regex.Split(line, @"(?<=[。！？!?])"))
            {
                var normalized = NormalizeDialogueText(clause);
                if (!string.IsNullOrWhiteSpace(normalized))
                    fragments.Add(normalized);
            }
        }
        return fragments.Distinct().ToList();
    }

    private static string NormalizeDialogueText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = Regex.Replace(text, @"[（(][^）)]*[)）]", "");
        t = Regex.Replace(t, @"[「」『』“”""'、，。！？：:；;\s]", "");
        return t.Replace("留音", "");
    }

    private static bool ContainsField(string shot, string fieldName) =>
        shot.Contains(fieldName, StringComparison.Ordinal);

    private static bool HasRhythmMarkers(string result) =>
        RhythmMarkers.Any(marker => result.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>提取「法天象地·霜月仙尊」的核心名（· 后末段）；无「·」前缀返回 null（整名匹配已由 SKILL_OWNER_MISMATCH 覆盖）。</summary>
    private static string? ExtractSkillCoreName(string formalName)
    {
        var idx = formalName.IndexOf('·');
        if (idx < 0 || idx == formalName.Length - 1) return null;
        var core = formalName.Substring(idx + 1).Trim();
        return core.Length >= 2 ? core : null;
    }

    /// <summary>命中“单发重击/秒杀/一击”类打斗模板时，不按多回合口径校验。</summary>
    private static bool IsSingleStrikeTemplate(string result)
    {
        return result.Contains("单发重击", StringComparison.Ordinal) ||
            result.Contains("秒杀", StringComparison.Ordinal) ||
            result.Contains("一击贯穿", StringComparison.Ordinal) ||
            result.Contains("碾压打脸", StringComparison.Ordinal) ||
            result.Contains("贴身短打", StringComparison.Ordinal) ||
            result.Contains("单发", StringComparison.Ordinal);
    }

    private static bool ContainsBeatText(string result, CombatBeat beat)
    {
        if (!string.IsNullOrWhiteSpace(beat.GrammarId) &&
            result.Contains(beat.GrammarId, StringComparison.OrdinalIgnoreCase))
            return true;

        var terms = (beat.ActionDescription ?? "")
            .Split(new[] { '，', '、', '；', '。', ',', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length >= 2)
            .ToList();
        var matched = terms.Count(term => result.Contains(term, StringComparison.OrdinalIgnoreCase));
        return terms.Count > 0 && matched >= Math.Max(1, terms.Count / 2);
    }

    private static int CountRhythmRounds(string result)
    {
        var count = 0;
        var lines = result.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains("回合", StringComparison.OrdinalIgnoreCase))
            {
                count++;
                continue;
            }
            if (RhythmMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase)))
                count++;
        }
        return count;
    }

    internal static List<string> SplitShots(string result)
    {
        var shots = new List<string>();
        var lines = result.Replace("\r\n", "\n").Split('\n');
        var current = new List<string>();
        var sawShot = false;
        foreach (var rawLine in lines)
        {
            if (StoryboardFrameParser.TryGetShotNumber(rawLine) != null)
            {
                sawShot = true;
                if (current.Count > 0)
                {
                    shots.Add(string.Join("\n", current));
                    current = new List<string>();
                }
            }
            // 单元标题、控制模式、技能/运镜原子等前置行不是镜头，不参与镜头级校验。
            if (!sawShot) continue;
            current.Add(rawLine);
        }
        if (current.Count > 0)
            shots.Add(string.Join("\n", current));
        return shots;
    }


    private static List<int> ReadBeatIndexes(string shot)
    {
        var indexes = new List<int>();
        foreach (var line in shot.Split('\n'))
        {
            var ids = StoryboardFrameParser.TryGetCombatBeatIds(line);
            if (string.IsNullOrWhiteSpace(ids)) continue;
            indexes.AddRange(ids.Split(',').Select(int.Parse));
        }
        return indexes.Distinct().OrderBy(x => x).ToList();
    }

    private static int? ReadShotDurationSeconds(string shot)
    {
        var lines = shot.Split('\n');
        foreach (var line in lines)
        {
            var match = ShotDurationExplicitRegex.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var seconds)) return seconds;
        }
        foreach (var line in lines)
        {
            var match = ShotDurationBareRegex.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var seconds)) return seconds;
        }
        return null;
    }

    private static double? ReadTimelineCoverageSeconds(string shot)
    {
        double? maxEnd = null;
        foreach (var line in shot.Split('\n'))
        {
            if (!TimelineFieldRegex.IsMatch(line)) continue;
            foreach (Match match in TimelineRangeRegex.Matches(line))
            {
                if (!double.TryParse(match.Groups[2].Value, out var end)) continue;
                if (maxEnd == null || end > maxEnd.Value) maxEnd = end;
            }
        }
        return maxEnd;
    }

    private static int CountActionEvents(string shot)
    {
        var count = 0;
        foreach (var marker in ActionEventMarkers)
        {
            if (shot.Contains(marker, StringComparison.OrdinalIgnoreCase)) count++;
        }
        return count;
    }

    internal static string? ReadShotId(string shot)
    {
        foreach (var line in shot.Split('\n'))
        {
            var id = StoryboardFrameParser.TryGetShotNumber(line);
            if (id != null) return id;
        }
        return null;
    }
}
