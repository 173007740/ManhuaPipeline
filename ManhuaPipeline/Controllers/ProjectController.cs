using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProjectController : ControllerBase
{
    private readonly DbService _db;
    private readonly PostOverlayPlanService _overlay;
    private readonly ILogger<ProjectController> _logger;
    public ProjectController(DbService db, PostOverlayPlanService overlay, ILogger<ProjectController> logger)
    {
        _db = db;
        _overlay = overlay;
        _logger = logger;
    }

    private int GetUserId()
    {
        return HttpContext.Session.GetInt32("UserId") ?? 0;
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetProjects(uid));
    }

    [HttpGet("duration-stats")]
    public IActionResult DurationStats([FromQuery] int? dramaId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetProjectDurationStats(dramaId, uid));
    }


    [HttpGet("{id}")]
    public IActionResult Get(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var p = _db.GetProject(id, uid);
        if (p == null) return NotFound();
        return Ok(p);
    }

    [HttpPost]
    public IActionResult Create([FromBody] CreateProjectRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Title)) return BadRequest(new { message = "请输入项目名称" });
        if (req.DramaId <= 0) return BadRequest(new { message = "请选择所属漫剧" });
        if (_db.GetDrama(req.DramaId, uid) == null) return NotFound(new { message = "漫剧不存在" });
        var id = _db.CreateProject(uid, req.DramaId, req.Title, req.Description, req.ScriptContent, req.ProjectType);
        return Ok(new { projectId = id, message = "创建成功" });
    }

    /// <summary>切换项目内容类型（短剧 / 广告 / MV）。已有内容不重算，仅影响后续生成的分镜与提示词规则。</summary>
    [HttpPut("{id}/project-type")]
    public IActionResult UpdateProjectType(int id, [FromBody] UpdateProjectTypeRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(id, uid)) return NotFound(new { message = "项目不存在" });
        var pt = DbService.NormalizeProjectType(req.ProjectType);
        _db.UpdateProjectType(id, pt);
        return Ok(new { projectId = id, projectType = pt, message = "项目类型已更新" });
    }

    // ============ MV 歌词轨 ============

    /// <summary>读取项目歌词轨（MV 对口型用）。</summary>
    [HttpGet("{id}/lyrics")]
    public IActionResult GetLyrics(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(id, uid)) return NotFound(new { message = "项目不存在" });
        return Ok(_db.GetLyricLines(id));
    }

    /// <summary>整轨覆盖保存歌词。支持两种输入：Lrc 文本（推荐，时间戳来自真歌）
    /// 或 Lines 数组（手动填写 startSec/endSec/text）。两者都给时以 Lrc 为准。</summary>
    [HttpPut("{id}/lyrics")]
    public IActionResult UpdateLyrics(int id, [FromBody] UpdateLyricsRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(id, uid)) return NotFound(new { message = "项目不存在" });

        List<ProjectLyricLine> lines;
        if (!string.IsNullOrWhiteSpace(req.Lrc))
            lines = ParseLrc(req.Lrc!);
        else
            lines = (req.Lines ?? new List<LyricLineDto>())
                .Select(x => new ProjectLyricLine { StartSec = x.StartSec, EndSec = x.EndSec, Text = x.Text })
                .ToList();

        // LRC 无时间戳的行会被整行丢弃；静默存成 0 条会让用户以为保存成功、实际时间轨是空的。
        var hadInput = !string.IsNullOrWhiteSpace(req.Lrc) || (req.Lines?.Count ?? 0) > 0;
        if (hadInput && lines.Count == 0)
            return BadRequest(new { message = "没识别到歌词行：LRC 每行都需要 [mm:ss.xx] 时间戳，例如 [00:12.34]歌词文本。只想清空歌词轨时请把内容删空后再保存。" });

        _db.ReplaceLyricLines(id, lines);
        return Ok(new { count = lines.Count, message = "歌词轨已保存" });
    }

    /// <summary>解析 LRC：形如 [00:12.34]歌词文本。
    /// 每行支持多个时间戳；未给出结束时间的，以下一句起始为结束，末句默认 5 秒。</summary>
    private static List<ProjectLyricLine> ParseLrc(string lrc)
    {
        var result = new List<ProjectLyricLine>();
        var rx = new System.Text.RegularExpressions.Regex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]");
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var ms = rx.Matches(line);
            if (ms.Count == 0) continue;
            var text = rx.Replace(line, "").Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (System.Text.RegularExpressions.Match m in ms)
            {
                if (!decimal.TryParse(m.Groups[1].Value, out var mm)) continue;
                var secPart = m.Groups[2].Value.Replace(':', '.');
                if (!decimal.TryParse(secPart, System.Globalization.CultureInfo.InvariantCulture, out var ss)) continue;
                result.Add(new ProjectLyricLine { StartSec = mm * 60 + ss, EndSec = mm * 60 + ss, Text = text });
            }
        }

        result.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        for (var i = 0; i < result.Count; i++)
        {
            var next = i + 1 < result.Count ? result[i + 1].StartSec : result[i].StartSec + 5m;
            result[i].EndSec = next > result[i].StartSec ? next : result[i].StartSec + 5m;
        }
        return result;
    }

    [HttpPut("{id}/script")]
    public IActionResult UpdateScript(int id, [FromBody] UpdateScriptRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(id, uid)) return NotFound(new { message = "项目不存在" });
        _db.UpdateProjectScript(id, uid, req.ScriptContent);
        return Ok(new { message = "剧本保存成功" });
    }

    [HttpPut("{id}/episode-count")]
    public IActionResult UpdateEpisodeCount(int id, [FromBody] UpdateEpisodeCountRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(id, uid)) return NotFound(new { message = "项目不存在" });
        if (req.Count < 1 || req.Count > 100) return BadRequest(new { message = "分集数量必须在 1–100 之间" });
        _db.UpdateEpisodeCount(id, uid, req.Count);
        return Ok(new { message = "分集数量已更新" });
    }

    /// <summary>
    /// L2 连续性层（六类表）+ 单元资产绑定的只读汇总，供诊断页面使用。
    /// 说明：连续性表是阶段 4 的强输入，若抽取质量差会静默传播到阶段 5 / 9，
    /// 因此这里只提供只读视图用于归因，不提供写入——抽取是唯一产出路径（人工编辑暂不开放）。
    /// </summary>
    [HttpGet("{id}/continuity")]
    public IActionResult GetContinuity(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(id, uid)) return NotFound(new { message = "项目不存在" });

        var tables = _db.GetContinuityTables(id);
        var bindings = _db.GetUnitAssetBindings(id);

        return Ok(new
        {
            projectId = id,
            continuity = tables.Select(t => new
            {
                t.TableType,
                TypeName = t.TypeName,
                t.EpisodeNumber,
                t.Source,
                t.UpdatedAt,
                HasContent = !string.IsNullOrWhiteSpace(t.ContentText),
                t.ContentText
            }),
            unitBindings = bindings.Select(b => new
            {
                b.EpisodeNumber,
                b.UnitNumber,
                b.Category,
                b.AssetId,
                b.Name,
                b.HasImage,
                b.SortOrder
            }),
            summary = new
            {
                tableCount = tables.Count,
                filledTableCount = tables.Count(t => !string.IsNullOrWhiteSpace(t.ContentText)),
                bindingCount = bindings.Count,
                unitCount = bindings.Select(b => b.EpisodeNumber + "|" + b.UnitNumber).Distinct().Count()
            }
        });
    }

    /// <summary>
    /// L5 后期叠加清单：按镜头列出必须「画面留白 + 后期叠加」的文字类元素（短信/新闻/文件/告示/地图/屏幕界面等）。
    /// 这些元素交给视频模型生成必然乱码，因此阶段 9 只画洁净留白，文字由后期叠加；本接口是给后期的人工施工单。
    /// 只读派生，不落库：清单内容始终与当前提示词一致。
    /// </summary>
    [HttpGet("{id}/post-overlay")]
    public IActionResult GetPostOverlay(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(id, uid)) return NotFound(new { message = "项目不存在" });

        var plan = _overlay.Build(id);
        return Ok(new
        {
            projectId = id,
            ruleText = plan.RuleText,
            shotCount = plan.ShotCount,
            itemCount = plan.Items.Count,
            unguardedCount = plan.UnguardedCount,
            categoryCounts = plan.CategoryCounts.Select(kv => new { category = kv.Key, count = kv.Value }),
            items = plan.Items.Select(i => new
            {
                i.EpisodeNumber,
                i.UnitName,
                i.ShotLabel,
                i.Categories,
                i.Cues,
                i.Excerpt,
                i.Guarded
            })
        });
    }

    /// <summary>
    /// L3 关键帧层只读视图：每个剧情节点一张关键帧（8-16 张/集），含构图与锁定项。
    /// 关键帧是「分镜 → 视频生成」之间的锚点：定死角色站位、道具状态、场景朝向、线索可见性与镜头衔接。
    /// 生成入口在 StageController（POST api/project/{id}/stage/keyframes/generate），本接口只读。
    /// </summary>
    [HttpGet("{id}/keyframes")]
    public IActionResult GetKeyframes(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(id, uid)) return NotFound(new { message = "项目不存在" });

        var tableExists = _db.KeyframeTableExists();
        var rows = _db.GetKeyframes(id);
        return Ok(new
        {
            projectId = id,
            tableExists,
            count = rows.Count,
            episodeCounts = rows.GroupBy(r => r.EpisodeNumber)
                .OrderBy(g => g.Key)
                .Select(g => new { episodeNumber = g.Key, count = g.Count(), llmCount = g.Count(x => x.Source == "llm") }),
            keyframes = rows.Select(r => new
            {
                r.KeyframeId,
                r.EpisodeNumber,
                r.SortOrder,
                r.NodeLabel,
                r.NodeReason,
                r.ShotLabel,
                r.UnitNumber,
                r.Composition,
                r.LockedCharacters,
                r.LockedProps,
                r.LockedSceneDirection,
                r.ClueVisible,
                r.NextConnection,
                r.ImagePrompt,
                r.Status,
                r.Source,
                r.UpdatedAt
            })
        });
    }

    /// <summary>读取项目目标总时长：显式设置优先，其次剧本头部"建议时长"自动识别。</summary>
    [HttpGet("{id}/target-duration")]
    public IActionResult GetTargetDuration(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var p = _db.GetProject(id, uid);
        if (p == null) return NotFound(new { message = "项目不存在" });

        var explicitText = string.IsNullOrWhiteSpace(p.TargetDurationText) ? null : p.TargetDurationText.Trim();
        var budget = DurationBudgetParser.TryParse(explicitText);
        var source = "explicit";
        if (budget == null)
        {
            budget = DurationBudgetParser.TryParse(p.ScriptContent);
            source = budget == null ? "none" : "script";
        }
        return Ok(new
        {
            targetDurationText = explicitText,
            source = source,
            budget = budget == null
                ? null
                : new { minSeconds = budget.Value.MinSeconds, maxSeconds = budget.Value.MaxSeconds, display = budget.Value.Display }
        });
    }

    /// <summary>保存（或清除）项目目标总时长。格式示例：3分05秒—3分20秒 / 185-200秒 / 3分钟。空值=清除。</summary>
    [HttpPut("{id}/target-duration")]
    public IActionResult UpdateTargetDuration(int id, [FromBody] UpdateTargetDurationRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(id, uid)) return NotFound(new { message = "项目不存在" });

        var text = string.IsNullOrWhiteSpace(req.TargetDuration) ? null : req.TargetDuration.Trim();
        if (text != null && DurationBudgetParser.TryParse(text) == null)
            return BadRequest(new { message = "时长格式无法识别，示例：3分05秒—3分20秒 / 185-200秒 / 3分钟" });

        _db.UpdateProjectTargetDuration(id, uid, text);
        return Ok(new { message = text == null ? "已清除时长预算设置" : "目标总时长已保存" });
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var project = _db.GetProject(id, uid);
        if (project == null) return NotFound(new { message = "项目不存在" });
        if (!_db.DeleteProject(id, uid)) return NotFound(new { message = "项目不存在" });
        try
        {
            UploadStorage.DeleteUploadFile(project.CoverImage, "/uploads/projects/");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cover for project {ProjectId}", id);
        }
        return Ok(new { message = "项目删除成功" });
    }

    [HttpPost("{id}/cover-upload")]
    [RequestSizeLimit(UploadValidation.MaxProfileImageBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadValidation.MaxProfileImageBytes)]
    public async Task<IActionResult> UploadCoverFile(int id, IFormFile file)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var project = _db.GetProject(id, uid);
        if (project == null) return NotFound(new { message = "项目不存在" });
        if (!UploadValidation.TryValidateImage(file, UploadValidation.MaxProfileImageBytes, out var extension, out var error))
            return BadRequest(new { message = error });
        var fileName = $"project_{id}_{DateTime.Now.Ticks}{extension}";
        var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "projects");
        Directory.CreateDirectory(uploadDir);
        var filePath = Path.Combine(uploadDir, fileName);
        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }
        var coverUrl = $"/uploads/projects/{fileName}";
        _db.UpdateProjectCoverImage(id, uid, coverUrl);
        if (!string.Equals(project.CoverImage, coverUrl, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                UploadStorage.DeleteUploadFile(project.CoverImage, "/uploads/projects/");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete old cover for project {ProjectId}", id);
            }
        }
        return Ok(new { coverUrl = coverUrl, message = "上传成功" });
    }

    [HttpPut("{id}/cover")]
    public IActionResult UpdateCover(int id, [FromBody] UpdateCoverRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var project = _db.GetProject(id, uid);
        if (project == null) return NotFound(new { message = "项目不存在" });
        _db.UpdateProjectCover(id, uid, req.CoverImage, req.Title, req.Description);
        if (!string.Equals(project.CoverImage, req.CoverImage, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                UploadStorage.DeleteUploadFile(project.CoverImage, "/uploads/projects/");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete old cover for project {ProjectId}", id);
            }
        }
        return Ok(new { message = "项目信息更新成功" });
    }

    [HttpPut("{id}/style")]
    public IActionResult UpdateStyle(int id, [FromBody] UpdateProjectStyleRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var p = _db.GetProject(id, uid);
        if (p == null) return NotFound();
        _db.UpdateProjectStyle(id, req.StyleId);
        return Ok(new { message = "已更新" });
    }

    [HttpPut("{id}/video-settings")]
    public IActionResult UpdateVideoSettings(int id, [FromBody] UpdateProjectVideoSettingsRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var p = _db.GetProject(id, uid);
        if (p == null) return NotFound();
        _db.UpdateProjectVideoSettings(id, req.Ratio ?? "16:9", req.Watermark, req.Audio, req.Resolution ?? "720p", req.Megapixels);
        return Ok(new { message = "已保存" });
    }

    [HttpPut("{id}/tags")]
    public IActionResult UpdateTags(int id, [FromBody] UpdateProjectTagsRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var p = _db.GetProject(id, uid);
        if (p == null) return NotFound();
        _db.UpdateProjectTags(id, req.Tags);
        return Ok(new { message = "已保存" });
    }

    /// <summary>
    /// 保存项目的「资产库类型」：资产图同步进参考图库时写进图库的「类型」大类（如 动漫/写实/游戏/仙侠）。
    /// 传空 = 清除，图库类型回退成资产分类（角色/道具/环境/特效）。
    /// </summary>
    [HttpPut("{id}/library-category")]
    public IActionResult UpdateLibraryCategory(int id, [FromBody] UpdateProjectLibraryCategoryRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(id, uid)) return NotFound(new { message = "项目不存在" });
        var value = string.IsNullOrWhiteSpace(req?.LibraryCategory) ? null : req!.LibraryCategory!.Trim();
        if (value != null && value.Length > 50) return BadRequest(new { message = "类型名称最多 50 个字" });
        _db.UpdateProjectLibraryCategory(id, uid, value);
        return Ok(new { message = value == null ? "已清除资产库类型" : "资产库类型已保存" });
    }

    [HttpPut("{id}/continue")]
    public IActionResult ContinueScript(int id, [FromBody] ContinueScriptRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var proj = _db.GetProject(id, uid);
        if (proj == null) return NotFound(new { message = "项目不存在" });
        if (string.IsNullOrWhiteSpace(req.NewContent))
            return BadRequest(new { message = "续写内容不能为空" });

        var sep = "\n\n---\n\n### 以下为续写内容\n";
        var newScript = (proj.ScriptContent ?? "") + sep + req.NewContent.Trim();
        _db.UpdateProjectScript(id, uid, newScript);
        var newBatch = proj.CurrentBatch + 1;
        _db.UpdateProjectBatch(id, newBatch);
        _db.ResetStageForContinue(id, new[] { 10 });
        return Ok(new { message = "续写内容已保存，当前批次：" + newBatch });
    }
}

public class CreateProjectRequest
{
    public int DramaId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? ScriptContent { get; set; }
    /// <summary>项目内容类型：drama（默认）/ ad / mv。</summary>
    public string? ProjectType { get; set; }
}

public class UpdateProjectTypeRequest
{
    public string ProjectType { get; set; } = "drama";
}

public class UpdateLyricsRequest
{
    /// <summary>LRC 原文，含 [mm:ss.xx] 时间戳。与 Lines 二选一，优先使用。</summary>
    public string? Lrc { get; set; }
    public List<LyricLineDto>? Lines { get; set; }
}

public class LyricLineDto
{
    public decimal StartSec { get; set; }
    public decimal EndSec { get; set; }
    public string Text { get; set; } = "";
}

public class UpdateEpisodeCountRequest
{
    public int Count { get; set; } = 1;
}
public class UpdateTargetDurationRequest
{
    public string? TargetDuration { get; set; }
}
public class ContinueScriptRequest
{
    public string NewContent { get; set; } = "";
}

public class UpdateCoverRequest
{
    public string? CoverImage { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
}
public class UpdateScriptRequest
{
    public string ScriptContent { get; set; } = "";
}

public class UpdateProjectStyleRequest
{
    public int? StyleId { get; set; }
}

public class UpdateProjectTagsRequest
{
    public string? Tags { get; set; }
}

public class UpdateProjectLibraryCategoryRequest
{
    public string? LibraryCategory { get; set; }
}

public class UpdateProjectVideoSettingsRequest
{
    public string? Ratio { get; set; }
    public bool Watermark { get; set; }
    public bool Audio { get; set; } = true;
    public string? Resolution { get; set; }
    /// <summary>出图像素档（百万像素），ComfyUI 引擎专用；火山方舟看 Resolution。页面传 0.5/1/1.5/2。</summary>
    public double Megapixels { get; set; } = 1;
}

