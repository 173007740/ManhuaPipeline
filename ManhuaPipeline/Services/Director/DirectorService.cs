using System.Text.Json;
using ManhuaPipeline.Models;
using Microsoft.Extensions.Configuration;

namespace ManhuaPipeline.Services.Director;

/// <summary>
/// 导演决策层服务：为 Stage 4 的每个 Unit 生成/补齐 DirectorPlan，
/// 并保存到数据库供 Stage 5 分镜生成读取。失败时静默回退，不阻塞 Stage 5。
/// </summary>
public class DirectorService
{
    private readonly LLMService _llm;
    private readonly DbService _db;
    private readonly IConfiguration _config;
    private readonly EpisodeDirectorPlanner _episodePlanner;
    private readonly int _maxConcurrency;
    private readonly double _temperature;

    public DirectorService(LLMService llm, DbService db, IConfiguration config)
    {
        _llm = llm;
        _db = db;
        _config = config;
        _episodePlanner = new EpisodeDirectorPlanner(llm, db, config);
        _maxConcurrency = Math.Max(1, config.GetValue("Director:MaxConcurrency", config.GetValue("Stage5:MaxConcurrency", 3)));
        _temperature = Math.Clamp(config.GetValue("Director:Temperature", 0.3), 0, 2);
    }

    public async Task<int> EnsureDirectorPlansAsync(
        int projectId,
        int userId,
        List<StageUnit> units,
        string apiUrl,
        string apiKey,
        string model,
        List<SkillLibraryItem> skills,
        List<FightTemplateItem> templates,
        List<CameraAtomItem> atoms,
        Action<StageProgressUpdate>? onProgress = null,
        string? thinkingMode = null)
    {
        if (units == null || units.Count == 0) return 0;

        var context = BuildContext(projectId, units, skills, templates, atoms);
        var total = units.Count;
        var completed = 0;
        var generated = 0;

        onProgress?.Invoke(new StageProgressUpdate
        {
            Done = 0,
            Total = total,
            CurrentPhase = "导演决策",
            Message = $"共 {total} 个单元"
        });

        for (var batchStart = 0; batchStart < units.Count; batchStart += _maxConcurrency)
        {
            var batch = units.Skip(batchStart).Take(_maxConcurrency).ToList();
            var tasks = batch
                .Select((unit, offset) => Task.Run(() => GenerateOrReusePlanAsync(
                    projectId, unit, batchStart + offset, context, apiUrl, apiKey, model, thinkingMode)))
                .ToList();

            var results = await Task.WhenAll(tasks);
            generated += results.Count(r => r != null);
            completed += batch.Count;

            onProgress?.Invoke(new StageProgressUpdate
            {
                Done = completed,
                Total = total,
                CurrentPhase = "导演决策",
                Message = $"已完成 {completed}/{total} 个单元导演决策"
            });
        }

        StoryboardPlanningLogger.Append(
            $"===== DirectorPlan 生成结束 项目ID={projectId} 单元数={total} 成功={generated} =====\n\n");
        return generated;
    }

    public Task<DirectorPlan?> GetPlanAsync(int projectId, string unitNumber)
        => Task.FromResult(_db.GetDirectorPlan(projectId, unitNumber));

    public Task<List<DirectorPlan>> GetPlansAsync(int projectId)
        => Task.FromResult(_db.GetDirectorPlans(projectId));

    public Task<int> EnsureEpisodeDirectorPlansAsync(
        int projectId,
        List<StageUnit> units,
        string apiUrl,
        string apiKey,
        string model,
        Action<StageProgressUpdate>? onProgress = null,
        bool force = false,
        string? thinkingMode = null)
        => _episodePlanner.EnsureEpisodeDirectorPlansAsync(projectId, units, apiUrl, apiKey, model, onProgress, force, thinkingMode);

    public Task<EpisodeDirectorPlan?> GetEpisodePlanAsync(int projectId, int episodeNumber)
        => _episodePlanner.GetPlanAsync(projectId, episodeNumber);

    public Task<EpisodeDirectorState?> GetEpisodeStateAsync(int projectId, int episodeNumber)
        => _episodePlanner.GetStateAsync(projectId, episodeNumber);

    public Task<EpisodeUnitStateSnapshot?> GetPreviousSnapshotAsync(
        int projectId,
        StageUnit unit,
        List<StageUnit>? units)
        => _episodePlanner.GetPreviousSnapshotAsync(projectId, unit, units);

    public Task MarkEpisodeUnitCompletedAsync(int projectId, StageUnit unit, DirectorPlan? plan, string storyboard)
        => _episodePlanner.MarkUnitCompletedAsync(projectId, unit, plan, storyboard);

    /// <summary>保存 Stage 5 校验结果：得分、是否 NeedsReview、结构化违规与返工次数。</summary>
    public Task SaveValidationAsync(int projectId, string unitNumber, DirectorValidationResult result, int repairCount)
    {
        if (result == null) return Task.CompletedTask;
        _db.SaveDirectorPlanValidation(
            projectId, unitNumber, result.Score, result.NeedsReview,
            JsonSerializer.Serialize(result.Violations, JsonOptions), repairCount);
        return Task.CompletedTask;
    }

    private async Task<DirectorPlan?> GenerateOrReusePlanAsync(
        int projectId,
        StageUnit unit,
        int unitIndex,
        DirectorContext context,
        string apiUrl,
        string apiKey,
        string model,
        string? thinkingMode = null)
    {
        try
        {
            var existing = _db.GetDirectorPlan(projectId, unit.UnitNumber);
            if (DirectorValidator.IsValid(existing))
                return existing;

            var prevUnit = unitIndex > 0 && unitIndex < context.Units.Count ? context.Units[unitIndex - 1] : null;
            var nextUnit = unitIndex >= 0 && unitIndex + 1 < context.Units.Count ? context.Units[unitIndex + 1] : null;
            var systemPrompt = DirectorPromptBuilder.BuildSystemPrompt();

            var episodePlan = context.GetEpisodePlan(unit.EpisodeNumber);
            var episodeState = _db.GetEpisodeDirectorState(projectId, unit.EpisodeNumber);
            var previousSnapshot = _episodePlanner.GetPreviousSnapshotAsync(projectId, unit, context.Units).GetAwaiter().GetResult();
            var userMessage = DirectorPromptBuilder.BuildUserMessage(
                context.Project,
                context.StylePrompt,
                context.StoryAnalysis,
                context.Blueprint,
                context.Episodes,
                unit,
                prevUnit,
                nextUnit,
                context.Characters,
                context.Props,
                context.Environments,
                context.Effects,
                context.Skills,
                context.Templates,
                context.Atoms,
                context.FightArcs,
                episodePlan,
                episodeState,
                previousSnapshot);

            var raw = await SafeCallAsync(apiUrl, apiKey, model, systemPrompt, userMessage, thinkingMode);
            var characterNames = context.Characters.Select(c => c.Name).ToList();
            var plan = DirectorValidator.ValidateAndMap(projectId, unit, raw ?? "", characterNames, context.FightArcs);

            if (plan == null)
            {
                LogDirectorCall(projectId, model, unit.UnitNumber, userMessage, raw, "校验失败，重试一次");
                raw = await SafeCallAsync(apiUrl, apiKey, model, systemPrompt, userMessage, thinkingMode);
                plan = DirectorValidator.ValidateAndMap(projectId, unit, raw ?? "", characterNames, context.FightArcs);
            }

            if (plan != null)
            {
                plan = EpisodeDirectorPlanParser.EnforceUnitLimits(plan, episodePlan, unit);
                _db.SaveDirectorPlan(plan);
                LogDirectorCall(projectId, model, unit.UnitNumber, userMessage, raw, "成功");
            }
            else
            {
                LogDirectorCall(projectId, model, unit.UnitNumber, userMessage, raw, "生成失败，本单元按原流程生成分镜");
            }

            return plan;
        }
        catch (Exception ex)
        {
            LogDirectorCall(projectId, model, unit.UnitNumber, "", null, "生成异常: " + ex.Message);
            return null;
        }
    }

    private async Task<string?> SafeCallAsync(string apiUrl, string apiKey, string model, string systemPrompt, string userMessage, string? thinkingMode = null)
    {
        try
        {
            return await _llm.CallAsync(apiUrl, apiKey, model, systemPrompt, userMessage, jsonMode: true, temperature: _temperature, thinkingMode: thinkingMode);
        }
        catch
        {
            return null;
        }
    }

    private DirectorContext BuildContext(
        int projectId,
        List<StageUnit> units,
        List<SkillLibraryItem> skills,
        List<FightTemplateItem> templates,
        List<CameraAtomItem> atoms)
    {
        var project = _db.GetProjectById(projectId);
        var stage2 = _db.GetStageData(projectId, 2);
        var stage3 = _db.GetStageData(projectId, 3);
        var stylePrompt = "";
        if (project?.StyleId.HasValue == true)
            stylePrompt = _db.GetVideoStyle(project.StyleId.Value)?.StylePrompt ?? "";

        return new DirectorContext
        {
            Project = project,
            StylePrompt = stylePrompt,
            StoryAnalysis = stage2?.LlmResponse ?? stage2?.Content,
            Blueprint = stage3?.LlmResponse ?? stage3?.Content,
            Episodes = _db.GetEpisodes(projectId),
            EpisodePlans = _db.GetEpisodeDirectorPlans(projectId),
            Characters = _db.GetCharacterAssets(projectId),
            Props = _db.GetPropAssets(projectId),
            Environments = _db.GetEnvAssets(projectId),
            Effects = _db.GetEffectAssets(projectId),
            Skills = skills ?? new List<SkillLibraryItem>(),
            Templates = templates ?? new List<FightTemplateItem>(),
            FightArcs = _db.GetFightArcTemplates(),
            Atoms = atoms ?? new List<CameraAtomItem>(),
            Units = units ?? new List<StageUnit>()
        };
    }

    private static void LogDirectorCall(
        int projectId,
        string model,
        string unitNumber,
        string request,
        string? raw,
        string status)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("================================================================");
        sb.AppendLine("[Director] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.AppendLine("项目ID: " + projectId);
        sb.AppendLine("模型: " + model);
        sb.AppendLine("单元: " + unitNumber);
        sb.AppendLine("状态: " + status);
        sb.AppendLine("---- 请求 ----");
        sb.AppendLine(request);
        sb.AppendLine("---- LLM返回 ----");
        sb.AppendLine(string.IsNullOrEmpty(raw) ? "(空)" : raw);
        sb.AppendLine("================================================================");
        sb.AppendLine();
        StoryboardPlanningLogger.Append(sb.ToString());
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed class DirectorContext
    {
        public Project? Project { get; set; }
        public string StylePrompt { get; set; } = "";
        public string? StoryAnalysis { get; set; }
        public string? Blueprint { get; set; }
        public List<Episode> Episodes { get; set; } = new();
        public List<EpisodeDirectorPlan> EpisodePlans { get; set; } = new();
        public List<CharacterAsset> Characters { get; set; } = new();
        public List<PropAsset> Props { get; set; } = new();
        public List<EnvironmentAsset> Environments { get; set; } = new();
        public List<EffectAsset> Effects { get; set; } = new();
        public List<SkillLibraryItem> Skills { get; set; } = new();
        public List<FightTemplateItem> Templates { get; set; } = new();
        public List<FightArcTemplate> FightArcs { get; set; } = new();
        public List<CameraAtomItem> Atoms { get; set; } = new();
        public List<StageUnit> Units { get; set; } = new();

        public EpisodeDirectorPlan? GetEpisodePlan(int episodeNumber)
        {
            return EpisodePlans.FirstOrDefault(p => p.EpisodeNumber == episodeNumber);
        }
    }
}
