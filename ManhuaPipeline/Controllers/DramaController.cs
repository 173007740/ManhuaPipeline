using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DramaController : ControllerBase
{
    private readonly DbService _db;
    private readonly DirectorAgentService _agent;
    private readonly ILogger<DramaController> _logger;
    public DramaController(DbService db, DirectorAgentService agent, ILogger<DramaController> logger)
    { _db = db; _agent = agent; _logger = logger; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    [HttpGet]
    public IActionResult GetAll()
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetDramas(uid));
    }

    [HttpGet("{id}")]
    public IActionResult Get(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var d = _db.GetDrama(id, uid);
        if (d == null) return NotFound();
        return Ok(d);
    }

    [HttpPost]
    public IActionResult Create([FromBody] CreateDramaRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Title)) return BadRequest(new { message = "请输入漫剧名称" });
        var id = _db.CreateDrama(uid, req.Title, req.Description);
        // 整部剧本素材：建漫剧时交的这份，后面每集打开流水线立项那格时会预填进去
        if (!string.IsNullOrWhiteSpace(req.ScriptContent))
            _db.SetDramaScriptContent(id, req.ScriptContent!.Trim());
        return Ok(new { dramaId = id, message = "创建成功" });
    }

    [HttpPut("{id}")]
    public IActionResult Update(int id, [FromBody] CreateDramaRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.UpdateDrama(id, uid, req.Title, req.Description, null, req.ScriptContent))
            return NotFound(new { message = "漫剧不存在" });
        return Ok(new { message = "更新成功" });
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var drama = _db.GetDrama(id, uid);
        if (drama == null) return NotFound(new { message = "漫剧不存在" });
        var projects = _db.GetProjectsByDrama(id, uid);
        if (!_db.DeleteDrama(id, uid)) return NotFound(new { message = "漫剧不存在" });
        try
        {
            UploadStorage.DeleteUploadFile(drama.CoverImage, "/uploads/dramas/");
            foreach (var p in projects)
                UploadStorage.DeleteUploadFile(p.CoverImage, "/uploads/projects/");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cover files for drama {DramaId}", id);
        }
        return Ok(new { message = "删除成功" });
    }

    [HttpGet("{id}/projects")]
    public IActionResult GetProjects(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetProjectsByDrama(id, uid));
    }

    [HttpPost("{id}/cover")]
    [RequestSizeLimit(UploadValidation.MaxProfileImageBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadValidation.MaxProfileImageBytes)]
    public async Task<IActionResult> UploadCover(int id, IFormFile file)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var drama = _db.GetDrama(id, uid);
        if (drama == null) return NotFound(new { message = "漫剧不存在" });
        if (!UploadValidation.TryValidateImage(file, UploadValidation.MaxProfileImageBytes, out var extension, out var error))
            return BadRequest(new { message = error });
        var fileName = $"drama_{id}_{DateTime.Now.Ticks}{extension}";
        var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "dramas");
        Directory.CreateDirectory(uploadDir);
        var filePath = Path.Combine(uploadDir, fileName);
        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }
        var coverUrl = $"/uploads/dramas/{fileName}";
        _db.UpdateDramaCover(id, uid, coverUrl);
        if (!string.Equals(drama.CoverImage, coverUrl, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                UploadStorage.DeleteUploadFile(drama.CoverImage, "/uploads/dramas/");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete old cover for drama {DramaId}", id);
            }
        }
        return Ok(new { coverUrl = coverUrl, message = "上传成功" });
    }

    // ============================================================
    // 分集提纲：立项（P0）产出 → 单集项目
    //
    // 立项是漫剧级的：跑 P0 时一个项目都还没建，产出里那张「N 集分集卡点」表
    // 解析出来存进 Dramas，再按它长出 N 个项目（一集一个）。
    // 三步都能单独重来：跑立项、解析分集表、建项目。
    // ============================================================

    /// <summary>取分集提纲。没跑过立项就是空数组。</summary>
    [HttpGet("{id}/episodes")]
    public IActionResult GetEpisodes(int id)
    {
        if (GetUserId() == 0) return Unauthorized();
        return Ok(new { items = _db.GetEpisodeOutline(id) });
    }

    /// <summary>
    /// 从最近一次 P0 产出里解析分集提纲并存库，但不建项目——
    /// 模型给的表对不对，让人先看一眼再决定建不建。
    /// </summary>
    [HttpPost("{id}/episodes/parse")]
    public IActionResult ParseEpisodes(int id)
    {
        if (GetUserId() == 0) return Unauthorized();

        var step = _db.GetLatestP0Step(id);
        if (step == null || string.IsNullOrWhiteSpace(step.OutputText))
            return BadRequest(new { message = "这漫剧还没跑过立项（P0），先跑一次再来解析" });

        // 立项产出归漫剧：解析的同一份原文也写进 Dramas，每一集按 dramaId 就能读到它
        _db.SaveDramaP0Result(id, step.StepId, step.OutputText);

        var list = _db.ClampEpisodeOutline(id, EpisodeOutlineParser.Parse(step.OutputText));

        // 产出里没写分集表：单集剧补一条「第1集」占位；多集剧不猜，照实报错让人去看产出
        if (list.Count == 0) list = _db.SingleEpisodeFallback(id);

        if (list.Count == 0)
            return BadRequest(new { message = "这次 P0 产出里没找到分集表。重跑一次立项，或在项目里手工建" });

        _db.SaveEpisodeOutline(id, list);
        return Ok(new { items = list, count = list.Count });
    }

    /// <summary>按已存的分集提纲建（或更新）单集项目。重复调用不会长出重复项目。</summary>
    [HttpPost("{id}/episodes/build")]
    public IActionResult BuildEpisodes(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();

        var list = _db.GetEpisodeOutline(id);
        if (list.Count == 0)
            return BadRequest(new { message = "还没有分集提纲：先跑立项，或点「解析分集表」" });

        // 按立项里填的总集数截一刀：填 1 集就只该有 1 集
        list = _db.ClampEpisodeOutline(id, list);

        // 内容类型按立项那份落成：立项里改了「广告」，新生成的每一集就是广告项目
        var (created, updated) = _db.BuildEpisodeProjects(uid, id, list, _db.GetContentType(id));
        return Ok(new { created, updated, total = list.Count });
    }

    /// <summary>
    /// 跑一次立项（P0），产出解析成分集提纲后直接建出单集项目。
    /// 后台跑、前端拿 runId 轮询：一次立项几分钟，不能让 HTTP 请求干等到超时。
    /// </summary>
    [HttpPost("{id}/episodes/generate")]
    public IActionResult GenerateEpisodes(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();

        var drama = _db.GetDrama(id, uid);
        if (drama == null) return NotFound(new { message = "漫剧不存在" });

        var pack = _db.GetSkillPacks().FirstOrDefault();
        if (pack.PackId == 0) return BadRequest(new { message = "没有可用的规则包，先去技能库导入" });

        var running = _db.FindUnfinishedSkillRun(pack.PackId, null, id);
        if (running != null)
            return Conflict(new { message = $"这漫剧还有一次立项没跑完（#{running}），先处理完再重跑", runId = running });

        var input = _db.BuildP0InputText(id);
        if (input.Trim().Length == 0)
            return BadRequest(new { message = "立项还没填：画幅、题材、集数这些一项都没有，先填立项再跑" });

        var runId = _db.CreateSkillRun(pack.PackId, null, null, "立项 · " + drama.Title, null, id);
        _db.UpdateSkillRun(runId, "P0", "running");

        _ = Task.Run(async () =>
        {
            try
            {
                var r = await _agent.RunStepAsync(uid, runId, "P0", input, null, null);

                /* 产出先落到漫剧那一行，再谈解析。
                   立项整部漫剧一份：这一份落住了，12 集每一集打开都能看到它，
                   不用去翻「哪一集跑了 P0」那条运行史。
                   就算下面这张分集表没解析出来，产出本身也已经在库里，人能看见。 */
                _db.SaveDramaP0Result(id, r.StepId, r.Output);

                var list = EpisodeOutlineParser.Parse(r.Output);

                // 按立项里填的总集数截断：这份产出万一写了更多集，也不该越过人填的那个数
                list = _db.ClampEpisodeOutline(id, list);

                /* 产出里没写分集表（常见于单集剧：三幕骨架写得很全，就是没有那张 N 集表）时，
                   给总集数 = 1 的补一条「第1集」占位 —— 不然人跑完一圈一个集也没长出来，
                   只能回头猜哪里坏了。多集剧不补：凭空造 N 个空壳项目比没有更糟。 */
                if (list.Count == 0) list = _db.SingleEpisodeFallback(id);

                if (list.Count == 0)
                {
                    /* 产出对人是完整的，只是没按表格写——原文已经存在步骤记录里让人能去看。
                       这里停成 error，但不停在 running，免得页面一直转圈等一个不会来的结果。 */
                    _db.UpdateSkillRun(runId, "P0", "error");
                    _logger.LogWarning("P0 产出里没有分集表，且不是单集剧 drama={DramaId} run={RunId}", id, runId);
                    return;
                }

                _db.SaveEpisodeOutline(id, list);
                _db.BuildEpisodeProjects(uid, id, list, _db.GetContentType(id));
                _db.UpdateSkillRun(runId, "P0", "done");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "立项生成分集失败 drama={DramaId} run={RunId}", id, runId);
                _db.UpdateSkillRun(runId, "P0", "error");
            }
        });

        return Ok(new { runId, started = true });
    }

}

public class CreateDramaRequest
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>整部剧的剧本素材（选填）。每集的剧本存在各自项目上，这里是它的来源。</summary>
    public string? ScriptContent { get; set; }
}
