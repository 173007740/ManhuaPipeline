using System.Text.Json;
using ManhuaPipeline.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Services;

/// <summary>
/// L2 连续性层服务：负责「抽取连续性表」与「拼装注入下游的连续性上下文」。
/// 抽取时机为 Stage 4 分集细化开始前（已有则跳过），因此不占用任何阶段编号。
/// 表结构见 Database/Upgrade_连续性表.sql。
/// </summary>
public class ContinuityExtractionService
{
    private readonly DbService _db;
    private readonly LLMService _llm;
    private readonly ILogger<ContinuityExtractionService> _logger;

    public ContinuityExtractionService(DbService db, LLMService llm, ILogger<ContinuityExtractionService> logger)
    {
        _db = db;
        _llm = llm;
        _logger = logger;
    }

    /// <summary>
    /// 确保项目已有连续性表：六类齐全时直接返回 true；否则调 LLM 抽取一次并落库。
    /// force=true 时强制重抽。返回最终是否可用（表缺失、抽取失败等一律返回 false，由调用方降级）。
    /// </summary>
    public async Task<bool> EnsureContinuityTablesAsync(
        int projectId, string apiUrl, string apiKey, string model,
        string? assetCatalog = null, string? thinkingMode = null, bool force = false)
    {
        try
        {
            if (!force)
            {
                var existing = _db.GetContinuityTables(projectId);
                if (existing.Select(t => t.TableType).Distinct().Count() >= ContinuityTableTypes.All.Length)
                    return true;
            }

            var proj = _db.GetProjectById(projectId);
            if (proj == null || string.IsNullOrWhiteSpace(proj.ScriptContent))
            {
                _logger.LogWarning("[连续性表] 项目 {ProjectId} 无剧本内容，跳过抽取", projectId);
                return false;
            }

            var storyAnalysis = _db.GetStageData(projectId, PipelineStage.StoryAnalysis)?.LlmResponse;
            var blueprint = _db.GetStageData(projectId, PipelineStage.GlobalBlueprint)?.LlmResponse;

            var result = await _llm.ExtractContinuity(
                proj.ScriptContent, storyAnalysis, blueprint, assetCatalog, apiUrl, apiKey, model, thinkingMode);
            if (result == null || result.IsEmpty)
            {
                _logger.LogWarning("[连续性表] 项目 {ProjectId} 抽取结果为空，跳过落库", projectId);
                return false;
            }

            var rows = BuildRows(projectId, result);
            if (rows.Count == 0)
            {
                _logger.LogWarning("[连续性表] 项目 {ProjectId} 六类表渲染后均为空，跳过落库", projectId);
                return false;
            }

            var written = _db.ReplaceContinuityTables(projectId, rows);
            _logger.LogInformation("[连续性表] 项目 {ProjectId} 已写入 {Count} 类连续性表：{Types}",
                projectId, written, string.Join("、", rows.Select(r => r.TypeName)));
            return true;
        }
        catch (SqlException ex) when (ex.Number is 208 or 2812)
        {
            _logger.LogWarning(ex, "[连续性表] ProjectContinuityTables 表不存在，已跳过。请先执行 ManhuaPipeline/Database/Upgrade_连续性表.sql。项目 {ProjectId}", projectId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[连续性表] 抽取失败，不影响分集细化主流程。项目 {ProjectId}", projectId);
            return false;
        }
    }

    /// <summary>
    /// 把已落库的连续性表拼成注入 Stage 4 分集细化的文本；无数据或表缺失返回 null。
    /// 按 short-drama-agent 第 6-11 节的固定顺序输出，便于模型对齐。
    /// </summary>
    public string? BuildContinuityText(int projectId)
    {
        try
        {
            var tables = _db.GetContinuityTables(projectId);
            if (tables.Count == 0) return null;

            var parts = new List<string>();
            foreach (var type in ContinuityTableTypes.All)
            {
                var blocks = tables
                    .Where(t => string.Equals(t.TableType, type, StringComparison.OrdinalIgnoreCase))
                    .Select(t => string.IsNullOrWhiteSpace(t.ContentText)
                        ? ContinuityTableRenderer.Render(t.TableType, t.ContentJson)
                        : t.ContentText)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();
                if (blocks.Count == 0) continue;
                parts.Add("◆ " + ContinuityTableTypes.DisplayName(type) + "\n" + string.Join("\n", blocks));
            }

            return parts.Count == 0 ? null : string.Join("\n\n", parts);
        }
        catch (SqlException ex) when (ex.Number is 208 or 2812)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[连续性表] 拼装注入文本失败，项目 {ProjectId}", projectId);
            return null;
        }
    }

    /// <summary>把一次抽取结果拆成六类落库行（ContentJson + 渲染后的 ContentText）。</summary>
    private static List<ProjectContinuityTable> BuildRows(int projectId, ContinuityExtractionResult result)
    {
        var rows = new List<ProjectContinuityTable>();

        void Add(string tableType, object payload, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            rows.Add(new ProjectContinuityTable
            {
                ProjectId = projectId,
                EpisodeNumber = 0,
                TableType = tableType,
                ContentJson = JsonSerializer.Serialize(payload),
                ContentText = text,
                Source = "llm"
            });
        }

        Add(ContinuityTableTypes.SceneSpace, result.SceneSpace, ContinuityTableRenderer.RenderSceneSpace(result.SceneSpace));
        Add(ContinuityTableTypes.CharacterContinuity, result.CharacterContinuity, ContinuityTableRenderer.RenderCharacterContinuity(result.CharacterContinuity));
        Add(ContinuityTableTypes.PropState, result.PropState, ContinuityTableRenderer.RenderPropState(result.PropState));
        Add(ContinuityTableTypes.ClueReveal, result.ClueReveal, ContinuityTableRenderer.RenderClueReveal(result.ClueReveal));
        Add(ContinuityTableTypes.ActionCausality, result.ActionCausality, ContinuityTableRenderer.RenderActionCausality(result.ActionCausality));
        Add(ContinuityTableTypes.TransitionMotive, result.TransitionMotive, ContinuityTableRenderer.RenderTransitionMotive(result.TransitionMotive));

        return rows;
    }
}
