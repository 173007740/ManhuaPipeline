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
///
/// 出图参数（比例/质量/张数/背景/格式）来自任务快照，快照为空时回落到配置页的默认值，
/// 所以批量入队、老任务重跑都能拿到一份合法参数。
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
    public Task<GenerateOutcome> GenerateForTaskAsync(AssetImageTask task, CancellationToken ct)
    {
        TryBindIdentityAnchor(task);        // 没指定参考图时，看能不能拿同身份在前几集那张定妆图来锚定
        return string.IsNullOrWhiteSpace(task.SourceImageUrl)
            ? GenerateAsync(task, ct)
            : GenerateDerivedAsync(task, ct);
    }

    /// <summary>
    /// 出图前自动挂锚图：这条资产还没出过图，而同一个身份（漫剧复用资产身份层）在别的集
    /// 已经出好一张定妆图 —— 就把那张作为参考图带上，走派生路径出来的脸 / 结构 / 造型
    /// 跟前面几集是同一套。以前每一集各自凭提示词出，12 集下来就是 12 张不同的脸。
    ///
    /// 三条不插手的规矩：
    ///   1. 人手工指定了参考图 → 听人的；
    ///   2. 这条资产已经出过图 → 这次是「重新出」，由人决定要不要换形象；
    ///   3. 锚点就是本集自己出的 → 没必要拿自己参考自己。
    /// </summary>
    private void TryBindIdentityAnchor(AssetImageTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.SourceImageUrl)) return;
        try
        {
            var table = AssetImageSupport.TableOf(task.Category);
            if (string.IsNullOrEmpty(table)) return;

            var asset = AssetImageSupport.ResolveAsset(_db, task.ProjectId, task.Category, task.AssetId);
            if (asset == null) return;
            if (!string.IsNullOrWhiteSpace(asset.ImageUrl)) return;      // 已经出过图，不替人决定

            var identityId = _db.GetAssetIdentityId(table!, task.AssetId);
            if (identityId <= 0) return;

            var (url, _, srcProject, srcAsset) = _db.GetIdentityAnchor(identityId);
            if (string.IsNullOrWhiteSpace(url) || srcProject <= 0 || srcProject == task.ProjectId) return;

            task.SourceImageUrl = url;
            task.SourceProjectId = srcProject;
            task.SourceAssetId = srcAsset;
            var ep = _db.GetProjectById(srcProject)?.EpisodeNumber;
            task.SourceNote = "跨集复用：照着" + (ep.HasValue ? "第 " + ep.Value + " 集" : "前面一集")
                              + "同身份那张定妆图出，形象与它保持一致";

            _logger.LogInformation(
                "[AssetImage] 跨集锚定 projectId={ProjectId} {Category}#{AssetId} 身份={IdentityId} 参考第 {Ep} 集的定妆图",
                task.ProjectId, task.Category, task.AssetId, identityId, ep);
        }
        catch (Exception ex)
        {
            // 锚不上就照原样出，不能因为取锚点出错连图都出不了
            _logger.LogWarning(ex, "[AssetImage] 取跨集锚点失败，按普通出图走 projectId={ProjectId} {Category}#{AssetId}",
                task.ProjectId, task.Category, task.AssetId);
        }
    }

    /// <summary>
    /// 出图成功后登记锚点：这个身份的第一张图就是它的定妆图（后面几集照着它出）。
    /// 已有锚点的不会被顶掉 —— 要换锚点是人在页面上手动换的事。
    /// </summary>
    private void RegisterIdentityAnchor(AssetImageTask task, string localPath, string? prompt)
    {
        try
        {
            var table = AssetImageSupport.TableOf(task.Category);
            if (string.IsNullOrEmpty(table)) return;
            var identityId = _db.GetAssetIdentityId(table!, task.AssetId);
            if (identityId <= 0) return;
            _db.SetIdentityAnchor(identityId, localPath, prompt, task.ProjectId, task.AssetId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AssetImage] 登记身份锚点失败（不影响出图结果） projectId={ProjectId} {Category}#{AssetId}",
                task.ProjectId, task.Category, task.AssetId);
        }
    }

    /// <summary>
    /// 本次出图要用的参数：任务里带了就照它执行（入队时的快照，改配置不影响在跑的任务），
    /// 没带（批量入队、老任务）就取该用户在配置页里的默认值。
    /// </summary>
    private ImageGenOptions ResolveOptions(AssetImageTask task)
    {
        var def = _db.GetImageGenOptions(task.UserId);
        return new ImageGenOptions
        {
            UserId = task.UserId,
            AspectRatio = def.AspectRatio,
            Quality = string.IsNullOrWhiteSpace(task.Quality) ? def.Quality : task.Quality,
            ImageCount = task.ImageCount ?? def.ImageCount,
            Background = string.IsNullOrWhiteSpace(task.Background) ? def.Background : task.Background,
            OutputFormat = string.IsNullOrWhiteSpace(task.OutputFormat) ? def.OutputFormat : task.OutputFormat
        }.Normalized();
    }

    public async Task<GenerateOutcome> GenerateAsync(AssetImageTask task, CancellationToken ct)
    {
        var asset = AssetImageSupport.ResolveAsset(_db, task.ProjectId, task.Category, task.AssetId);
        if (asset == null) return new GenerateOutcome(false, null, 0, null, "资产不存在");

        var (config, model, configError) = ResolveImageConfig(task.UserId);
        if (configError != null) return new GenerateOutcome(false, null, 0, null, configError);

        var opt = ResolveOptions(task);
        var useSize = ImageService.IsValidSize(task.Size) ? task.Size!.Trim() : ImageService.DefaultSizeFor(task.Category);

        // 模版按「本剧 → 账号默认 → 出厂默认」取（ComposeFinalPrompt 内部取），所以每部剧的风格互不影响
        var projectStyle = AssetImageSupport.GetAssetStylePrompt(_db, _logger, task.ProjectId);
        var prompt = AssetImageSupport.ComposeFinalPrompt(
            _db, task.UserId, task.ProjectId, task.Category, asset, projectStyle,
            task.PromptOverride, task.NegativeOverride, task.ExtraPrompt,
            AssetImageSupport.GetAssetStyleNegative(_db, _logger, task.ProjectId));

        _logger.LogInformation(
            "[AssetImage] 开始出图 projectId={ProjectId} {Category}#{AssetId} size={Size} quality={Quality} 张数={Count} 背景={Bg} 格式={Fmt} model={Model}",
            task.ProjectId, task.Category, task.AssetId, useSize,
            opt.Quality, opt.ImageCount, opt.Background, opt.OutputFormat, model);

        // 中转实测一次只回一张（传 n=2 也只回 1 张），所以「一次出几张」在这里靠循环实现：
        // 每张都落盘并进参考图库，第一张回填资产卡。
        string? firstPath = null;
        var firstLibId = 0;
        var lastError = "出图失败：未拿到图片";

        for (var i = 0; i < opt.ImageCount; i++)
        {
            var result = await _image.GenerateAsync(prompt, config!.ApiUrl, config.ApiKey, model, useSize, ct, opt);
            if (!result.Ok || result.Data == null)
            {
                lastError = "出图失败：" + (result.Error ?? "未知错误");
                // 第一张就失败 = 这次出图没成；后面的失败不回滚前面已经出好的
                if (i == 0) return new GenerateOutcome(false, null, 0, prompt, lastError);
                _logger.LogWarning("[AssetImage] 第 {Index} 张出图失败（前面的已入图库，不影响资产卡）：{Error}", i + 1, result.Error);
                break;
            }

            var (localPath, fileSize) = ImageService.SaveLocalImage(result.Data, result.Ext ?? ".png", asset.Name, _logger);
            var libraryId = AssetImageSupport.SyncToReferenceLibrary(
                _db, _logger, task.UserId, task.ProjectId, task.Category, task.AssetId, asset.Name, localPath, fileSize);

            if (i == 0)
            {
                firstPath = localPath;
                firstLibId = libraryId;
                _db.UpdateAssetImage(task.ProjectId, task.Category, task.AssetId, localPath);
                RegisterIdentityAnchor(task, localPath, prompt);   // 这个身份的第一张图 → 定为定妆锚图
            }

            _logger.LogInformation(
                "[AssetImage] 第 {Index}/{Total} 张完成 projectId={ProjectId} {Category}#{AssetId} → {Path} ({Size}B), libraryAssetId={LibId}",
                i + 1, opt.ImageCount, task.ProjectId, task.Category, task.AssetId, localPath, fileSize, libraryId);
        }

        return firstPath == null
            ? new GenerateOutcome(false, null, 0, prompt, lastError)
            : new GenerateOutcome(true, firstPath, firstLibId, prompt, null);
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

        var opt = ResolveOptions(task);
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

        var projectStyle = AssetImageSupport.GetAssetStylePrompt(_db, _logger, task.ProjectId);
        var prompt = AssetImageSupport.ComposeFinalPrompt(
            _db, task.UserId, task.ProjectId, task.Category, asset, projectStyle,
            refNote + userBody, task.NegativeOverride, task.ExtraPrompt,
            AssetImageSupport.GetAssetStyleNegative(_db, _logger, task.ProjectId));

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
            "[AssetImage] 开始派生出图 projectId={ProjectId} {Category}#{AssetId} size={Size} quality={Quality} 张数={Count} model={Model} 参考图={RefCount}张 来源=({SourceProjectId}#{SourceAssetId}) 血缘={DerivationId}",
            task.ProjectId, task.Category, task.AssetId, useSize, opt.Quality, opt.ImageCount, model,
            refImages.Count, task.SourceProjectId, task.SourceAssetId, derivationId);

        string? firstPath = null;
        var firstLibId = 0;
        var lastError = "出图失败：未拿到图片";

        for (var i = 0; i < opt.ImageCount; i++)
        {
            var result = await _image.GenerateWithImagesAsync(
                prompt, refImages, config!.ApiUrl, config.ApiKey, model, useSize, ct,
                null, opt);
            if (!result.Ok || result.Data == null)
            {
                lastError = "出图失败：" + (result.Error ?? "未知错误");
                if (i == 0) return new GenerateOutcome(false, null, 0, prompt, lastError);
                _logger.LogWarning("[AssetImage] 第 {Index} 张派生出图失败（前面的已入图库）：{Error}", i + 1, result.Error);
                break;
            }

            var (localPath, fileSize) = ImageService.SaveLocalImage(result.Data, result.Ext ?? ".png", asset.Name, _logger);
            var libraryId = AssetImageSupport.SyncToReferenceLibrary(
                _db, _logger, task.UserId, task.ProjectId, task.Category, task.AssetId, asset.Name, localPath, fileSize);

            if (i == 0)
            {
                firstPath = localPath;
                firstLibId = libraryId;
                _db.UpdateAssetImage(task.ProjectId, task.Category, task.AssetId, localPath);
                RegisterIdentityAnchor(task, localPath, prompt);   // 同上：第一张图定为这个身份的定妆锚图
                if (derivationId > 0) _db.CompleteCharacterCardDerivation(derivationId, localPath);
            }

            _logger.LogInformation(
                "[AssetImage] 派生第 {Index}/{Total} 张完成 projectId={ProjectId} {Category}#{AssetId} → {Path} ({Size}B), libraryAssetId={LibId}, 血缘={DerivationId}",
                i + 1, opt.ImageCount, task.ProjectId, task.Category, task.AssetId, localPath, fileSize, libraryId, derivationId);
        }

        return firstPath == null
            ? new GenerateOutcome(false, null, 0, prompt, lastError)
            : new GenerateOutcome(true, firstPath, firstLibId, prompt, null);
    }

    /// <summary>取文生图配置并做非空校验，两条出图路径共用。Error 非空表示配置不可用。</summary>
    private (LLMConfig? Config, string Model, string? Error) ResolveImageConfig(int userId)
    {
        var config = _db.GetDefaultImageConfig(userId);
        if (config == null || string.IsNullOrWhiteSpace(config.ApiKey))
            return (null, "", "尚未配置出图渠道：请到「API 配置 → 图片模型配置」添加一个渠道（接口地址、API Key、模型名称）");
        var model = (config.ModelName ?? "").Trim();
        if (model.Length == 0)
            return (null, "", "出图模型名称为空：请到「API 配置 → 图片模型配置」补全该渠道的模型名称");
        return (config, model, null);
    }
}
