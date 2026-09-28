using ManhuaPipeline.Models;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Services;

/// <summary>
/// 画布节点出图执行器：把一条 CanvasTasks 跑完。
///
/// 与 <see cref="AssetImageRunner"/> 同一套路（不依赖 HttpContext，后台队列和同步接口都能调），
/// 差别在两处：
///   1. 参考图不是资产卡上的固定来源，而是「画布上连进来的上游节点」——沿 CanvasEdges 反查
///      ToNodeId = 自己的线，取上游节点的图。一条都没连上就自动退化成文生图，
///      这样图生图节点单独放着也能用。
///   2. 不做 16:9 强制裁切（资产卡要跟成片画幅对齐才需要）。画布是自由画幅，
///      用户选 1:1 就是 1:1，落盘原样保存。
///
/// 扩展点：新增一种会出图的节点类型，只要 CanvasNodeCatalog 里 Generates=true，
/// 再在 RunGenerateAsync 的 switch 里加一个分支即可。
/// </summary>
public class CanvasImageRunner
{
    private readonly DbService _db;
    private readonly ImageService _image;
    private readonly CanvasEventHub _hub;
    private readonly ILogger<CanvasImageRunner> _logger;

    public CanvasImageRunner(
        DbService db, ImageService image, CanvasEventHub hub, ILogger<CanvasImageRunner> logger)
    {
        _db = db;
        _image = image;
        _hub = hub;
        _logger = logger;
    }

    public sealed record Outcome(bool Ok, string? ImageUrl, string? Error);

    /// <summary>
    /// 一个节点最多攒多少张历史图（含每次新出的）。超了就从最旧的开始丢。
    /// 图都落在 uploads/canvas 下，不设上限的话 ExtraJson 和磁盘都会慢慢涨上去。
    /// </summary>
    private const int MaxCandidates = 20;

    /// <summary>一张上游参考图：Url 送去出图，Title 用来在提示词里做编号指代。</summary>
    private sealed record RefImage(string Url, string Title);

    /// <summary>队列入口：跑一条任务，顺带维护节点状态并推 SSE。</summary>
    public async Task<Outcome> RunAsync(CanvasTask task, CancellationToken ct)
    {
        var node = _db.GetCanvasNode(task.NodeId);
        if (node == null)
        {
            _db.FailCanvasTask(task.TaskId, "节点不存在");
            return new Outcome(false, null, "节点不存在");
        }

        _db.SetCanvasNodeStatus(node.Id, CanvasNodeStatus.Running, null, null);
        _hub.Publish(node.BoardId, new CanvasNodeEvent("node.status", node.Id, CanvasNodeStatus.Running));

        try
        {
            var result = await ExecuteNodeAsync(task.UserId, node, ct);

            if (result.Ok)
            {
                _db.SetCanvasNodeStatus(node.Id, CanvasNodeStatus.Done, result.ImageUrl, null);
                _db.CompleteCanvasTask(task.TaskId);
                _hub.Publish(node.BoardId,
                    new CanvasNodeEvent("node.status", node.Id, CanvasNodeStatus.Done, result.ImageUrl ?? ""));

                // 把「上游已经齐了」的下游带起来 —— 链式依赖靠这一步自己往下推进
                EnqueueReadyDownstream(node.BoardId, node.Id, task.UserId);

                _logger.LogInformation("[Canvas] 节点 {NodeId} 出图完成 → {Url}", node.Id, result.ImageUrl);
            }
            else
            {
                _db.SetCanvasNodeStatus(node.Id, CanvasNodeStatus.Failed, null, result.Error);
                _db.FailCanvasTask(task.TaskId, result.Error);
                _hub.Publish(node.BoardId,
                    new CanvasNodeEvent("node.status", node.Id, CanvasNodeStatus.Failed, "", result.Error ?? ""));

                _logger.LogWarning("[Canvas] 节点 {NodeId} 出图失败：{Error}", node.Id, result.Error);
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 停机：任务由调用方放回队列，这里只把节点状态退回 idle，别让它永远亮着「生成中」
            _db.SetCanvasNodeStatus(node.Id, CanvasNodeStatus.Idle, null, null);
            throw;
        }
        catch (Exception ex)
        {
            _db.SetCanvasNodeStatus(node.Id, CanvasNodeStatus.Failed, null, ex.Message);
            _db.FailCanvasTask(task.TaskId, ex.Message);
            _hub.Publish(node.BoardId,
                new CanvasNodeEvent("node.status", node.Id, CanvasNodeStatus.Failed, "", ex.Message));
            _logger.LogError(ex, "[Canvas] 节点 {NodeId} 出图异常", node.Id);
            return new Outcome(false, null, ex.Message);
        }
    }

    /// <summary>
    /// 一个节点出完图之后，把「上游已经齐了」的下游推进队列。
    ///
    /// 这是链式依赖能自己往下跑的关键：点「运行全部」时只排第一波（上游齐了的那些），
    /// 之后每出完一张就把它的下游带起来，直到整条链跑完 —— 不需要页面一直开着盯。
    /// 判定规则走 CanvasGraph，和 Controller 入队时用的是同一套。
    /// </summary>
    private void EnqueueReadyDownstream(int boardId, int finishedNodeId, int userId)
    {
        var nodes = _db.GetCanvasNodes(boardId);
        var edges = _db.GetCanvasEdges(boardId);

        foreach (var downId in CanvasGraph.DownstreamIds(finishedNodeId, edges))
        {
            var down = nodes.FirstOrDefault(n => n.Id == downId);
            if (down == null) continue;
            if (!CanvasGraph.CanEnqueue(down, nodes, edges)) continue;

            _db.EnqueueCanvasTask(boardId, down.Id, userId);
            _db.SetCanvasNodeStatus(down.Id, CanvasNodeStatus.Queued, null, null);
            _hub.Publish(boardId, new CanvasNodeEvent("node.status", down.Id, CanvasNodeStatus.Queued));
        }
    }

    private async Task<Outcome> ExecuteNodeAsync(int userId, CanvasNode node, CancellationToken ct)
    {
        var spec = CanvasNodeCatalog.Resolve(node.NodeType);
        if (spec == null)
            return new Outcome(false, null, "未知的节点类型：" + node.NodeType);

        // 素材节点本身不出图：它的图在创建时就已经定好，直接算完成
        if (!spec.Generates)
        {
            return string.IsNullOrWhiteSpace(node.ImageUrl)
                ? new Outcome(false, null, "这个节点还没有图片（素材节点需要先指定图片）")
                : new Outcome(true, node.ImageUrl, null);
        }

        // 出图参数按节点走：这个节点自己设过的用节点上的，没设过的用配置页那份默认值。
        // 同一块画布上不同节点常常要不同比例/质量，所以不能只认全局配置。
        var nodeOpt = CanvasNodeOptions.Parse(node.ExtraJson);
        var opt = nodeOpt.Merge(_db.GetImageGenOptions(userId));

        // 出图渠道同样是每个节点各记一份：挑过就用挑的那个，没挑就用全局默认那条。
        // 这样同一块画布上可以 A 节点用 seedream 出 2K、B 节点用 gpt-image 试画风。
        var (config, model, configError) = ResolveImageConfig(userId, nodeOpt.ConfigId);
        if (configError != null) return new Outcome(false, null, configError);
        var ratio = string.IsNullOrWhiteSpace(nodeOpt.AspectRatio)
            ? CanvasSizes.RatioOf(node.Size ?? spec.DefaultSize)   // 老节点没存过参数：按它原来的画幅推
            : opt.AspectRatio;

        // 上游参考图：连到本节点 ref 端口的所有上游节点的图（带标题，提示词里要用来编号指代）
        var refs = ResolveUpstreamImages(node);
        var refImages = refs.Select(r => r.Url).ToList();

        // 图片风格：默认跟随所属项目的画风（生成视频用的那套风格提示词），
        // 节点上可以改成「不套用」或自己写一段。gpt-image 没有 style 参数，风格只能拼进提示词。
        var styleText = ResolveStyleText(node, nodeOpt);

        var prompt = BuildPrompt(node, ratio, refs, styleText);
        var size = ImageGenOptions.SizeForRatio(ratio);

        _logger.LogInformation(
            "[Canvas] 开始出图 node={NodeId} type={Type} ratio={Ratio}({Size}) model={Model} 质量={Quality} 张数={Count} 背景={Bg} 格式={Fmt} 参考图={RefCount}张 风格={Style}",
            node.Id, node.NodeType, ratio, size, model, opt.Quality, opt.ImageCount,
            opt.Background, opt.OutputFormat, refImages.Count,
            styleText == null ? "无" : (styleText.Length > 20 ? styleText.Substring(0, 20) + "…" : styleText));

        // 一次出 N 张：中转目前一次只回一张（传 n=2 也只给 1 张），所以按次数循环。
        // 中途失败不浪费已经出好的那些 —— 有几张算几张，返回第一张 + 错误。
        var urls = new List<string>();
        string? lastError = null;

        for (var i = 0; i < opt.ImageCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            var result = refImages.Count > 0
                ? await _image.GenerateWithImagesAsync(
                    prompt, refImages, config!.ApiUrl, config.ApiKey, model, size, ct,
                    refs.Select(r => r.Title).ToList(), opt)
                : await _image.GenerateAsync(prompt, config!.ApiUrl, config.ApiKey, model, size, ct, opt);

            if (!result.Ok || result.Data == null)
            {
                lastError = result.Error ?? "未知错误";
                _logger.LogWarning("[Canvas] 节点 {NodeId} 第 {Index}/{Total} 张失败：{Error}",
                    node.Id, i + 1, opt.ImageCount, lastError);
                break;
            }

            urls.Add(SaveCanvasImage(result.Data, result.Ext ?? ".png", node.BoardId, node.Id));
            _logger.LogInformation("[Canvas] 节点 {NodeId} 第 {Index}/{Total} 张完成 → {Url}",
                node.Id, i + 1, opt.ImageCount, urls[^1]);
        }

        // 多张全部落盘后【追加】进节点的候选列表，而不是覆盖。
        // 覆盖的话，同一个节点重跑一次，上一次出的图就再也找不回来了 —— 文件其实还好端端
        // 躺在 uploads/canvas 下（文件名带毫秒，不会撞车），只是没人再记得它的 URL。
        // 现在翻历史靠两处：节点卡片上的左右切换条，和右侧面板的历史缩略图。
        var history = nodeOpt.Candidates ?? new List<string>();
        var all = history.Concat(urls).ToList();
        // 超过上限就丢最旧的。注意只从列表里移除、不动磁盘文件：
        // 「存入资源库」可能引用了同一个 URL，贸然删文件会把那边也弄坏。
        if (all.Count > MaxCandidates)
            all = all.Skip(all.Count - MaxCandidates).ToList();

        nodeOpt.Candidates = all;
        // 定位到本次新出的第一张：刚跑完当然是给看最新的，想看旧的往回翻就行。
        // 一张都没出成功时保持原索引不动，别把节点当前在看的图跳走。
        nodeOpt.CandidateIndex = urls.Count > 0
            ? Math.Clamp(all.Count - urls.Count, 0, all.Count - 1)
            : nodeOpt.CandidateIndex;
        _db.UpdateCanvasNodeContent(node.Id, null, null, size, nodeOpt.ToJson());

        if (urls.Count == 0)
            return new Outcome(false, null, "出图失败：" + lastError);

        // 出够了但中间折了几张，也当成成功 —— 提示一下少了几张就行
        if (urls.Count < opt.ImageCount)
            _logger.LogWarning("[Canvas] 节点 {NodeId} 只要到 {Got}/{Want} 张", node.Id, urls.Count, opt.ImageCount);

        // 返回本次新出的那张给节点显示（已经把索引定位到它了）
        return new Outcome(true, all[nodeOpt.CandidateIndex], null);
    }

    /// <summary>
    /// 取上游参考图：所有 ToNodeId = 本节点的连线，按连线 Id 排序（即用户连线的先后顺序），
    /// 取上游节点的图。上游没出好的（没跑过 / 失败了）直接跳过 —— 宁可少一张参考图，
    /// 也不要拿一张空路径去请求把整次出图搞失败。
    /// </summary>
    private List<RefImage> ResolveUpstreamImages(CanvasNode node)
    {
        var list = new List<RefImage>();
        var edges = _db.GetCanvasEdges(node.BoardId);
        foreach (var e in edges.Where(e => e.ToNodeId == node.Id
                                           && string.Equals(e.ToPort, CanvasPort.Ref, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(e => e.Id))
        {
            var upstream = _db.GetCanvasNode(e.FromNodeId);
            if (upstream == null || string.IsNullOrWhiteSpace(upstream.ImageUrl)) continue;
            if (!string.Equals(upstream.Status, CanvasNodeStatus.Done, StringComparison.OrdinalIgnoreCase)) continue;

            // 标题为空就退到节点类型名，保证编号说明里每行都有个叫得出来的名字
            var label = CanvasNodeCatalog.Resolve(upstream.NodeType)?.Label ?? upstream.NodeType;
            var title = string.IsNullOrWhiteSpace(upstream.Title) ? label : upstream.Title!.Trim();
            list.Add(new RefImage(upstream.ImageUrl!.Trim(), title));
        }
        return list;
    }

    /// <summary>
    /// 决定这个节点要套的那段风格文本：
    /// project（默认）= 取所属项目的画风，与生成视频用的是同一份（Projects.StyleId → VideoStyles.StylePrompt）；
    /// none = 完全不套；custom = 用节点上自己写的那段。
    /// 独立灵感板（没挂项目）取不到风格，就当不套用。
    /// </summary>
    private string? ResolveStyleText(CanvasNode node, CanvasNodeOptions opt)
    {
        var mode = opt.NormalizedStyleMode();
        if (mode == CanvasNodeOptions.StyleModeNone) return null;
        if (mode == CanvasNodeOptions.StyleModeCustom)
            return string.IsNullOrWhiteSpace(opt.StyleText) ? null : opt.StyleText!.Trim();

        // library：取图片风格库里那一条的风格描述。风格不再从画布所属项目上取，
        // 所以不挂项目的独立灵感板也能用风格。那条风格被删了就当没选，退化成不套用。
        if (mode == CanvasNodeOptions.StyleModeLibrary)
        {
            if (opt.StyleId is not > 0) return null;
            try
            {
                var style = _db.GetImageStyle(opt.StyleId.Value);
                return string.IsNullOrWhiteSpace(style?.StyleDesc) ? null : style!.StyleDesc.Trim();
            }
            catch (Exception ex)
            {
                // 取风格失败不该把出图打挂，退化成不套风格继续出
                _logger.LogWarning(ex, "[Canvas] 取图片风格失败 styleId={StyleId}", opt.StyleId);
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// 拼最终提示词：用户写的提示词 + 画幅约束 + 画面风格。
    /// 画幅必须显式强调，否则模型会按自己的习惯出图（实测图生图尤其容易跟随参考图比例）。
    /// </summary>
    private static string BuildPrompt(CanvasNode node, string ratio, IReadOnlyList<RefImage>? refs = null, string? styleText = null)
    {
        var aspect = $"画面规格：{ratio}（{ImageGenOptions.SizeForRatio(ratio)}）";

        var user = (node.Prompt ?? "").Trim();
        var body = user.Length == 0
            ? aspect + "，主体完整居中，构图干净。"
            : user + "\n" + aspect + "，主体完整居中。";

        // 风格段与资产出图保持同一写法（"画面风格：…"），画布出的图才能和成片同一画风
        if (!string.IsNullOrWhiteSpace(styleText))
            body += "\n画面风格：" + styleText!.Trim().TrimEnd('。', '.', '；', ';') + "。";

        // 有参考图就补一行编号说明：用户在提示词里写的「[图1]」「第2张」才有落点。
        // 没有这行的话，模型手里只是一堆图，只能靠猜用户指的是哪张
        var usable = (refs ?? Array.Empty<RefImage>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Url)).ToList();
        if (usable.Count == 0) return body;

        var names = usable.Select((r, i) => $"{i + 1}={r.Title}");
        var line = "【参考图说明】本次共有 " + usable.Count + " 张参考图，按顺序编号："
                   + string.Join("；", names)
                   + "。提示词里的「[图N]」「第N张」「图N」都指对应编号的那张参考图，请严格按编号理解。";
        return body + "\n" + line;
    }

    /// <summary>
    /// 落盘到 /uploads/canvas/{boardId}/。
    /// 刻意不走 ImageService.SaveLocalImage：那个方法固定在 uploads/reference 且会强制 16:9 裁切，
    /// 画布要保留用户选的画幅，所以自己存一份。
    /// </summary>
    private string SaveCanvasImage(byte[] data, string ext, int boardId, int nodeId)
    {
        var dir = Path.Combine(AppPaths.Root, "uploads", "canvas", boardId.ToString());
        Directory.CreateDirectory(dir);
        var fileName = $"node{nodeId}_{DateTime.Now:yyyyMMddHHmmssfff}{ext}";
        File.WriteAllBytes(Path.Combine(dir, fileName), data);
        return $"/uploads/canvas/{boardId}/{fileName}";
    }

    /// <summary>
    /// 取出图渠道并做非空校验。configId &gt; 0 = 节点单独挑的那条（取不到就退回默认，
    /// 比如那条渠道被删了）；否则用全局默认那条。
    /// </summary>
    private (LLMConfig? Config, string Model, string? Error) ResolveImageConfig(int userId, int? configId)
    {
        var config = configId is > 0
            ? _db.GetImageConfig(userId, configId.Value) ?? _db.GetDefaultImageConfig(userId)
            : _db.GetDefaultImageConfig(userId);
        if (config == null || string.IsNullOrWhiteSpace(config.ApiKey))
            return (null, "", "尚未配置出图渠道：请到「API 配置 → 图片模型配置」添加一个渠道（接口地址、API Key、模型名称）");
        var model = (config.ModelName ?? "").Trim();
        if (model.Length == 0)
            return (null, "", "出图模型名称为空：请到「API 配置 → 图片模型配置」补全该渠道的模型名称");
        return (config, model, null);
    }
}
