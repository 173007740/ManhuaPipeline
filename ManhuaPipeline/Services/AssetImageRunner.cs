using ManhuaPipeline.Models;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Services;

/// <summary>
/// 真正执行一张资产出图：拼提示词 → 调中转出图 → 落盘 → 同步参考图库 → 回填资产卡。
/// 不依赖 HttpContext：后台队列（关页面也继续跑）与同步接口都复用它。
///
/// 两条执行路径，队列统一从 <see cref="GenerateForTaskAsync"/> 进来按任务内容分流：
///   · 纯文生图   <see cref="GenerateAsync"/> —— 只用资产卡提示词（原有路径，行为未变）
///   · 参考图派生 <see cref="GenerateDerivedAsync"/> —— 「来源角色图 + 服装参考图 + 描述性提示词」出图，
///     用于角色换装/换形态，出图的同时登记一条派生血缘（CharacterCardDerivations）
/// </summary>
public class AssetImageRunner
{
    private readonly DbService _db;
    private readonly ImageService _image;
    private readonly ILogger<AssetImageRunner> _logger;

    public AssetImageRunner(DbService db, ImageService image, ILogger<AssetImageRunner> logger)
    {
        _db = db;
        _image = image;
        _logger = logger;
    }

    public sealed record GenerateOutcome(
        bool Ok, string? ImageUrl, int LibraryAssetId, string? UsedPrompt, string? Error);

    /// <summary>队列任务入口：任务里带来源图时走「参考图派生」，否则走原来的纯文生图。</summary>
    public Task<GenerateOutcome> GenerateForTaskAsync(AssetImageTask task, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(task.SourceImageUrl)
            ? GenerateAsync(task.UserId, task.ProjectId, task.Category, task.AssetId,
                task.PromptOverride, task.NegativeOverride, task.ExtraPrompt, task.Size, ct)
            : GenerateDerivedAsync(task, ct);

    public async Task<GenerateOutcome> GenerateAsync(
        int userId, int projectId, string category, int assetId,
        string? promptOverride, string? negativeOverride, string? extraPrompt, string? size,
        CancellationToken ct)
    {
        var asset = AssetImageSupport.ResolveAsset(_db, projectId, category, assetId);
        if (asset == null) return new GenerateOutcome(false, null, 0, null, "资产不存在");

        var (config, model, configError) = ResolveImageConfig(userId);
        if (configError != null) return new GenerateOutcome(false, null, 0, null, configError);

        var useSize = ImageService.IsValidSize(size) ? size!.Trim() : ImageService.DefaultSizeFor(category);

        // 模版按「本剧 → 账号默认 → 出厂默认」取（ComposeFinalPrompt 内部取），所以每部剧的风格互不影响
        var projectStyle = AssetImageSupport.GetProjectStylePrompt(_db, _logger, projectId);
        var prompt = AssetImageSupport.ComposeFinalPrompt(
            _db, userId, projectId, category, asset, projectStyle, promptOverride, negativeOverride, extraPrompt);

        _logger.LogInformation(
            "[AssetImage] 开始出图 projectId={ProjectId} {Category}#{AssetId} size={Size} model={Model} 用资产提示词={UsePrompt} 临时覆盖={Overridden}",
            projectId, category, assetId, useSize, model, !string.IsNullOrWhiteSpace(asset.ImagePrompt),
            !string.IsNullOrWhiteSpace(promptOverride));

        var result = await _image.GenerateAsync(prompt, config!.ApiUrl, config.ApiKey, model, useSize, ct);
        if (!result.Ok || result.Data == null)
            return new GenerateOutcome(false, null, 0, prompt, "出图失败：" + (result.Error ?? "未知错误"));

        var (localPath, fileSize) = ImageService.SaveLocalImage(result.Data, result.Ext ?? ".png", asset.Name, _logger);
        var libraryId = AssetImageSupport.SyncToReferenceLibrary(
            _db, _logger, userId, projectId, category, assetId, asset.Name, localPath, fileSize);
        _db.UpdateAssetImage(projectId, category, assetId, localPath);

        _logger.LogInformation(
            "[AssetImage] 完成 projectId={ProjectId} {Category}#{AssetId} → {Path} ({Size}B), libraryAssetId={LibId}",
            projectId, category, assetId, localPath, fileSize, libraryId);

        return new GenerateOutcome(true, localPath, libraryId, prompt, null);
    }

    /// <summary>
    /// 参考图派生：以「来源角色图 + 服装参考图」为视觉参考生成新图，并登记派生血缘。
    /// 参考图顺序固定 —— 第 1 张是角色原型（锁定面部/发型/体型），第 2 张是服装参考（只取款式与配色），
    /// 这个分工必须写进提示词，否则模型不知道哪张图管什么。
    /// </summary>
    private async Task<GenerateOutcome> GenerateDerivedAsync(AssetImageTask task, CancellationToken ct)
    {
        var asset = AssetImageSupport.ResolveAsset(_db, task.ProjectId, task.Category, task.AssetId);
        if (asset == null) return new GenerateOutcome(false, null, 0, null, "资产不存在");

        var (config, model, configError) = ResolveImageConfig(task.UserId);
        if (configError != null) return new GenerateOutcome(false, null, 0, null, configError);

        var useSize = ImageService.IsValidSize(task.Size)
            ? task.Size!.Trim()
            : ImageService.DefaultSizeFor(task.Category);

        var refImages = new List<string> { task.SourceImageUrl!.Trim() };
        var garmentUrl = string.IsNullOrWhiteSpace(task.GarmentImageUrl) ? null : task.GarmentImageUrl!.Trim();
        if (garmentUrl != null) refImages.Add(garmentUrl);

        var refNote = (refImages.Count > 1
            ? "参考图共 2 张：第 1 张是角色原型，锁定面部、发型与体型；第 2 张是服装参考，只取它的款式、颜色与细节。"
            : "参考图共 1 张：作为角色原型，锁定面部、发型与体型。")
            + "参考图只用来锁定人物形象，画面比例不必跟随参考图 —— 请按 16:9 横向宽幅重新构图。";
        var userBody = string.IsNullOrWhiteSpace(task.PromptOverride)
            ? "保持人物身份不变，按参考图调整服装与造型。"
            : task.PromptOverride!.Trim();

        var projectStyle = AssetImageSupport.GetProjectStylePrompt(_db, _logger, task.ProjectId);
        var prompt = AssetImageSupport.ComposeFinalPrompt(
            _db, task.UserId, task.ProjectId, task.Category, asset, projectStyle,
            refNote + userBody, task.NegativeOverride, task.ExtraPrompt);

        // 血缘先落库拿到 Id，出图成功后回填结果图。
        // 失败也留下这条记录（ResultImageUrl 为空），排查时能看到「某次派生试过、没成」。
        var derivationId = string.Equals(task.Category, "characters", StringComparison.OrdinalIgnoreCase)
            ? _db.SaveCharacterCardDerivation(new CharacterCardDerivation
            {
                UserId = task.UserId,
                ProjectId = task.ProjectId,
                AssetId = task.AssetId,
                SourceProjectId = task.SourceProjectId,
                SourceAssetId = task.SourceAssetId,
                SourceImageUrl = task.SourceImageUrl!.Trim(),
                GarmentImageUrl = garmentUrl,
                Prompt = task.PromptOverride,
                Note = task.SourceNote
            })
            : 0;

        _logger.LogInformation(
            "[AssetImage] 开始派生出图 projectId={ProjectId} {Category}#{AssetId} size={Size} model={Model} 参考图={Count}张 来源=({SourceProjectId}#{SourceAssetId}) 血缘={DerivationId}",
            task.ProjectId, task.Category, task.AssetId, useSize, model, refImages.Count,
            task.SourceProjectId, task.SourceAssetId, derivationId);

        var result = await _image.GenerateWithImagesAsync(
            prompt, refImages, config!.ApiUrl, config.ApiKey, model, useSize, ct);
        if (!result.Ok || result.Data == null)
            return new GenerateOutcome(false, null, 0, prompt, "出图失败：" + (result.Error ?? "未知错误"));

        var (localPath, fileSize) = ImageService.SaveLocalImage(result.Data, result.Ext ?? ".png", asset.Name, _logger);
        var libraryId = AssetImageSupport.SyncToReferenceLibrary(
            _db, _logger, task.UserId, task.ProjectId, task.Category, task.AssetId, asset.Name, localPath, fileSize);
        _db.UpdateAssetImage(task.ProjectId, task.Category, task.AssetId, localPath);
        if (derivationId > 0) _db.CompleteCharacterCardDerivation(derivationId, localPath);

        _logger.LogInformation(
            "[AssetImage] 派生完成 projectId={ProjectId} {Category}#{AssetId} → {Path} ({Size}B), libraryAssetId={LibId}, 血缘={DerivationId}",
            task.ProjectId, task.Category, task.AssetId, localPath, fileSize, libraryId, derivationId);

        return new GenerateOutcome(true, localPath, libraryId, prompt, null);
    }

    /// <summary>取文生图配置并做非空校验，两条出图路径共用。Error 非空表示配置不可用。</summary>
    private (LLMConfig? Config, string Model, string? Error) ResolveImageConfig(int userId)
    {
        var config = _db.GetActiveConfig(userId, "image");
        if (config == null || string.IsNullOrWhiteSpace(config.ApiKey))
            return (null, "", "尚未配置文生图：请到「API 配置 → 文生图（中转）」填写接口地址、API Key 与模型名称");
        var model = (config.ModelName ?? "").Trim();
        if (model.Length == 0)
            return (null, "", "文生图模型名称为空：请到「API 配置 → 文生图（中转）」填写模型名称（如 gpt-image-1 / seedream-3.0 / gemini-2.5-flash-image）");
        return (config, model, null);
    }
}
