using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Microsoft.AspNetCore.Mvc;

namespace ManhuaPipeline.Controllers;

/// <summary>
/// 无限画布（节点连线式出图）。
///
/// 职责边界：
///   · 这里只做「校验 + 入队」，真正出图由后台 CanvasImageQueueService 跑（关页面也跑完）。
///   · 节点有哪些端口、要不要出图，一律问 CanvasNodeCatalog，不在这里写死 —— 以后加节点类型不用改控制器。
///
/// 依赖推进（谁先跑）规则：
///   一个节点的上游全部 done 才会入队它；每有一个节点出完图，
///   CanvasImageRunner 会顺带把「上游已经齐了」的下游节点入队，于是整条链自动往下走。
/// </summary>
[ApiController]
[Route("api/canvas")]
public class CanvasController : ControllerBase
{
    private readonly DbService _db;
    private readonly CanvasEventHub _hub;
    private readonly ILogger<CanvasController> _logger;

    public CanvasController(DbService db, CanvasEventHub hub, ILogger<CanvasController> logger)
    {
        _db = db;
        _hub = hub;
        _logger = logger;
    }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    /// <summary>取不到画布就返回这个，避免把「别人的画布」和「不存在」区分开（不泄露信息）。</summary>
    private CanvasBoard? GetOwnBoard(int boardId, int uid)
    {
        var board = _db.GetCanvasBoard(boardId);
        return board != null && board.UserId == uid ? board : null;
    }

    // ==================== 元信息（前端渲染工具栏 / 端口用） ====================

    /// <summary>
    /// 节点类型目录 + 画幅选项。前端靠它渲染「加节点」按钮和端口，
    /// 后端新增一种节点类型后前端自动就有，不用改页面。
    /// </summary>
    [HttpGet("node-types")]
    public IActionResult GetNodeTypes()
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();

        // 配置页那份出图参数在这里当「新建节点的初始值」下发：节点上可以各自改，
        // 但新建出来看到的就是自己配的那套，不用每次重选。
        var defaults = _db.GetImageGenOptions(uid);

        return Ok(new
        {
            types = CanvasNodeCatalog.All.Select(t => new
            {
                type = t.Type,
                label = t.Label,
                icon = t.Icon,
                hint = t.Hint,
                inputs = t.Inputs,
                outputs = t.Outputs,
                generates = t.Generates,
                promptEditable = t.PromptEditable,
                defaultSize = t.DefaultSize
            }),
            sizes = CanvasSizes.Options.Select(o => new
            {
                ratio = o.Ratio,
                label = o.Label,
                size = o.Size,
                // 中转实际会吐回来的分辨率：总像素被锁在 ~1.573MP，请求值不等于输出值，
                // 页面上要显示这个（见 ImageGenOptions.ActualSizeForRatio）
                width = o.Width,
                height = o.Height
            }),
            // 出图渠道清单：节点面板上那个「用哪个模型」的下拉就靠它渲染。
            // 节点默认「跟随全局」（= 配置页标了默认的那条），这里把默认那条也下发，
            // 页面上才能写出「跟随全局（当前：方舟 seedream 2K）」这种看得懂的选项。
            imageModels = _db.GetImageConfigs(uid).Select(c => new
            {
                configId = c.ConfigId,
                name = string.IsNullOrWhiteSpace(c.DisplayName) ? (c.ModelName ?? "未命名渠道") : c.DisplayName,
                modelName = c.ModelName,
                isDefault = c.IsActive,
                resolutionHint = ImageGenOptions.ResolutionHintFor(c.ApiUrl, c.ModelName)
            }),
            defaultImageConfigId = _db.GetDefaultImageConfig(uid)?.ConfigId ?? 0,
            imageOptions = new
            {
                qualities = ImageGenOptions.Qualities,
                backgrounds = ImageGenOptions.Backgrounds,
                formats = ImageGenOptions.Formats,
                maxCount = ImageGenOptions.MaxCount,
                defaults = new
                {
                    aspectRatio = defaults.AspectRatio,
                    quality = defaults.Quality,
                    imageCount = defaults.ImageCount,
                    background = defaults.Background,
                    outputFormat = defaults.OutputFormat
                }
            }
        });
    }

    // ==================== 画布 ====================

    [HttpGet("boards")]
    public IActionResult ListBoards([FromQuery] int? projectId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetCanvasBoards(uid, projectId));
    }

    public sealed record CreateBoardRequest(string? Title, int? ProjectId);

    [HttpPost("boards")]
    public IActionResult CreateBoard([FromBody] CreateBoardRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();

        // 挂到项目下时校验项目归属，避免把画布挂到别人的项目上
        if (req.ProjectId.HasValue && !_db.ProjectBelongsToUser(req.ProjectId.Value, uid))
            return NotFound(new { message = "项目不存在" });

        var title = string.IsNullOrWhiteSpace(req.Title) ? "未命名画布" : req.Title!.Trim();
        var id = _db.CreateCanvasBoard(uid, req.ProjectId, title);
        return Ok(new { id, title });
    }

    /// <summary>一次拉全：画布 + 节点 + 连线。画布数据量小（几十个节点），没必要分三次请求。</summary>
    [HttpGet("boards/{boardId:int}")]
    public IActionResult GetBoard(int boardId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var board = GetOwnBoard(boardId, uid);
        if (board == null) return NotFound(new { message = "画布不存在" });

        return Ok(new
        {
            board,
            nodes = _db.GetCanvasNodes(boardId),
            edges = _db.GetCanvasEdges(boardId)
        });
    }

    /// <summary>
    /// 画布所属项目的画风提示词 —— 节点「跟随项目画风」时用的就是它（与生成视频同一套）。
    /// 没挂项目的独立灵感板返回空，页面据此提示「当前不套用任何风格」。
    /// </summary>
    [HttpGet("boards/{boardId:int}/style")]
    public IActionResult GetBoardStyle(int boardId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var board = GetOwnBoard(boardId, uid);
        if (board == null) return NotFound(new { message = "画布不存在" });

        var pid = board.ProjectId;
        string? style = null;
        string? projectTitle = null;
        if (pid is > 0)
        {
            style = AssetImageSupport.GetProjectStylePrompt(_db, _logger, pid.Value);
            projectTitle = _db.GetProject(pid.Value, uid)?.Title;
        }

        return Ok(new { projectId = pid, projectTitle, stylePrompt = style });
    }

    public sealed record BoardProjectRequest(int? ProjectId);

    /// <summary>
    /// 给画布挂（或解除）项目。画风是从项目上取的，所以这是「跟随项目画风」能不能生效的总开关。
    /// 传 null = 解除，画布变回独立灵感板。
    /// </summary>
    [HttpPatch("boards/{boardId:int}/project")]
    public IActionResult SetBoardProject(int boardId, [FromBody] BoardProjectRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var board = GetOwnBoard(boardId, uid);
        if (board == null) return NotFound(new { message = "画布不存在" });

        if (req.ProjectId.HasValue && !_db.ProjectBelongsToUser(req.ProjectId.Value, uid))
            return NotFound(new { message = "项目不存在" });

        _db.UpdateCanvasBoardProject(boardId, req.ProjectId);
        return Ok(new { ok = true, projectId = req.ProjectId });
    }

    public sealed record ViewportRequest(float X, float Y, float Scale);

    /// <summary>保存视口（平移 / 缩放）。前端拖拽结束后防抖调用，不会每帧打库。</summary>
    [HttpPatch("boards/{boardId:int}/viewport")]
    public IActionResult SaveViewport(int boardId, [FromBody] ViewportRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (GetOwnBoard(boardId, uid) == null) return NotFound(new { message = "画布不存在" });

        // 缩放下限 0.1、上限 4：再小看不清，再大没有意义
        var scale = Math.Clamp(req.Scale <= 0 ? 1f : req.Scale, 0.1f, 4f);
        _db.UpdateCanvasBoardViewport(boardId, req.X, req.Y, scale);
        return Ok(new { ok = true });
    }

    public sealed record TitleRequest(string Title);

    [HttpPatch("boards/{boardId:int}/title")]
    public IActionResult RenameBoard(int boardId, [FromBody] TitleRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (GetOwnBoard(boardId, uid) == null) return NotFound(new { message = "画布不存在" });

        var title = string.IsNullOrWhiteSpace(req.Title) ? "未命名画布" : req.Title.Trim();
        _db.UpdateCanvasBoardTitle(boardId, title);
        return Ok(new { ok = true });
    }

    [HttpDelete("boards/{boardId:int}")]
    public IActionResult DeleteBoard(int boardId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (GetOwnBoard(boardId, uid) == null) return NotFound(new { message = "画布不存在" });

        _db.DeleteCanvasBoard(boardId);
        return Ok(new { ok = true });
    }

    // ==================== 节点 ====================

    /// <summary>
    /// 建节点。ImageUrl / AssetId 只有素材节点用得上（自带图，不跑生成）；
    /// 会出图的节点忽略这两个字段 —— 图是跑出来之后才回填的。
    /// </summary>
    public sealed record CreateNodeRequest(
        string NodeType, float X, float Y, string? Title, string? ImageUrl, int? AssetId);

    [HttpPost("boards/{boardId:int}/nodes")]
    public IActionResult CreateNode(int boardId, [FromBody] CreateNodeRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (GetOwnBoard(boardId, uid) == null) return NotFound(new { message = "画布不存在" });

        var spec = CanvasNodeCatalog.Resolve(req.NodeType);
        if (spec == null) return BadRequest(new { message = "未知的节点类型：" + req.NodeType });

        // 素材节点是「自带图」的：从素材库拖进来、或者指定已有图片路径。
        // 它不是要生成的东西，而是给别人当参考图用的，所以有图就直接是 done，不用再排队跑一次。
        var img = string.IsNullOrWhiteSpace(req.ImageUrl) ? null : req.ImageUrl!.Trim();

        var node = new CanvasNode
        {
            BoardId = boardId,
            NodeType = spec.Type,
            X = req.X,
            Y = req.Y,
            Title = string.IsNullOrWhiteSpace(req.Title) ? spec.Label : req.Title!.Trim(),
            Size = spec.DefaultSize,
            ImageUrl = img,
            AssetId = req.AssetId,
            Status = img != null && !spec.Generates ? CanvasNodeStatus.Done : CanvasNodeStatus.Idle,
            // 出图参数按节点存：新建时把配置页那套写成初始值，节点面板打开看到的就是它，
            // 之后在节点上改只影响这一个节点（素材节点不出图，不需要）
            ExtraJson = spec.Generates
                ? CanvasNodeOptions.FromDefaults(_db.GetImageGenOptions(uid)).ToJson()
                : null
        };
        var id = _db.CreateCanvasNode(node);
        return Ok(new { id });
    }

    public sealed record PositionRequest(float X, float Y);

    [HttpPatch("nodes/{nodeId:int}/position")]
    public IActionResult MoveNode(int nodeId, [FromBody] PositionRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!OwnsNode(nodeId, uid, out var node)) return NotFound(new { message = "节点不存在" });

        _db.UpdateCanvasNodePosition(node!.Id, req.X, req.Y);
        return Ok(new { ok = true });
    }

    public sealed record NodeContentRequest(string? Title, string? Prompt, string? Size, string? ExtraJson);

    [HttpPatch("nodes/{nodeId:int}/content")]
    public IActionResult UpdateNode(int nodeId, [FromBody] NodeContentRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!OwnsNode(nodeId, uid, out var node)) return NotFound(new { message = "节点不存在" });

        // 画幅统一成中转接口认的 WxH，前端传比例（16:9）也收
        var size = req.Size == null ? null : CanvasSizes.Normalize(req.Size);

        // 出图参数按「打补丁」处理：前端只提交它改的那几项，
        // 候选图列表（一次出多张的结果）由后端维护，不能被前端没传的字段抹掉
        var extra = MergeNodeOptions(node!, req.ExtraJson);

        _db.UpdateCanvasNodeContent(node!.Id, req.Title, req.Prompt, size, extra);
        return Ok(new { ok = true });
    }

    /// <summary>
    /// 把前端传来的出图参数补丁并进节点已有的 ExtraJson：
    /// 没传的字段保持原值，Candidates / CandidateIndex 只能由后端改。
    /// </summary>
    private static string? MergeNodeOptions(CanvasNode node, string? incomingJson)
    {
        if (string.IsNullOrWhiteSpace(incomingJson)) return null;   // 一个字都没传就完全不动

        var current = CanvasNodeOptions.Parse(node.ExtraJson);
        var patch = CanvasNodeOptions.Parse(incomingJson);

        if (patch.AspectRatio != null) current.AspectRatio = patch.AspectRatio;
        if (patch.Quality != null) current.Quality = patch.Quality;
        if (patch.ImageCount is > 0) current.ImageCount = patch.ImageCount;
        if (patch.Background != null) current.Background = patch.Background;
        if (patch.OutputFormat != null) current.OutputFormat = patch.OutputFormat;

        // 出图渠道：> 0 = 这个节点单独挑了一个渠道；显式传 0 = 回到「跟随全局默认」；
        // 没传（null）就完全不动。这里不校验归属，出图时再按 userId 取，取不到会退回默认那条。
        if (patch.ConfigId is > 0) current.ConfigId = patch.ConfigId;
        else if (patch.ConfigId is 0) current.ConfigId = null;
        if (patch.StyleMode != null) current.StyleMode = patch.StyleMode;
        if (patch.StyleText != null) current.StyleText = patch.StyleText;
        // 风格库：> 0 = 挑了图片风格库里某一条；显式传 0 = 清空（退回不套用）；没传（null）就不动
        if (patch.StyleId is > 0) current.StyleId = patch.StyleId;
        else if (patch.StyleId is 0) current.StyleId = null;

        return current.ToJson();
    }

    public sealed record CandidateRequest(int Index);

    /// <summary>
    /// 一次出多张（n&gt;1）时在候选图之间切换：只换节点展示的那张，不重新出图、不花钱。
    /// </summary>
    [HttpPatch("nodes/{nodeId:int}/candidate")]
    public IActionResult SelectCandidate(int nodeId, [FromBody] CandidateRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!OwnsNode(nodeId, uid, out var node)) return NotFound(new { message = "节点不存在" });

        var opt = CanvasNodeOptions.Parse(node!.ExtraJson);
        var list = opt.Candidates ?? new List<string>();
        if (list.Count == 0) return BadRequest(new { message = "这个节点只有一张图" });

        var idx = Math.Clamp(req.Index, 0, list.Count - 1);
        opt.CandidateIndex = idx;

        _db.UpdateCanvasNodeContent(node.Id, null, null, null, opt.ToJson());
        _db.SetCanvasNodeStatus(node.Id, node.Status, list[idx], null);

        return Ok(new { ok = true, imageUrl = list[idx], index = idx, total = list.Count });
    }

    [HttpDelete("nodes/{nodeId:int}")]
    public IActionResult DeleteNode(int nodeId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!OwnsNode(nodeId, uid, out var node)) return NotFound(new { message = "节点不存在" });

        _db.DeleteCanvasNode(node!.Id);
        return Ok(new { ok = true });
    }

    // ==================== 连线 ====================

    public sealed record CreateEdgeRequest(int FromNodeId, int ToNodeId, string? FromPort, string? ToPort);

    [HttpPost("boards/{boardId:int}/edges")]
    public IActionResult CreateEdge(int boardId, [FromBody] CreateEdgeRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (GetOwnBoard(boardId, uid) == null) return NotFound(new { message = "画布不存在" });

        var from = _db.GetCanvasNode(req.FromNodeId);
        var to = _db.GetCanvasNode(req.ToNodeId);
        if (from == null || to == null || from.BoardId != boardId || to.BoardId != boardId)
            return NotFound(new { message = "节点不存在" });

        var fromPort = string.IsNullOrWhiteSpace(req.FromPort) ? CanvasPort.Out : req.FromPort!.Trim();
        var toPort = string.IsNullOrWhiteSpace(req.ToPort) ? CanvasPort.Ref : req.ToPort!.Trim();

        // 端口必须真的存在于这两个节点类型上（由 CanvasNodeCatalog 说了算）
        if (!CanvasNodeCatalog.HasOutput(from.NodeType, fromPort))
            return BadRequest(new { message = $"「{from.Title}」没有 {fromPort} 输出端口" });
        if (!CanvasNodeCatalog.HasInput(to.NodeType, toPort))
            return BadRequest(new { message = $"「{to.Title}」没有 {toPort} 输入端口" });
        if (from.Id == to.Id)
            return BadRequest(new { message = "不能连到自己" });
        if (_db.CanvasEdgeExists(from.Id, fromPort, to.Id, toPort))
            return BadRequest(new { message = "这两个端口之间已经有连线了" });
        if (CreatesCycle(boardId, from.Id, to.Id))
            return BadRequest(new { message = "这样连会形成循环，画布跑不下去了" });

        var id = _db.CreateCanvasEdge(boardId, from.Id, fromPort, to.Id, toPort);
        return Ok(new { id });
    }

    [HttpDelete("edges/{edgeId:int}")]
    public IActionResult DeleteEdge(int edgeId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();

        // 连线本身不存 UserId，先取出它再回查所属画布的归属
        var edge = _db.GetCanvasEdge(edgeId);
        if (edge == null) return NotFound(new { message = "连线不存在" });
        if (GetOwnBoard(edge.BoardId, uid) == null) return NotFound(new { message = "连线不存在" });

        _db.DeleteCanvasEdge(edgeId);
        return Ok(new { ok = true });
    }

    // ==================== 出图 ====================

    /// <summary>跑单个节点：上游没出好的会先把上游一起排队，避免拿到空参考图。</summary>
    [HttpPost("nodes/{nodeId:int}/run")]
    public IActionResult RunNode(int nodeId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!OwnsNode(nodeId, uid, out var node)) return NotFound(new { message = "节点不存在" });

        var spec = CanvasNodeCatalog.Resolve(node!.NodeType);
        if (spec == null) return BadRequest(new { message = "未知的节点类型" });
        if (!spec.Generates) return BadRequest(new { message = "素材节点本身不出图，它是给别人当参考图用的" });

        var count = EnqueueChain(node.BoardId, new[] { node.Id }, uid);
        return Ok(new { queued = count });
    }

    /// <summary>
    /// 运行整块画布：入队所有「上游已经齐了」的节点。
    /// 上游没出好的这一波不排 —— 等上游跑完，Runner 会自动把它们带起来。
    /// </summary>
    [HttpPost("boards/{boardId:int}/run")]
    public IActionResult RunBoard(int boardId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (GetOwnBoard(boardId, uid) == null) return NotFound(new { message = "画布不存在" });

        var nodes = _db.GetCanvasNodes(boardId);
        var edges = _db.GetCanvasEdges(boardId);
        var ready = nodes.Where(n => IsRunnable(n, nodes, edges)).Select(n => n.Id).ToArray();
        var count = EnqueueChain(boardId, ready, uid);
        return Ok(new { queued = count });
    }

    /// <summary>
    /// 节点状态 SSE 长连接。出图在后台跑，页面靠这条连接收结果。
    /// 写法与 VideoController.StreamEvents 一致（含关掉 nginx 缓冲两行）。
    /// </summary>
    [HttpGet("boards/{boardId:int}/events")]
    public async Task StreamEvents(int boardId, CancellationToken ct)
    {
        var uid = GetUserId();
        if (uid == 0)
        {
            Response.StatusCode = 401;
            return;
        }
        if (GetOwnBoard(boardId, uid) == null)
        {
            Response.StatusCode = 404;
            return;
        }

        Response.StatusCode = 200;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

        await Response.WriteAsync("retry: 3000\n\n", ct);
        await Response.Body.FlushAsync(ct);

        try
        {
            await foreach (var frame in _hub.SubscribeAsync(uid, boardId, ct))
            {
                await Response.WriteAsync(frame, ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    // ==================== 内部辅助 ====================

    private bool OwnsNode(int nodeId, int uid, out CanvasNode? node)
    {
        node = _db.GetCanvasNode(nodeId);
        if (node == null) return false;
        return GetOwnBoard(node.BoardId, uid) != null;
    }

    /// <summary>这条线连上后会不会成环：从 to 出发能不能走回 from。</summary>
    private bool CreatesCycle(int boardId, int fromNodeId, int toNodeId)
    {
        var edges = _db.GetCanvasEdges(boardId);
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(toNodeId);

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == fromNodeId) return true;
            if (!seen.Add(cur)) continue;
            foreach (var e in edges.Where(e => e.FromNodeId == cur))
                stack.Push(e.ToNodeId);
        }
        return false;
    }

    /// <summary>节点现在能不能跑：类型要能出图、状态允许入队、上游全部就绪。</summary>
    private static bool IsRunnable(CanvasNode node, List<CanvasNode> allNodes, List<CanvasEdge> edges)
    {
        var spec = CanvasNodeCatalog.Resolve(node.NodeType);
        if (spec == null || !spec.Generates) return false;
        if (!CanvasNodeStatus.CanEnqueue(node.Status)) return false;

        // 判定规则统一走 CanvasGraph —— 和 Runner 跑完带起下游时用的是同一套，避免两边规则跑偏
        return CanvasGraph.UpstreamReady(node, allNodes, edges);
    }

    /// <summary>
    /// 把节点进队列（连带补齐上游）。返回实际入队条数。
    /// 上游没出图时先排上游：否则下游拿不到参考图，会静默退化成文生图，结果和用户的连线意图不符。
    /// </summary>
    private int EnqueueChain(int boardId, IEnumerable<int> nodeIds, int uid)
    {
        var nodes = _db.GetCanvasNodes(boardId);
        var edges = _db.GetCanvasEdges(boardId);
        var queued = 0;

        // 目标节点 + 它们所有上游，按「上游在前」排好（规则在 CanvasGraph 里，和别处共用）
        var ordered = CanvasGraph.CollectWithUpstream(nodeIds, nodes, edges);
        var targets = nodeIds as IReadOnlyCollection<int> ?? nodeIds.ToArray();
        var isTarget = targets.ToHashSet();

        foreach (var n in ordered)
        {
            var spec = CanvasNodeCatalog.Resolve(n.NodeType);
            if (spec == null || !spec.Generates) continue;
            if (!CanvasNodeStatus.CanEnqueue(n.Status)) continue;

            // 顺带上来的上游，只在它「还没有可用图」时才补跑 —— 已经有图的上游重排一遍，
            // 既白花一次出图，也会把上游状态从已完成刷回排队中/生成中，看着像乱跑。
            // 用户点的目标节点不受此限：点它就是想重新生成。
            if (!isTarget.Contains(n.Id) && HasUsableImage(n)) continue;

            _db.EnqueueCanvasTask(boardId, n.Id, uid);
            _db.SetCanvasNodeStatus(n.Id, CanvasNodeStatus.Queued, null, null);
            _hub.Publish(boardId, new CanvasNodeEvent("node.status", n.Id, CanvasNodeStatus.Queued));
            queued++;
        }

        return queued;
    }

    /// <summary>
    /// 这个节点现在是否已经有一张能直接当参考图用的图。
    /// 出图节点：状态已完成且 ImageUrl 非空；素材节点（本身不出图）：只要指定过图片就算有。
    /// </summary>
    private static bool HasUsableImage(CanvasNode node)
    {
        var spec = CanvasNodeCatalog.Resolve(node.NodeType);
        if (spec == null) return false;
        return spec.Generates
            ? string.Equals(node.Status, CanvasNodeStatus.Done, StringComparison.OrdinalIgnoreCase)
              && !string.IsNullOrWhiteSpace(node.ImageUrl)
            : !string.IsNullOrWhiteSpace(node.ImageUrl);
    }
}
