using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/project/{projectId}/asset")]
public class AssetController : ControllerBase
{
    private readonly DbService _db;
    private readonly ImageService _image;
    private readonly LLMService _llm;
    private readonly ILogger<AssetController> _logger;

    public AssetController(DbService db, ImageService image, LLMService llm, ILogger<AssetController> logger)
    {
        _db = db;
        _image = image;
        _llm = llm;
        _logger = logger;
    }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    private IActionResult? CheckProjectAccess(int projectId)
    {
        var userId = GetUserId();
        if (userId == 0) return Unauthorized();
        return _db.ProjectBelongsToUser(projectId, userId)
            ? null
            : NotFound(new { message = "项目不存在" });
    }

    [HttpGet("characters")]
    public IActionResult GetCharacters(int projectId)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        return Ok(_db.GetCharacterAssets(projectId));
    }

    [HttpGet("props")]
    public IActionResult GetProps(int projectId)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        return Ok(_db.GetPropAssets(projectId));
    }

    [HttpGet("environments")]
    public IActionResult GetEnvironments(int projectId)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        return Ok(_db.GetEnvAssets(projectId));
    }

    [HttpGet("effects")]
    public IActionResult GetEffects(int projectId)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        return Ok(_db.GetEffectAssets(projectId));
    }

    [HttpPut("characters/{assetId}")]
    public IActionResult UpdateCharacter(int projectId, int assetId, [FromBody] UpdateCharacterAssetRequest req)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "名称不能为空" });
        _db.UpdateCharacterAsset(projectId, assetId, req.Name.Trim(), req.Description, req.ImageUrl, req.Attributes);
        if (req.ImagePrompt != null || req.NegativePrompt != null)
            _db.SaveAssetPrompt(projectId, "characters", assetId, req.ImagePrompt, req.NegativePrompt);
        return Ok(new { success = true });
    }

    [HttpPut("props/{assetId}")]
    public IActionResult UpdateProp(int projectId, int assetId, [FromBody] UpdatePropAssetRequest req)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "名称不能为空" });
        _db.UpdatePropAsset(projectId, assetId, req.Name.Trim(), req.Description, req.ImageUrl);
        if (req.ImagePrompt != null || req.NegativePrompt != null)
            _db.SaveAssetPrompt(projectId, "props", assetId, req.ImagePrompt, req.NegativePrompt);
        return Ok(new { success = true });
    }

    [HttpPut("environments/{assetId}")]
    public IActionResult UpdateEnvironment(int projectId, int assetId, [FromBody] UpdateEnvAssetRequest req)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "名称不能为空" });
        _db.UpdateEnvAsset(projectId, assetId, req.Name.Trim(), req.Description, req.ImageUrl);
        if (req.ImagePrompt != null || req.NegativePrompt != null)
            _db.SaveAssetPrompt(projectId, "environments", assetId, req.ImagePrompt, req.NegativePrompt);
        return Ok(new { success = true });
    }

    [HttpPut("effects/{assetId}")]
    public IActionResult UpdateEffect(int projectId, int assetId, [FromBody] UpdateEnvAssetRequest req)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "名称不能为空" });
        _db.UpdateEffectAsset(projectId, assetId, req.Name.Trim(), req.Description, req.ImageUrl);
        if (req.ImagePrompt != null || req.NegativePrompt != null)
            _db.SaveAssetPrompt(projectId, "effects", assetId, req.ImagePrompt, req.NegativePrompt);
        return Ok(new { success = true });
    }

    /// <summary>
    /// 给资产卡出图（文生图），结果回填该资产的 ImageUrl，并同步一份进用户级参考图库。
    /// 走 LLMConfigs 中 Provider='image' 配置的中转接口，同步等待（单张图通常几十秒）。
    /// </summary>
    [HttpPost("{category}/{assetId}/image")]
    public async Task<IActionResult> GenerateAssetImage(
        int projectId, string category, int assetId,
        [FromBody] GenerateAssetImageRequest? req, CancellationToken ct)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        var uid = GetUserId();
        category = (category ?? "").Trim().ToLowerInvariant();

        var asset = ResolveAssetForImage(projectId, category, assetId);
        if (asset == null) return NotFound(new { message = "资产不存在" });

        var config = _db.GetActiveConfig(uid, "image");
        if (config == null || string.IsNullOrWhiteSpace(config.ApiKey))
            return BadRequest(new { message = "尚未配置文生图：请到「API 配置 → 文生图（中转）」填写接口地址、API Key 与模型名称" });
        var model = (config.ModelName ?? "").Trim();
        if (model.Length == 0)
            return BadRequest(new { message = "文生图模型名称为空：请到「API 配置 → 文生图（中转）」填写模型名称（如 gpt-image-1 / seedream-3.0 / gemini-2.5-flash-image）" });

        var size = ImageService.IsValidSize(req?.Size) ? req!.Size!.Trim() : ImageService.DefaultSizeFor(category);
        // 出图提示词：优先用「提取时按模版生成」的正文（改模版风格/负面词对已有资产立即生效），
        // 老资产还没有提示词时才退回旧的「名称+描述+属性」拼法。
        // 模版按「本剧 → 账号默认 → 出厂默认」取，所以每部剧的风格互不影响
        var tpl = _db.GetAssetPromptTemplate(uid, projectId, category);
        var projectStyle = GetProjectStylePrompt(projectId);
        // 出图时允许「临时覆盖」：提示词面板里改过但没保存的正文、或让大模型按临时要求改写过的正文，
        // 通过 PromptOverride 传进来只作用于本次出图，不写回资产卡（临时换装、临时改氛围都走这条路）。
        var bodyPrompt = string.IsNullOrWhiteSpace(req?.PromptOverride)
            ? asset.Value.ImagePrompt
            : req!.PromptOverride!.Trim();
        var assetNegative = string.IsNullOrWhiteSpace(req?.NegativeOverride)
            ? asset.Value.NegativePrompt
            : req.NegativeOverride.Trim();
        var negative = string.IsNullOrWhiteSpace(assetNegative)
            ? (tpl.Enabled ? tpl.NegativePrompt : null)
            : assetNegative;
        string prompt;
        if (!string.IsNullOrWhiteSpace(bodyPrompt))
        {
            prompt = ImageService.ComposeAssetImagePrompt(
                bodyPrompt,
                tpl.Enabled ? tpl.StyleLock : null,
                negative, projectStyle, req?.ExtraPrompt);
        }
        else
        {
            prompt = ImageService.BuildAssetImagePrompt(
                category, asset.Value.Name, asset.Value.Description, asset.Value.Attributes,
                projectStyle, req?.ExtraPrompt);
            if (!string.IsNullOrWhiteSpace(negative))
                prompt += "负面提示词（画面中禁止出现）：" + negative.Trim().TrimEnd('。', '.') + "。";
        }

        _logger.LogInformation("[AssetImage] 开始出图 projectId={ProjectId} {Category}#{AssetId} size={Size} model={Model} 用资产提示词={UsePrompt} 临时覆盖={Overridden}",
            projectId, category, assetId, size, model, !string.IsNullOrWhiteSpace(bodyPrompt), !string.IsNullOrWhiteSpace(req?.PromptOverride));

        var result = await _image.GenerateAsync(prompt, config.ApiUrl, config.ApiKey, model, size, ct);
        if (!result.Ok || result.Data == null)
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "出图失败：" + (result.Error ?? "未知错误") });

        var (localPath, fileSize) = ImageService.SaveLocalImage(result.Data, result.Ext ?? ".png", asset.Value.Name, _logger);
        var libraryId = AssetImageSupport.SyncToReferenceLibrary(_db, _logger, uid, projectId, category, assetId, asset.Value.Name, localPath, fileSize);
        _db.UpdateAssetImage(projectId, category, assetId, localPath);

        _logger.LogInformation("[AssetImage] 完成 projectId={ProjectId} {Category}#{AssetId} → {Path} ({Size}B), libraryAssetId={LibId}",
            projectId, category, assetId, localPath, fileSize, libraryId);

        return Ok(new
        {
            success = true,
            imageUrl = localPath,
            libraryAssetId = libraryId,
            size,
            usedPrompt = prompt,
            usedAssetPrompt = !string.IsNullOrWhiteSpace(bodyPrompt),
            promptOverridden = !string.IsNullOrWhiteSpace(req?.PromptOverride),
            message = libraryId > 0
                ? "出图成功，已回填资产卡图片，并存入「我的资产」图库（可被「自动绑定参考图」按资产名命中）。"
                : "出图成功，已回填资产卡图片，但同步到「我的资产」图库失败（不影响资产卡使用）。"
        });
    }

    // ==================== 出图任务队列（后台出图：关页面/刷新也会跑完） ====================
    // 说明：上面那个同步接口保留作兼容/回退；页面默认走下面这套排队接口——
    // 入队后由 AssetImageQueueService 依次执行，与浏览器无关，关页面/刷新/换设备都会跑完。
    // 执行逻辑在 AssetImageRunner（与同步接口的提示词拼装规则保持一致，改规则时两边都要看）。

    /// <summary>入队一张资产出图（临时出图 PromptOverride 同样走这里，参数落库、后台照原样执行）。</summary>
    [HttpPost("{category}/{assetId}/image/queue")]
    public IActionResult QueueAssetImage(int projectId, string category, int assetId, [FromBody] GenerateAssetImageRequest? req)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        var uid = GetUserId();
        category = (category ?? "").Trim().ToLowerInvariant();
        if (DbService.AssetTableName(category) == null) return BadRequest(new { message = "未知资产类型: " + category });

        var asset = ResolveAssetForImage(projectId, category, assetId);
        if (asset == null) return NotFound(new { message = "资产不存在" });

        if (_db.HasActiveAssetImageTask(projectId, category, assetId))
            return Ok(new { queued = false, message = "这条资产已有出图任务在排队/执行中，不用重复提交。" });

        var taskId = _db.EnqueueAssetImageTask(new AssetImageTask
        {
            ProjectId = projectId,
            UserId = uid,
            Category = category,
            AssetId = assetId,
            AssetName = asset.Value.Name,
            PromptOverride = req?.PromptOverride,
            NegativeOverride = req?.NegativeOverride,
            ExtraPrompt = req?.ExtraPrompt,
            Size = req?.Size,
            SourceProjectId = req?.SourceProjectId,
            SourceAssetId = req?.SourceAssetId,
            SourceImageUrl = req?.SourceImageUrl,
            GarmentImageUrl = req?.GarmentImageUrl,
            SourceNote = req?.SourceNote
        });
        _logger.LogInformation("[AssetImage] 入队出图任务 {TaskId} projectId={ProjectId} {Category}#{AssetId}",
            taskId, projectId, category, assetId);
        return Ok(new
        {
            queued = true,
            taskId,
            message = "已加入出图队列（后台依次出图，关掉页面也会继续跑，回来刷新即可看到结果）。"
        });
    }

    /// <summary>批量入队：整个分类缺图（默认）或全部资产。</summary>
    [HttpPost("images/queue")]
    public IActionResult QueueAssetImages(int projectId, [FromBody] QueueAssetImagesRequest? req)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        var uid = GetUserId();
        var category = (req?.Category ?? "").Trim().ToLowerInvariant();
        if (DbService.AssetTableName(category) == null) return BadRequest(new { message = "未知资产类型: " + category });
        var all = string.Equals((req?.Scope ?? "missing").Trim(), "all", StringComparison.OrdinalIgnoreCase);

        var queued = 0;
        var skipped = 0;
        foreach (var a in ListAssetsForQueue(projectId, category))
        {
            if (!all && !string.IsNullOrWhiteSpace(a.ImageUrl)) continue;   // 缺图模式：已有图的跳过
            if (_db.HasActiveAssetImageTask(projectId, category, a.AssetId)) { skipped++; continue; }
            _db.EnqueueAssetImageTask(new AssetImageTask
            {
                ProjectId = projectId,
                UserId = uid,
                Category = category,
                AssetId = a.AssetId,
                AssetName = a.Name
            });
            queued++;
        }

        _logger.LogInformation("[AssetImage] 批量入队 projectId={ProjectId} {Category} scope={Scope} queued={Queued} skipped={Skipped}",
            projectId, category, all ? "all" : "missing", queued, skipped);

        var message = queued == 0
            ? (skipped > 0 ? $"没有新增任务：{skipped} 条已在队列中" : (all ? "这个分类下没有资产" : "这个分类下没有缺图的资产"))
            : $"已入队 {queued} 条出图任务{(skipped > 0 ? $"，{skipped} 条已在队列中跳过" : "")}。后台会依次出图，关掉页面也会继续跑，回来刷新即可看到结果。";
        return Ok(new { queued, skipped, message });
    }

    /// <summary>出图任务列表（前端轮询状态用，按 TaskId 倒序）。</summary>
    [HttpGet("image-tasks")]
    public IActionResult GetAssetImageTasks(int projectId, string? category = null, int take = 300)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        var cat = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();
        if (cat != null && DbService.AssetTableName(cat) == null) return BadRequest(new { message = "未知资产类型: " + cat });
        return Ok(_db.GetAssetImageTasks(projectId, cat, Math.Clamp(take, 1, 500)));
    }

    /// <summary>失败/已完成的出图任务重新排队。</summary>
    [HttpPost("image-tasks/{taskId}/retry")]
    public IActionResult RetryAssetImageTask(int projectId, int taskId)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        return _db.RetryAssetImageTask(projectId, taskId)
            ? Ok(new { message = "已重新排队，后台会接着出图。" })
            : BadRequest(new { message = "任务不存在，或状态不允许重试（只有失败/已完成的任务可重试）。" });
    }

    /// <summary>按类型列出资产的图片地址（批量出图判断"缺图"用）。</summary>
    private List<(int AssetId, string Name, string? ImageUrl)> ListAssetsForQueue(int projectId, string category)
    {
        var list = new List<(int, string, string?)>();
        switch (category)
        {
            case "characters":
                foreach (var a in _db.GetCharacterAssets(projectId)) list.Add((a.AssetId, a.Name, a.ImageUrl));
                break;
            case "props":
                foreach (var a in _db.GetPropAssets(projectId)) list.Add((a.AssetId, a.Name, a.ImageUrl));
                break;
            case "environments":
                foreach (var a in _db.GetEnvAssets(projectId)) list.Add((a.AssetId, a.Name, a.ImageUrl));
                break;
            case "effects":
                foreach (var a in _db.GetEffectAssets(projectId)) list.Add((a.AssetId, a.Name, a.ImageUrl));
                break;
        }
        return list;
    }

    /// <summary>
    /// 按当前模版重新生成该资产的「出图提示词」正文并入库。
    /// 用途：已经跑过 Stage 6/7/8/11 的老项目，不必重跑流水线就能补上提示词。
    /// </summary>
    [HttpPost("{category}/{assetId}/prompt")]
    public async Task<IActionResult> RebuildAssetPrompt(int projectId, string category, int assetId, CancellationToken ct)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        var uid = GetUserId();
        category = (category ?? "").Trim().ToLowerInvariant();
        if (DbService.AssetTableName(category) == null) return BadRequest(new { message = "未知资产类型: " + category });

        var asset = ResolveAssetForImage(projectId, category, assetId);
        if (asset == null) return NotFound(new { message = "资产不存在" });

        var cfg = GetTextLlmConfig();
        if (cfg == null)
            return BadRequest(new { message = "尚未配置文本大模型：请到「API 配置」填写 DeepSeek / Qwen / Chat-GPT 任意一个的 API Key" });

        // 模版按「本剧 → 账号默认 → 出厂默认」取，所以每部剧的风格互不影响
        var tpl = _db.GetAssetPromptTemplate(uid, projectId, category);
        var raw = await _llm.GenerateAssetImagePrompt(
            category, asset.Value.Name, asset.Value.Description, asset.Value.Attributes,
            tpl, cfg.Value.apiUrl, cfg.Value.apiKey, cfg.Value.model, cfg.Value.thinkingMode);

        var prompt = NormalizeGeneratedPrompt(raw);
        if (string.IsNullOrWhiteSpace(prompt))
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "大模型没有返回提示词，请稍后重试" });

        _db.SaveAssetImagePrompt(projectId, category, assetId, prompt);
        _logger.LogInformation("[AssetPrompt] 重新生成提示词 projectId={ProjectId} {Category}#{AssetId} 长度={Len}",
            projectId, category, assetId, prompt.Length);
        return Ok(new { success = true, imagePrompt = prompt, message = "已按模版重新生成提示词" });
    }

    /// <summary>
    /// 让大模型按「临时要求」改写该资产的出图提示词正文（例如临时换装、改姿态、改时间天气）。
    /// 默认只返回结果、不落库，配合出图接口的 PromptOverride 实现「出图前临时改」；
    /// 传 save=true 才写回资产卡。
    /// </summary>
    [HttpPost("{category}/{assetId}/prompt/rewrite")]
    public async Task<IActionResult> RewriteAssetPrompt(int projectId, string category, int assetId,
        [FromBody] RewriteAssetPromptRequest? req, CancellationToken ct)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        var uid = GetUserId();
        category = (category ?? "").Trim().ToLowerInvariant();
        if (DbService.AssetTableName(category) == null) return BadRequest(new { message = "未知资产类型: " + category });
        if (req == null || string.IsNullOrWhiteSpace(req.Instruction))
            return BadRequest(new { message = "请先填写「临时要求」，例如：换成蓝色校服、雨夜街道、戴斗笠" });

        var asset = ResolveAssetForImage(projectId, category, assetId);
        if (asset == null) return NotFound(new { message = "资产不存在" });

        var cfg = GetTextLlmConfig();
        if (cfg == null)
            return BadRequest(new { message = "尚未配置文本大模型：请到「API 配置」填写 DeepSeek / Qwen / Chat-GPT 任意一个的 API Key" });

        // 以「面板里的正文」为基准改写：用户可能已经先手工调过，别再退回库里的旧正文
        var tpl = _db.GetAssetPromptTemplate(uid, projectId, category);
        var basePrompt = string.IsNullOrWhiteSpace(req.BasePrompt) ? asset.Value.ImagePrompt : req.BasePrompt.Trim();
        var raw = await _llm.RewriteAssetImagePrompt(
            category, asset.Value.Name, asset.Value.Description, asset.Value.Attributes,
            basePrompt, req.Instruction.Trim(),
            tpl, cfg.Value.apiUrl, cfg.Value.apiKey, cfg.Value.model, cfg.Value.thinkingMode);

        var prompt = NormalizeGeneratedPrompt(raw);
        if (string.IsNullOrWhiteSpace(prompt))
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "大模型没有返回改写结果，请稍后重试" });

        if (req.Save) _db.SaveAssetImagePrompt(projectId, category, assetId, prompt);

        _logger.LogInformation("[AssetPrompt] 临时改写 projectId={ProjectId} {Category}#{AssetId} 保存={Save} 长度={Len} 要求={Instruction}",
            projectId, category, assetId, req.Save, prompt.Length, req.Instruction.Trim());
        return Ok(new
        {
            success = true,
            imagePrompt = prompt,
            saved = req.Save,
            message = req.Save ? "已按临时要求改写并保存到资产卡" : "已按临时要求改写（未保存，可直接出图）"
        });
    }

    /// <summary>
    /// 按当前模版为「整类资产」批量补生成提示词（默认只补还没有提示词的）。
    /// 每个资产一次大模型调用，逐个入库，单个失败不影响其余。
    /// </summary>
    [HttpPost("{category}/prompts/rebuild")]
    public async Task<IActionResult> RebuildAssetPrompts(int projectId, string category,
        [FromQuery] bool onlyMissing = true, CancellationToken ct = default)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        var uid = GetUserId();
        category = (category ?? "").Trim().ToLowerInvariant();
        if (DbService.AssetTableName(category) == null) return BadRequest(new { message = "未知资产类型: " + category });

        var list = ListAssetsForPrompt(projectId, category);
        if (list.Count == 0) return Ok(new { success = true, total = 0, updated = 0, failed = 0, message = "该分类下还没有资产" });

        var cfg = GetTextLlmConfig();
        if (cfg == null)
            return BadRequest(new { message = "尚未配置文本大模型：请到「API 配置」填写 DeepSeek / Qwen / Chat-GPT 任意一个的 API Key" });

        // 模版按「本剧 → 账号默认 → 出厂默认」取，所以每部剧的风格互不影响
        var tpl = _db.GetAssetPromptTemplate(uid, projectId, category);
        int updated = 0, failed = 0, skipped = 0;
        foreach (var a in list)
        {
            if (ct.IsCancellationRequested) break;
            if (onlyMissing && !string.IsNullOrWhiteSpace(a.ImagePrompt)) { skipped++; continue; }
            try
            {
                var raw = await _llm.GenerateAssetImagePrompt(
                    category, a.Name, a.Description, a.Attributes,
                    tpl, cfg.Value.apiUrl, cfg.Value.apiKey, cfg.Value.model, cfg.Value.thinkingMode);
                var prompt = NormalizeGeneratedPrompt(raw);
                if (string.IsNullOrWhiteSpace(prompt)) { failed++; continue; }
                _db.SaveAssetImagePrompt(projectId, category, a.AssetId, prompt);
                updated++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "[AssetPrompt] 批量生成失败 projectId={ProjectId} {Category}#{AssetId}", projectId, category, a.AssetId);
            }
        }

        _logger.LogInformation("[AssetPrompt] 批量生成完成 projectId={ProjectId} {Category} 共{Total} 更新{Updated} 跳过{Skipped} 失败{Failed}",
            projectId, category, list.Count, updated, skipped, failed);
        return Ok(new
        {
            success = true,
            total = list.Count,
            updated,
            skipped,
            failed,
            message = $"共 {list.Count} 条：更新 {updated} 条，跳过 {skipped} 条，失败 {failed} 条"
        });
    }

    /// <summary>按类型取资产卡上可用于出图的字段（名称 / 描述 / 属性 / 已生成提示词 / 负面词）。</summary>
    private (string Name, string? Description, string? Attributes, string? ImagePrompt, string? NegativePrompt)? ResolveAssetForImage(int projectId, string category, int assetId)
    {
        switch (category)
        {
            case "characters":
            {
                var a = _db.GetCharacterAssets(projectId).FirstOrDefault(x => x.AssetId == assetId);
                return a == null ? null : (a.Name, a.Description, a.Attributes, a.ImagePrompt, a.NegativePrompt);
            }
            case "props":
            {
                var a = _db.GetPropAssets(projectId).FirstOrDefault(x => x.AssetId == assetId);
                return a == null ? null : (a.Name, a.Description, (string?)null, a.ImagePrompt, a.NegativePrompt);
            }
            case "environments":
            {
                var a = _db.GetEnvAssets(projectId).FirstOrDefault(x => x.AssetId == assetId);
                return a == null ? null : (a.Name, a.Description, (string?)null, a.ImagePrompt, a.NegativePrompt);
            }
            case "effects":
            {
                var a = _db.GetEffectAssets(projectId).FirstOrDefault(x => x.AssetId == assetId);
                return a == null ? null : (a.Name, a.Description, (string?)null, a.ImagePrompt, a.NegativePrompt);
            }
            default:
                return null;
        }
    }

    /// <summary>按类型列出资产卡上的提示词相关字段（批量补提示词用）。</summary>
    private List<(int AssetId, string Name, string? Description, string? Attributes, string? ImagePrompt)> ListAssetsForPrompt(int projectId, string category)
    {
        var list = new List<(int, string, string?, string?, string?)>();
        switch (category)
        {
            case "characters":
                foreach (var a in _db.GetCharacterAssets(projectId))
                    list.Add((a.AssetId, a.Name, a.Description, a.Attributes, a.ImagePrompt));
                break;
            case "props":
                foreach (var a in _db.GetPropAssets(projectId))
                    list.Add((a.AssetId, a.Name, a.Description, null, a.ImagePrompt));
                break;
            case "environments":
                foreach (var a in _db.GetEnvAssets(projectId))
                    list.Add((a.AssetId, a.Name, a.Description, null, a.ImagePrompt));
                break;
            case "effects":
                foreach (var a in _db.GetEffectAssets(projectId))
                    list.Add((a.AssetId, a.Name, a.Description, null, a.ImagePrompt));
                break;
        }
        return list;
    }

    /// <summary>清理大模型返回的提示词：去掉代码围栏、自加的前缀、包裹引号。</summary>
    private static string NormalizeGeneratedPrompt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = raw.Trim();
        text = Regex.Replace(text, @"^```[a-zA-Z]*\s*", "");
        text = Regex.Replace(text, @"```\s*$", "").Trim();
        text = Regex.Replace(text, @"^\s*[-*\d\.、\s]*出图提示词\s*[：:]\s*", "");
        text = Regex.Replace(text, @"^\s*提示词\s*[：:]\s*", "");
        text = text.Trim().Trim('"', '\'', '“', '”', '「', '」').Trim();
        return text;
    }

    /// <summary>取文本大模型配置（和 Stage 阶段同一套选择与地址补全规则）。</summary>
    private (string apiUrl, string apiKey, string model, string? thinkingMode)? GetTextLlmConfig()
    {
        var uid = GetUserId();
        var active = _db.GetActiveLLMProvider(uid);
        if (active != "qwen" && active != "gpt") active = "deepseek";
        var defaults = new Dictionary<string, string>
        {
            ["deepseek"] = "https://api.deepseek.com/chat/completions",
            ["qwen"] = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
            ["gpt"] = "https://api.openai.com/v1/chat/completions"
        };
        var providers = new List<string> { active, "deepseek", "qwen", "gpt" };
        foreach (var p in providers.Distinct())
        {
            var cfg = _db.GetActiveConfig(uid, p);
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.ApiKey)) continue;
            var url = string.IsNullOrWhiteSpace(cfg.ApiUrl) ? defaults.GetValueOrDefault(p, "") : cfg.ApiUrl!.Trim();
            if (url.Length == 0) continue;
            if (!url.Contains("/chat/completions")) url = url.TrimEnd('/') + "/chat/completions";
            return (url, cfg.ApiKey!, cfg.ModelName ?? "", cfg.ThinkingMode);
        }
        return null;
    }

    /// <summary>取项目画风提示词（出图时拼到提示词末尾，保证资产图与成片风格一致）。</summary>
    private string? GetProjectStylePrompt(int projectId)
    {
        try
        {
            var project = _db.GetProjectById(projectId);
            if (project?.StyleId is int styleId)
            {
                var style = _db.GetVideoStyle(styleId);
                if (!string.IsNullOrWhiteSpace(style?.StylePrompt)) return style!.StylePrompt;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AssetImage] 取项目画风失败 projectId={ProjectId}", projectId);
        }
        return null;
    }

    [HttpDelete("characters/{assetId}")]
    public IActionResult DeleteCharacter(int projectId, int assetId)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        return ToDeleteResult(_db.DeleteCharacterAsset(projectId, assetId));
    }

    [HttpDelete("props/{assetId}")]
    public IActionResult DeleteProp(int projectId, int assetId)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        return ToDeleteResult(_db.DeletePropAsset(projectId, assetId));
    }

    [HttpDelete("environments/{assetId}")]
    public IActionResult DeleteEnvironment(int projectId, int assetId)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        return ToDeleteResult(_db.DeleteEnvAsset(projectId, assetId));
    }

    [HttpDelete("effects/{assetId}")]
    public IActionResult DeleteEffect(int projectId, int assetId)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        return ToDeleteResult(_db.DeleteEffectAsset(projectId, assetId));
    }

    private IActionResult ToDeleteResult(AssetDeleteResult r)
    {
        if (!r.Deleted) return NotFound(new { message = "资产不存在或已被删除" });
        return Ok(new
        {
            success = true,
            unitBindingsRemoved = r.UnitBindingsRemoved,
            frameBindingsRemoved = r.FrameBindingsRemoved
        });
    }

    [HttpPost("characters")]
    public IActionResult AddCharacter(int projectId, [FromBody] UpdateCharacterAssetRequest req)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "名称不能为空" });
        var id = _db.AddCharacterAsset(projectId, req.Name.Trim(), req.Description, req.ImageUrl, req.Attributes);
        if (!string.IsNullOrWhiteSpace(req.ImagePrompt)) _db.SaveAssetImagePrompt(projectId, "characters", id, req.ImagePrompt);
        return Ok(new { success = true, assetId = id });
    }

    [HttpPost("props")]
    public IActionResult AddProp(int projectId, [FromBody] UpdatePropAssetRequest req)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "名称不能为空" });
        var id = _db.AddPropAsset(projectId, req.Name.Trim(), req.Description, req.ImageUrl);
        if (!string.IsNullOrWhiteSpace(req.ImagePrompt)) _db.SaveAssetImagePrompt(projectId, "props", id, req.ImagePrompt);
        return Ok(new { success = true, assetId = id });
    }

    [HttpPost("environments")]
    public IActionResult AddEnvironment(int projectId, [FromBody] UpdateEnvAssetRequest req)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "名称不能为空" });
        var id = _db.AddEnvAsset(projectId, req.Name.Trim(), req.Description, req.ImageUrl);
        if (!string.IsNullOrWhiteSpace(req.ImagePrompt)) _db.SaveAssetImagePrompt(projectId, "environments", id, req.ImagePrompt);
        return Ok(new { success = true, assetId = id });
    }

    [HttpPost("effects")]
    public IActionResult AddEffect(int projectId, [FromBody] UpdateEnvAssetRequest req)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "名称不能为空" });
        var id = _db.AddEffectAsset(projectId, req.Name.Trim(), req.Description, req.ImageUrl);
        if (!string.IsNullOrWhiteSpace(req.ImagePrompt)) _db.SaveAssetImagePrompt(projectId, "effects", id, req.ImagePrompt);
        return Ok(new { success = true, assetId = id });
    }
}

public class UpdateCharacterAssetRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? Attributes { get; set; }

    /// <summary>出图提示词正文（提取时按模版生成，可在资产卡里改）。</summary>
    public string? ImagePrompt { get; set; }

    /// <summary>该资产专属负面提示词（留空则用模版里的统一负面词）。</summary>
    public string? NegativePrompt { get; set; }
}

public class UpdatePropAssetRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImagePrompt { get; set; }
    public string? NegativePrompt { get; set; }
}

public class UpdateEnvAssetRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImagePrompt { get; set; }
    public string? NegativePrompt { get; set; }
}

/// <summary>资产卡出图请求（各字段都可选，不传就用资产卡内容 + 类型默认尺寸）。</summary>
public class GenerateAssetImageRequest
{
    /// <summary>可选，形如 1280x720（16:9）；不传统一出 16:9（ImageService.AssetImageSize）。</summary>
    public string? Size { get; set; }

    /// <summary>可选，追加在提示词末尾的额外要求（例如「戴斗笠」「雨夜」）。</summary>
    public string? ExtraPrompt { get; set; }

    /// <summary>
    /// 可选，临时覆盖「出图提示词正文」——只作用于本次出图，不写回资产卡。
    /// 用于临时换装、临时改氛围：面板里改过没保存的正文，或按临时要求改写后的正文。
    /// </summary>
    public string? PromptOverride { get; set; }

    /// <summary>可选，临时覆盖「本条资产专属负面提示词」——同样只作用于本次出图。</summary>
    public string? NegativeOverride { get; set; }

    // ---- 参考图派生（角色换装/换形态）----
    // SourceImageUrl 非空即走「图生图」：把来源角色图与服装参考图一起交给中转的图片编辑接口，
    // 拼出的提示词里会写明「第 1 张是角色原型、第 2 张是服装参考」。五个字段全不传 = 原来的纯文生图。

    /// <summary>来源角色图（站内路径，如 /uploads/reference/xxx.png）。</summary>
    public string? SourceImageUrl { get; set; }

    /// <summary>服装参考图（站内路径，可空）。</summary>
    public string? GarmentImageUrl { get; set; }

    /// <summary>来源角色卡所在项目（弱引用；不传 = 来源是手动上传的图）。</summary>
    public int? SourceProjectId { get; set; }

    /// <summary>来源角色卡 Id（弱引用，源卡被删也照常出图）。</summary>
    public int? SourceAssetId { get; set; }

    /// <summary>派生备注，如「冬季版」。</summary>
    public string? SourceNote { get; set; }
}

/// <summary>批量出图请求。</summary>
public class QueueAssetImagesRequest
{
    /// <summary>characters / props / environments / effects。</summary>
    public string? Category { get; set; }

    /// <summary>missing（默认）= 只补还没有图的资产；all = 全部资产重新出图。</summary>
    public string? Scope { get; set; }
}

/// <summary>「让大模型按临时要求改写提示词」的请求。</summary>
public class RewriteAssetPromptRequest
{
    /// <summary>本次临时要求，例如「换成蓝色校服，雨夜街道」。</summary>
    public string? Instruction { get; set; }

    /// <summary>改写基准正文；不传则用资产卡里已存的正文。</summary>
    public string? BasePrompt { get; set; }

    /// <summary>true = 改写结果存回资产卡；false（默认）= 只返回，供本次出图临时使用。</summary>
    public bool Save { get; set; }
}
