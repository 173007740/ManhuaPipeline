using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ManhuaPipeline.Models;
using ManhuaPipeline.Models.Combat;
using ManhuaPipeline.Services.Combat;
using ManhuaPipeline.Services.Director;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace ManhuaPipeline.Services;

public class StoryboardPlanningService
{
    private readonly LLMService _llm;
    private readonly CombatGrammarEngine _grammar;
    private readonly DirectorService _director;
    private readonly DirectorSemanticValidator _semantic;
    private readonly int _maxConcurrency;

    public StoryboardPlanningService(LLMService llm, CombatGrammarEngine grammar, DirectorService director, DirectorSemanticValidator semantic, IConfiguration config)
    {
        _llm = llm;
        _grammar = grammar;
        _director = director;
        _semantic = semantic;
        _maxConcurrency = Math.Max(1, config.GetValue("Stage5:StoryboardMaxConcurrency", 4));
    }

    public async Task<string> PlanAsync(
        int projectId,
        string stage4Text,
        string apiUrl,
        string apiKey,
        string model,
        List<CameraAtomItem> atoms,
        List<SkillLibraryItem> skills,
        List<FightTemplateItem> templates,
        List<string>? characterNames,
        IReadOnlyList<CharacterAsset>? characters = null,
        string? envText = null,
        string? thinkingMode = null,
        Action<StageProgressUpdate>? onProgress = null,
        Action<string, string?, int, string?, int>? onChunk = null)
    {
        try
        {
            return await PlanAsyncCore(
                projectId, stage4Text, apiUrl, apiKey, model,
                atoms, skills, templates, characterNames, characters,
                envText, thinkingMode, onProgress, onChunk);
        }
        catch (Exception ex)
        {
            try
            {
                StoryboardPlanningLogger.Append(
                    $"\n===== [Stage5 主流程未捕获异常] {DateTime.Now:yyyy-MM-dd HH:mm:ss} 项目ID={projectId} =====\n{ex}\n");
            }
            catch
            {
                // 日志本身失败时不掩盖原始异常
            }
            throw;
        }
    }

    private async Task<string> PlanAsyncCore(
        int projectId,
        string stage4Text,
        string apiUrl,
        string apiKey,
        string model,
        List<CameraAtomItem> atoms,
        List<SkillLibraryItem> skills,
        List<FightTemplateItem> templates,
        List<string>? characterNames,
        IReadOnlyList<CharacterAsset>? characters = null,
        string? envText = null,
        string? thinkingMode = null,
        Action<StageProgressUpdate>? onProgress = null,
        Action<string, string?, int, string?, int>? onChunk = null)
    {
        var cameraText = BuildCameraAtomText(atoms);
        var skillText = BuildSkillText(skills);
        var fightSummary = BuildFightSummary(templates);

        var units = StageUnitParser.Parse(stage4Text);
        StoryboardPlanningLogger.Append(
            $"===== Stage5 开始 项目ID={projectId} 模型={model} 单元数={units.Count} =====\n\n");

        if (units.Count == 0)
        {
            onProgress?.Invoke(new StageProgressUpdate
            {
                Done = 0,
                Total = 1,
                CurrentPhase = "整段生成",
                Message = "未识别到单元，按整段生成分镜"
            });

            var request = BuildFallbackRequest(stage4Text, cameraText, skillText, fightSummary);
            var result = await _llm.PlanShots(stage4Text, apiUrl, apiKey, model, cameraText, skillText, fightSummary, envText: envText, thinkingMode: thinkingMode);
            LogCall(projectId, model, "整段自由分镜", "-", request, result);
            if (IsRefusalOrPlaceholder(result)) return "";
            if (!StoryboardFrameParser.HasAnyShot(result)) return "";
            var normalized = NormalizeUnitHeadings(result.Trim());
            if (!string.IsNullOrWhiteSpace(normalized)) onChunk?.Invoke(normalized, null, 0, null, 0);
            return normalized;
        }

        var total = units.Count;
        onProgress?.Invoke(new StageProgressUpdate
        {
            Done = 0,
            Total = total,
            CurrentPhase = "准备",
            Message = $"共 {total} 个单元"
        });

        var parts = new List<string>();
        var failedUnits = new List<string>();

        var episodeGroups = units
            .GroupBy(u => u.EpisodeNumber)
            .OrderBy(g => g.Key)
            .ToList();
        StoryboardPlanningLogger.Append(
            $"===== Stage5 全单元并行：最大并发 {_maxConcurrency}，单元间用导演计划状态衔接 =====\n\n");

        var overallSw = Stopwatch.StartNew();
        var processedCount = 0;

        foreach (var episodeGroup in episodeGroups)
        {
            var episodeUnits = episodeGroup.ToList();
            var episodePlan = await _director.GetEpisodePlanAsync(projectId, episodeGroup.Key);
            var results = new string?[episodeUnits.Count];
            var started = new bool[episodeUnits.Count];
            var done = new bool[episodeUnits.Count];
            var episodeSw = Stopwatch.StartNew();

            onProgress?.Invoke(new StageProgressUpdate
            {
                Done = processedCount,
                Total = total,
                CurrentPhase = "并行生成",
                Message = $"第{episodeGroup.Key}集共 {episodeUnits.Count} 个单元，全单元并行"
            });

            using var gate = new SemaphoreSlim(_maxConcurrency);
            var running = new List<Task<(int Index, StageUnit Unit, string? Result)>>();

            while (true)
            {
                for (var i = 0; i < episodeUnits.Count; i++)
                {
                    if (started[i]) continue;
                    started[i] = true;
                    var index = i;
                    var unit = episodeUnits[i];
                    var previousSnapshot = BuildPlannedSnapshot(episodePlan, episodeUnits, index);
                    await gate.WaitAsync();
                    running.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var sw = Stopwatch.StartNew();
                            StoryboardPlanningLogger.Append($"===== Unit {unit.UnitNumber} 开始 =====\n");
                            onProgress?.Invoke(new StageProgressUpdate
                            {
                                Done = processedCount,
                                Total = total,
                                CurrentUnit = unit.UnitNumber,
                                CurrentPhase = "并行生成",
                                Message = $"正在生成单元 {unit.UnitNumber}（{index + 1}/{total}）"
                            });
                            var result = await PlanUnitSafelyAsync(
                                projectId, unit, previousSnapshot, apiUrl, apiKey, model, atoms, skills, templates,
                                cameraText, skillText, fightSummary, characterNames, characters, envText, thinkingMode, onProgress, index, total);
                            sw.Stop();
                            StoryboardPlanningLogger.Append($"===== Unit {unit.UnitNumber} 完成 耗时 {sw.Elapsed.TotalSeconds:F1}s =====\n");
                            return (index, unit, result);
                        }
                        catch (Exception ex)
                        {
                            StoryboardPlanningLogger.Append(
                                $"===== Unit {unit.UnitNumber} 后台生成异常（已记录，视为失败单元） {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n{ex}\n");
                            return (index, unit, null);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }));
                }

                if (running.Count == 0)
                    throw new InvalidOperationException("分镜单元依赖解析失败，无法继续生成");

                var finished = await Task.WhenAny(running);
                running.Remove(finished);
                var (doneIndex, doneUnit, doneResult) = await finished;
                done[doneIndex] = true;
                processedCount++;
                results[doneIndex] = string.IsNullOrWhiteSpace(doneResult) ? null : doneResult.Trim();
                try
                {
                    if (results[doneIndex] == null)
                        failedUnits.Add(doneUnit.UnitNumber);
                    else
                        onChunk?.Invoke(results[doneIndex]!, doneUnit.UnitNumber, doneUnit.EpisodeNumber, doneUnit.Type, doneIndex);

                    onProgress?.Invoke(new StageProgressUpdate
                    {
                        Done = processedCount,
                        Total = total,
                        CurrentPhase = "已写入",
                        Message = $"已完成 {processedCount}/{total} 单元"
                    });
                }
                catch (Exception ex)
                {
                    StoryboardPlanningLogger.Append(
                        $"\n===== [Stage5 单元回调异常] {DateTime.Now:yyyy-MM-dd HH:mm:ss} 项目ID={projectId} 单元={doneUnit.UnitNumber} 类型={doneUnit.Type} onChunk/onProgress 抛出 =====\n{ex}\n");
                    throw;
                }

                if (done.All(x => x)) break;
            }

            foreach (var result in results)
            {
                if (!string.IsNullOrWhiteSpace(result)) parts.Add(result);
            }

            episodeSw.Stop();
            StoryboardPlanningLogger.Append(
                $"===== 第{episodeGroup.Key}集分镜完成 单元数={episodeUnits.Count} 成功={results.Count(r => !string.IsNullOrWhiteSpace(r))} 失败={results.Count(r => string.IsNullOrWhiteSpace(r))} 耗时 {episodeSw.Elapsed.TotalSeconds:F1}s =====\n\n");
        }

        overallSw.Stop();
        StoryboardPlanningLogger.Append(
            $"===== Stage5 分镜生成完成 总单元数={units.Count} 耗时 {overallSw.Elapsed.TotalSeconds:F1}s =====\n\n");

        if (failedUnits.Count > 0)
            throw new InvalidOperationException("以下单元分镜生成失败（返回空）：" + string.Join(", ", failedUnits));

        return string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private enum DirectorMode { Fast, Normal, Strict }

    private static bool IsFreeDrama(StageUnit unit) =>
        unit.Type.Contains("文戏") ||
        unit.Type.Contains("情感") ||
        unit.Type.Contains("日常") ||
        unit.Type.Contains("喜剧") ||
        unit.Type.Contains("悬疑") ||
        unit.Type.Contains("惊悚");

    private static EpisodeUnitStateSnapshot? BuildPlannedSnapshot(
        EpisodeDirectorPlan? episodePlan, List<StageUnit> episodeUnits, int index)
    {
        if (index == 0 || episodePlan == null) return null;
        var prev = episodeUnits[index - 1];
        var endState = episodePlan.UnitEndStates?.FirstOrDefault(s =>
            string.Equals(
                EpisodeDirectorPlanParser.NormalizeUnitToken(s.UnitNumber),
                EpisodeDirectorPlanParser.NormalizeUnitToken(prev.UnitNumber),
                StringComparison.OrdinalIgnoreCase));
        if (endState == null) return null;
        return new EpisodeUnitStateSnapshot
        {
            ProjectId = 0,
            EpisodeNumber = prev.EpisodeNumber > 0 ? prev.EpisodeNumber : 0,
            UnitNumber = prev.UnitNumber,
            Source = "episode_plan",
            State = endState
        };
    }

    private async Task<string?> PlanUnitSafelyAsync(
        int projectId,
        StageUnit unit,
        EpisodeUnitStateSnapshot? previousSnapshot,
        string apiUrl,
        string apiKey,
        string model,
        List<CameraAtomItem> atoms,
        List<SkillLibraryItem> skills,
        List<FightTemplateItem> templates,
        string? cameraText,
        string? skillText,
        string? fightSummary,
        List<string>? characterNames,
        IReadOnlyList<CharacterAsset>? characters,
        string? envText,
        string? thinkingMode,
        Action<StageProgressUpdate>? onProgress,
        int completed,
        int total)
    {
        try
        {
            var directorPlan = await _director.GetPlanAsync(projectId, unit.UnitNumber);
            var mode = ResolveDirectorMode(directorPlan);
            var maxRepairs = mode switch { DirectorMode.Fast => 0, DirectorMode.Normal => 1, _ => 2 };
            var semanticEnabled = mode != DirectorMode.Fast;

            var first = await PlanUnitAsync(
                projectId, unit, previousSnapshot, apiUrl, apiKey, model, atoms, skills, templates,
                cameraText, skillText, fightSummary, characterNames, characters, envText, thinkingMode, onProgress, completed, total, new List<string>());
            if (string.IsNullOrWhiteSpace(first) || IsRefusalOrPlaceholder(first))
                return null;

            // 无实际镜头的返回视为废响应：不参与最高分比较，也绝不写入数据库。
            var best = StoryboardFrameParser.HasAnyShot(first) ? first : null;
            var bestValidation = semanticEnabled
                ? await ValidateWithSemanticAsync(
                    projectId, directorPlan, unit, previousSnapshot, first, apiUrl, apiKey, model, skills, characters, thinkingMode)
                : await BuildRuleValidationAsync(
                    projectId, directorPlan, unit, previousSnapshot, first, skills, characters);
            if (best != null && bestValidation.Passed)
            {
                await PersistValidationAsync(projectId, unit.UnitNumber, bestValidation, 0);
                await _director.MarkEpisodeUnitCompletedAsync(projectId, unit, directorPlan, first);
                return NormalizeUnitHeadings(first.Trim());
            }

            // 镜头数不足或镜头时长总和与单元时长不符时至少返工一次，禁止低思考模式下静默保留。
            if (bestValidation.Violations?.Any(v => v.Code is "SHOT_COUNT_LOW" or "DURATION_SUM") == true)
                maxRepairs = Math.Max(maxRepairs, 1);

            LogCall(projectId, model, $"导演校验未通过（{mode} 模式，返工上限 {maxRepairs}）", unit.UnitNumber,
                StageUnitParser.ToPlanText(unit), ValidationSummary(bestValidation));
            var feedback = DirectorRepairPlanner.BuildRepairFeedback(bestValidation, first);

            for (var repair = 1; repair <= maxRepairs; repair++)
            {
                var repaired = await PlanUnitAsync(
                    projectId, unit, previousSnapshot, apiUrl, apiKey, model, atoms, skills, templates,
                    cameraText, skillText, fightSummary, characterNames, characters, envText, thinkingMode, onProgress, completed, total, feedback);
                if (string.IsNullOrWhiteSpace(repaired) || IsRefusalOrPlaceholder(repaired))
                    break;
                if (!StoryboardFrameParser.HasAnyShot(repaired))
                {
                    LogCall(projectId, model, $"返工 {repair}/{maxRepairs} 无实际镜头，跳过", unit.UnitNumber,
                        StageUnitParser.ToPlanText(unit), repaired);
                    continue;
                }

                var validation = semanticEnabled
                    ? await ValidateWithSemanticAsync(
                        projectId, directorPlan, unit, previousSnapshot, repaired, apiUrl, apiKey, model, skills, characters, thinkingMode)
                    : await BuildRuleValidationAsync(
                        projectId, directorPlan, unit, previousSnapshot, repaired, skills, characters);
                if (best == null || validation.Score > bestValidation.Score)
                {
                    best = repaired;
                    bestValidation = validation;
                }
                if (validation.Passed)
                {
                    await PersistValidationAsync(projectId, unit.UnitNumber, validation, repair);
                    await _director.MarkEpisodeUnitCompletedAsync(projectId, unit, directorPlan, repaired);
                    return NormalizeUnitHeadings(repaired.Trim());
                }

                LogCall(projectId, model, $"导演校验仍未通过（{mode} 模式，返工 {repair + 1}/{maxRepairs}）", unit.UnitNumber,
                    StageUnitParser.ToPlanText(unit), ValidationSummary(validation));
                feedback = DirectorRepairPlanner.BuildRepairFeedback(validation, repaired);
            }

            if (best == null)
            {
                LogCall(projectId, model, "所有生成结果均无实际镜头", unit.UnitNumber,
                    StageUnitParser.ToPlanText(unit), "(无镜头)");
                return null;
            }

            bestValidation.NeedsReview = !bestValidation.Passed;
            await PersistValidationAsync(projectId, unit.UnitNumber, bestValidation, maxRepairs);
            await _director.MarkEpisodeUnitCompletedAsync(projectId, unit, directorPlan, best);
            LogCall(projectId, model, $"{mode} 模式校验耗尽（保留最高分版本）", unit.UnitNumber,
                StageUnitParser.ToPlanText(unit), ValidationSummary(bestValidation) + "; NeedsReview=" + bestValidation.NeedsReview);
            return NormalizeUnitHeadings(best.Trim());
        }
        catch (Exception ex)
        {
            LogCall(projectId, model, "单元生成异常", unit.UnitNumber, StageUnitParser.ToPlanText(unit), "异常: " + ex.Message);
            return null;
        }
    }

    private static DirectorMode ResolveDirectorMode(DirectorPlan? directorPlan)
    {
        var intensity = directorPlan?.IntensityLevel ?? 3;
        if (intensity >= 8) return DirectorMode.Strict;
        if (intensity >= 5) return DirectorMode.Normal;
        return DirectorMode.Fast;
    }

    private async Task<DirectorValidationResult> ValidateWithSemanticAsync(
        int projectId,
        DirectorPlan? directorPlan,
        StageUnit unit,
        EpisodeUnitStateSnapshot? previousSnapshot,
        string result,
        string apiUrl,
        string apiKey,
        string model,
        List<SkillLibraryItem> skills,
        IReadOnlyList<CharacterAsset>? characters = null,
        string? thinkingMode = null)
    {
        var rule = await BuildRuleValidationAsync(projectId, directorPlan, unit, previousSnapshot, result, skills, characters);
        if (!rule.Passed) return rule;
        var semantic = await _semantic.ValidateAsync(directorPlan, unit, result, apiUrl, apiKey, model, thinkingMode: thinkingMode);
        return DirectorRuleValidator.Merge(rule, semantic);
    }

    private async Task<DirectorValidationResult> BuildRuleValidationAsync(
        int projectId,
        DirectorPlan? directorPlan,
        StageUnit unit,
        EpisodeUnitStateSnapshot? previousSnapshot,
        string result,
        List<SkillLibraryItem> skills,
        IReadOnlyList<CharacterAsset>? characters = null)
    {
        var episodePlan = await _director.GetEpisodePlanAsync(projectId, unit.EpisodeNumber);
        var episodeState = await _director.GetEpisodeStateAsync(projectId, unit.EpisodeNumber);
        return DirectorRuleValidator.Validate(directorPlan, unit, result, skills, episodePlan, episodeState, previousSnapshot, characters);
    }

    private Task PersistValidationAsync(int projectId, string unitNumber, DirectorValidationResult result, int repairCount)
        => _director.SaveValidationAsync(projectId, unitNumber, result, repairCount);

    private static string ValidationSummary(DirectorValidationResult validation)
    {
        var violations = validation.Violations == null ? "" : string.Join("；", validation.Violations.Select(v => v.Message));
        return $"得分 {validation.Score}/100；{violations}";
    }

    private async Task<string> PlanUnitAsync(
        int projectId,
        StageUnit unit,
        EpisodeUnitStateSnapshot? previousSnapshot,
        string apiUrl,
        string apiKey,
        string model,
        List<CameraAtomItem> atoms,
        List<SkillLibraryItem> skills,
        List<FightTemplateItem> templates,
        string? cameraText,
        string? skillText,
        string? fightSummary,
        List<string>? characterNames,
        IReadOnlyList<CharacterAsset>? characters,
        string? envText,
        string? thinkingMode,
        Action<StageProgressUpdate>? onProgress,
        int completed,
        int total,
        List<string> complianceFeedback)
    {
        var directorPlan = await _director.GetPlanAsync(projectId, unit.UnitNumber);
        var directorText = directorPlan == null ? null : DirectorPromptBuilder.BuildDecisionSection(directorPlan);
        var episodePlan = await _director.GetEpisodePlanAsync(projectId, unit.EpisodeNumber);
        var episodeState = await _director.GetEpisodeStateAsync(projectId, unit.EpisodeNumber);
        var v3Constraint = EpisodeDirectorPlanParser.BuildUnitConstraintSection(episodePlan, episodeState, unit, previousSnapshot);
        if (!string.IsNullOrWhiteSpace(v3Constraint))
            directorText = (directorText ?? "") + "\n\n" + v3Constraint;
        if (string.IsNullOrWhiteSpace(directorText)) directorText = null;
        if (unit.IsCombat)
            return await PlanCombatUnitAsync(
                projectId, unit, apiUrl, apiKey, model, atoms, skills, templates,
                cameraText, skillText, fightSummary, characterNames, characters, envText, thinkingMode, onProgress, completed, total,
                directorPlan, directorText, complianceFeedback);

        var planText = StageUnitParser.ToPlanText(unit);
        if (IsFreeDrama(unit))
        {
            onProgress?.Invoke(new StageProgressUpdate
            {
                Done = completed,
                Total = total,
                CurrentUnit = unit.UnitNumber,
                CurrentPhase = "自由分镜",
                Message = $"正在生成单元 {unit.UnitNumber} 的自由分镜"
            });
            var request = BuildFreeDramaRequest(planText, cameraText, directorText);
            var result = await _llm.PlanShots(planText, apiUrl, apiKey, model, cameraText, null, null, directorText, envText, thinkingMode: thinkingMode, complianceFeedback: complianceFeedback);
            LogCall(projectId, model, "自由分镜（运镜原子可选）", unit.UnitNumber, request, result);
            return result;
        }

        onProgress?.Invoke(new StageProgressUpdate
        {
            Done = completed,
            Total = total,
            CurrentUnit = unit.UnitNumber,
            CurrentPhase = "非打斗分镜",
            Message = $"正在生成单元 {unit.UnitNumber} 的分镜"
        });
        var nonCombatRequest = BuildNonCombatRequest(planText, skillText, cameraText, directorText);
        var nonCombatResult = await _llm.PlanShots(planText, apiUrl, apiKey, model, cameraText, skillText, null, directorText, envText, thinkingMode: thinkingMode, complianceFeedback: complianceFeedback);
        LogCall(projectId, model, "非打斗分镜（技能库+运镜原子）", unit.UnitNumber, nonCombatRequest, nonCombatResult);
        return nonCombatResult;
    }

    private async Task<string> PlanCombatUnitAsync(
        int projectId,
        StageUnit unit,
        string apiUrl,
        string apiKey,
        string model,
        List<CameraAtomItem> atoms,
        List<SkillLibraryItem> skills,
        List<FightTemplateItem> templates,
        string? cameraText,
        string? skillText,
        string? fightSummary,
        List<string>? characterNames,
        IReadOnlyList<CharacterAsset>? characters,
        string? envText,
        string? thinkingMode,
        Action<StageProgressUpdate>? onProgress,
        int completed,
        int total,
        DirectorPlan? directorPlan,
        string? directorText,
        List<string> complianceFeedback)
    {
        var planText = StageUnitParser.ToPlanText(unit);

        var intentRequest = "打斗单元原文:\n" + planText;
        var intent = await _llm.ExtractCombatIntent(planText, apiUrl, apiKey, model, thinkingMode: thinkingMode);
        LogCall(
            projectId, model, "打斗意图提取", unit.UnitNumber, intentRequest,
            intent == null ? "(意图提取失败)" : JsonSerializer.Serialize(intent, JsonOptions));

        if (intent == null)
        {
            onProgress?.Invoke(new StageProgressUpdate
            {
                Done = completed,
                Total = total,
                CurrentUnit = unit.UnitNumber,
                CurrentPhase = "打斗自由回退",
                Message = $"单元 {unit.UnitNumber} 意图提取失败，按自由分镜生成"
            });
            var request = BuildFallbackRequest(planText, cameraText, skillText, fightSummary);
            var result = await _llm.PlanShots(planText, apiUrl, apiKey, model, cameraText, skillText, fightSummary, directorText, envText, thinkingMode: thinkingMode);
            LogCall(projectId, model, "打斗自由回退（意图失败）", unit.UnitNumber, request, result);
            return result;
        }

        if (intent.Duration is not (5 or 11 or 15))
            intent.Duration = unit.Duration is 5 or 11 or 15 ? unit.Duration : 11;
        // 时长档位不限制交锋频率：5 秒同样允许承载多个快速攻防回合，由内容密度决定。
        if (intent.Intensity is < 1 or > 5)
            intent.Intensity = Math.Clamp(unit.Duration == 15 ? 4 : 3, 1, 5);

        // V1.5：导演 ActionPlan → 系统展开 CombatBeats，分镜只负责“怎么拍”。
        var directorBeats = ExpandDirectorBeats(directorPlan);
        var directorActionPlan = ParseDirectorActionPlan(directorPlan);
        var packedShots = ShotPacker.Pack(
            ActionChainBuilder.Build(directorBeats, directorActionPlan), unit, directorPlan);

        string? lockedActionChain = null;
        if (CombatIntentMapper.IsMovementCombat(intent, unit) || CombatIntentMapper.IsSoloCombat(intent, unit))
        {
            onProgress?.Invoke(new StageProgressUpdate
            {
                Done = completed,
                Total = total,
                CurrentUnit = unit.UnitNumber,
                CurrentPhase = "动作链跳过",
                Message = $"单元 {unit.UnitNumber} 为位移/单人爆发单元，不锁定动作链"
            });
            LogCall(projectId, model, "动作链跳过（位移/单人爆发单元）", unit.UnitNumber,
                JsonSerializer.Serialize(intent, JsonOptions), "不生成锁定动作链，由模板自由编排");
        }
        else
        {
            onProgress?.Invoke(new StageProgressUpdate
            {
                Done = completed,
                Total = total,
                CurrentUnit = unit.UnitNumber,
                CurrentPhase = "动作链编排",
                Message = $"正在编排单元 {unit.UnitNumber} 的动作链"
            });
            try
            {
                if (directorBeats.Count > 0)
                {
                    lockedActionChain = BuildBeatChainText(directorBeats);
                    LogCall(projectId, model, "导演节拍展开", unit.UnitNumber,
                        string.IsNullOrWhiteSpace(directorPlan?.ActionPlan) ? "(无 ActionPlan)" : directorPlan.ActionPlan,
                        string.IsNullOrWhiteSpace(lockedActionChain) ? "(动作链为空)" : lockedActionChain);
                }
                else
                {
                    var grammarRequest = CombatIntentMapper.ToCombatRequest(intent, unit);
                    var lockedIds = ParseGrammarIds(directorPlan?.CombatGrammarIds);
                    var sequence = lockedIds.Count == 0
                        ? _grammar.Generate(grammarRequest)
                        : _grammar.GenerateLocked(grammarRequest, lockedIds);
                    lockedActionChain = CombatIntentMapper.BuildLockedActionChain(sequence);
                    LogCall(projectId, model, "动作链编排", unit.UnitNumber,
                        JsonSerializer.Serialize(grammarRequest, JsonOptions),
                        string.IsNullOrWhiteSpace(lockedActionChain) ? "(动作链为空)" : lockedActionChain);
                }
            }
            catch (Exception ex)
            {
                LogCall(projectId, model, "动作链编排失败", unit.UnitNumber, planText, "异常: " + ex.Message);
            }
        }

        var template = CombatPlanSelector.SelectFightTemplate(templates, intent, unit.DirectorTemplate, unit.RawText);
        if (template == null)
        {
            onProgress?.Invoke(new StageProgressUpdate
            {
                Done = completed,
                Total = total,
                CurrentUnit = unit.UnitNumber,
                CurrentPhase = "打斗自由回退",
                Message = $"单元 {unit.UnitNumber} 未匹配到模板，按自由分镜生成"
            });
            var request = BuildFallbackRequest(planText, cameraText, skillText, fightSummary);
            var result = await _llm.PlanShots(planText, apiUrl, apiKey, model, cameraText, skillText, fightSummary, directorText, envText, thinkingMode: thinkingMode);
            LogCall(projectId, model, "打斗自由回退（未匹配模板）", unit.UnitNumber, request, result);
            return result;
        }

        var selectedAtoms = CombatPlanSelector.SelectCameraAtoms(atoms, unit, intent);
        var selectedSkills = CombatPlanSelector.SelectSkills(skills, intent, unit.RawText, characterNames, characters);
        // 分集细化点名的技能直接锁定到分镜，不依赖意图提取结果，防止丢失。
        var stage4LockedSkills = CombatPlanSelector.MatchSkillsToText(skills, unit.RawText, characterNames, characters);
        selectedSkills = MergeLockedSkills(selectedSkills, stage4LockedSkills);
        onProgress?.Invoke(new StageProgressUpdate
        {
            Done = completed,
            Total = total,
            CurrentUnit = unit.UnitNumber,
            CurrentPhase = "打斗分镜生成",
            Message = $"正在生成单元 {unit.UnitNumber} 的打斗分镜"
        });

        var requestBody = BuildCombatRequest(
            planText, intent, template, selectedAtoms, selectedSkills, lockedActionChain, directorText);
        try
        {
            var combatResult = await _llm.PlanCombatShots(
                unit, intent, template, selectedAtoms, selectedSkills, apiUrl, apiKey, model,
                lockedActionChain, directorText, directorBeats, packedShots, complianceFeedback, envText, thinkingMode: thinkingMode);
            LogCall(projectId, model, "打斗受控分镜", unit.UnitNumber, requestBody, combatResult);
            return combatResult;
        }
        catch (Exception ex)
        {
            LogCall(projectId, model, "打斗受控失败", unit.UnitNumber, requestBody, "异常: " + ex.Message);
            try
            {
                var retryResult = await _llm.PlanCombatShots(
                    unit, intent, template, selectedAtoms, selectedSkills, apiUrl, apiKey, model,
                    lockedActionChain, directorText, directorBeats, packedShots, complianceFeedback, envText, thinkingMode: thinkingMode);
                LogCall(projectId, model, "打斗受控分镜（重试成功）", unit.UnitNumber, requestBody, retryResult);
                return retryResult;
            }
            catch (Exception retryEx)
            {
                LogCall(projectId, model, "打斗受控重试失败", unit.UnitNumber, requestBody, "异常: " + retryEx.Message);
                onProgress?.Invoke(new StageProgressUpdate
                {
                    Done = completed,
                    Total = total,
                    CurrentUnit = unit.UnitNumber,
                    CurrentPhase = "打斗自由回退",
                    Message = $"单元 {unit.UnitNumber} 受控生成重试失败，按自由分镜重试"
                });
                var fallbackRequest = BuildFallbackRequest(planText, cameraText, skillText, fightSummary);
                var fallbackResult = await _llm.PlanShots(planText, apiUrl, apiKey, model, cameraText, skillText, fightSummary, directorText, envText, thinkingMode: thinkingMode);
                LogCall(projectId, model, "打斗自由回退（受控失败）", unit.UnitNumber, fallbackRequest, fallbackResult);
                return fallbackResult;
            }
        }
    }

    private List<CombatBeat> ExpandDirectorBeats(DirectorPlan? directorPlan)
    {
        if (directorPlan == null) return new List<CombatBeat>();
        var actionPlan = ParseDirectorActionPlan(directorPlan);
        actionPlan ??= new DirectorActionPlan
        {
            PrimaryFighterId = directorPlan.PrimarySubject,
            EnemyIds = string.IsNullOrWhiteSpace(directorPlan.SecondarySubject) ? [] : [directorPlan.SecondarySubject.Trim()],
            RoundCount = directorPlan.CombatRoundCount,
            CombatGrammarIds = ParseGrammarIds(directorPlan.CombatGrammarIds)
        };
        return _grammar.ExpandFromActionPlan(actionPlan).Beats;
    }

    private static DirectorActionPlan? ParseDirectorActionPlan(DirectorPlan? directorPlan)
    {
        if (directorPlan == null || string.IsNullOrWhiteSpace(directorPlan.ActionPlan)) return null;
        try
        {
            return JsonSerializer.Deserialize<DirectorActionPlan>(directorPlan.ActionPlan);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildBeatChainText(List<CombatBeat> beats)
    {
        if (beats.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var beat in beats)
        {
            sb.AppendLine(
                $"Beat{beat.Index:00} [{beat.GrammarId}] {beat.ActionDescription}（{beat.SpatialRelation}）");
        }
        return sb.ToString().TrimEnd();
    }

    private static string BuildFreeDramaRequest(string freeText, string? cameraText, string? directorText = null) =>
        "单元原文:\n" + freeText +
        "\n\n【运镜原子菜单（可选参考）】\n" + (string.IsNullOrWhiteSpace(cameraText) ? "无" : cameraText) +
        "\n\n技能库: 无\n打斗模板库: 无" + AppendDirectorSection(directorText);

    private static string BuildNonCombatRequest(string otherText, string? skillText, string? cameraText, string? directorText = null) =>
        "单元原文:\n" + otherText +
        "\n\n【运镜原子菜单】\n" + (string.IsNullOrWhiteSpace(cameraText) ? "无" : cameraText) +
        "\n\n技能库:\n" + (string.IsNullOrWhiteSpace(skillText) ? "无" : skillText) +
        "\n打斗模板库: 无" + AppendDirectorSection(directorText);

    private static string BuildFallbackRequest(
        string planText,
        string? cameraText,
        string? skillText,
        string? fightSummary,
        string? directorText = null) =>
        "单元原文:\n" + planText +
        "\n\n【运镜原子菜单】\n" + (string.IsNullOrWhiteSpace(cameraText) ? "无" : cameraText) +
        "\n\n【技能库】\n" + (string.IsNullOrWhiteSpace(skillText) ? "无" : skillText) +
        "\n\n【打斗模板库】\n" + (string.IsNullOrWhiteSpace(fightSummary) ? "无" : fightSummary) + AppendDirectorSection(directorText);

    private static string BuildCombatRequest(
        string planText,
        CombatIntent intent,
        FightTemplateItem template,
        List<CameraAtomItem> selectedAtoms,
        List<SkillLibraryItem> selectedSkills,
        string? lockedActionChain,
        string? directorText = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("打斗单元原文:");
        sb.AppendLine(planText);
        sb.AppendLine();
        sb.AppendLine("打斗意图:");
        sb.AppendLine(JsonSerializer.Serialize(intent, JsonOptions));
        sb.AppendLine();
        sb.AppendLine("【选中打斗模板】");
        sb.AppendLine("模板ID: " + template.FightTemplateId);
        sb.AppendLine("模板名: " + template.Name);
        sb.AppendLine("层级: T" + template.Tier);
        sb.AppendLine("时长: " + template.Duration + "s");
        sb.AppendLine("适用场景: " + template.Scene);
        sb.AppendLine("节拍: " + template.Beat);
        sb.AppendLine("动作行: " + template.ActionPrompt);
        sb.AppendLine("运镜行: " + template.CameraPrompt);
        sb.AppendLine("约束行: " + template.ConstraintPrompt);
        sb.AppendLine();
        sb.AppendLine("【选中运镜原子】");
        if (selectedAtoms.Count == 0)
        {
            sb.AppendLine("无");
        }
        else
        {
            foreach (var a in selectedAtoms)
                sb.AppendLine("- " + a.Name + "（" + a.Category + "）: " + a.Description);
        }
        sb.AppendLine();
        sb.AppendLine("【选中技能】");
        if (selectedSkills.Count == 0)
        {
            sb.AppendLine("无");
        }
        else
        {
            foreach (var s in selectedSkills)
                sb.AppendLine("- " + s.Name + "（" + s.Element + "系·T" + s.Tier + "）: " + s.PromptVideoForLLM);
        }
        sb.AppendLine();
        sb.AppendLine("【锁定动作链】");
        sb.AppendLine(string.IsNullOrWhiteSpace(lockedActionChain) ? "无" : lockedActionChain);
        if (!string.IsNullOrWhiteSpace(directorText))
        {
            sb.AppendLine();
            sb.AppendLine("【导演决策】");
            sb.AppendLine(directorText);
        }
        return sb.ToString();
    }

    private static string AppendDirectorSection(string? directorText) =>
        string.IsNullOrWhiteSpace(directorText) ? "" : "\n\n【导演决策】\n" + directorText;

    private static void LogCall(
        int projectId,
        string model,
        string phase,
        string unit,
        string request,
        string? result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================");
        sb.AppendLine("[Storyboard] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.AppendLine("项目ID: " + projectId);
        sb.AppendLine("模型: " + model);
        sb.AppendLine("单元: " + unit);
        sb.AppendLine("阶段: " + phase);
        sb.AppendLine("---- 请求 ----");
        sb.AppendLine(request);
        sb.AppendLine("---- LLM返回 ----");
        sb.AppendLine(string.IsNullOrEmpty(result) ? "(空)" : result);
        sb.AppendLine("================================================================");
        sb.AppendLine();
        StoryboardPlanningLogger.Append(sb.ToString());
    }

    private static List<string> ParseGrammarIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw
            .Split(new[] { ',', '，', ';', '；', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsRefusalOrPlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("尚未提供", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("请您补充具体单元内容", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("以下是您指定的空模板", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(text, @"【单元\d+\.X】") ||
               Regex.IsMatch(text, @"\d+\.X-\d+");
    }

    private static string NormalizeUnitHeadings(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var seenUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            var match = Regex.Match(line, @"^#{1,6}\s*【单元([\d.]+[a-zA-Z]?)】\s*(.*)$");
            if (match.Success)
            {
                var unitNumber = match.Groups[1].Value.Trim();
                if (!seenUnits.Add(unitNumber))
                {
                    var title = match.Groups[2].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(title))
                        sb.AppendLine("#### " + title);
                    continue;
                }
            }
            sb.AppendLine(rawLine);
        }
        return sb.ToString().TrimEnd('\n', '\r');
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string? BuildCameraAtomText(List<CameraAtomItem> atoms)
    {
        if (atoms == null || atoms.Count == 0) return null;
        return string.Join("\n\n", atoms
            .GroupBy(a => a.Category)
            .Select(g => "【" + g.Key + "】\n" + string.Join("\n", g.Select(a => "- " + a.Name + "：" + a.Description))));
    }

    private static string? BuildSkillText(List<SkillLibraryItem> skills)
    {
        if (skills == null || skills.Count == 0) return null;
        return string.Join("\n", skills.Select(s => "- " + s.Name + "（" + s.Element + "系·T" + s.Tier + "）: " + s.PromptVideoForLLM));
    }

    private static List<SkillLibraryItem> MergeLockedSkills(
        List<SkillLibraryItem> selectedSkills,
        List<SkillLibraryItem> lockedSkills)
    {
        var merged = new List<SkillLibraryItem>();
        foreach (var skill in lockedSkills.Concat(selectedSkills))
        {
            if (merged.Any(x => string.Equals(x.Name, skill.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            merged.Add(skill);
        }
        return merged;
    }

    private static string? BuildFightSummary(List<FightTemplateItem> templates)
    {
        if (templates == null || templates.Count == 0) return null;
        return string.Join("\n", templates.Select(t => "- 模板" + t.Name + "（T" + t.Tier + "·" + t.Duration + "s）: 适用:" + t.Scene + "；节拍:" + t.Beat));
    }
}
