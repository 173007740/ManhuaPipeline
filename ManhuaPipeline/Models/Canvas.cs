using System.Globalization;
using System.Text.Json;

namespace ManhuaPipeline.Models;

// =====================================================================
// 无限画布的数据模型
//
// 扩展性约定（重要）：
//   1. 节点有哪些输入/输出端口、默认出图尺寸、要不要真的出图，全部由
//      CanvasNodeCatalog 定义，数据库只存一个 NodeType 字符串。
//      —— 以后加一种节点（比如接 ComfyUI 工作流、加个「局部重绘」节点），
//         只在这里加一条 + Runner 里加一个分支，不用改表、不用改前端结构。
//   2. CanvasNode.ExtraJson 留给节点专属参数（seed / steps / 工作流 id …），
//      避免每加一种节点就给表加一列。
// =====================================================================

public class CanvasBoard
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? ProjectId { get; set; }
    public string Title { get; set; } = "";
    public float ViewportX { get; set; }
    public float ViewportY { get; set; }
    public float ViewportScale { get; set; } = 1f;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CanvasNode
{
    public int Id { get; set; }
    public int BoardId { get; set; }
    public string NodeType { get; set; } = CanvasNodeType.Text2Image;
    public float X { get; set; }
    public float Y { get; set; }
    public string? Title { get; set; }
    public string? Prompt { get; set; }
    public string? Size { get; set; }
    public string Status { get; set; } = CanvasNodeStatus.Idle;
    public string? ImageUrl { get; set; }
    public int? AssetId { get; set; }
    public string? ErrorMsg { get; set; }
    public string? ExtraJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CanvasEdge
{
    public int Id { get; set; }
    public int BoardId { get; set; }
    public int FromNodeId { get; set; }
    public string FromPort { get; set; } = CanvasPort.Out;
    public int ToNodeId { get; set; }
    public string ToPort { get; set; } = CanvasPort.Ref;
    public DateTime CreatedAt { get; set; }
}

public class CanvasTask
{
    public int TaskId { get; set; }
    public int BoardId { get; set; }
    public int NodeId { get; set; }
    public int UserId { get; set; }
    public string Status { get; set; } = CanvasTaskStatus.Queued;
    public string? ErrorMsg { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>节点状态。与 CanvasNodeStatus 一一对应，节点表上的 Status 反映它最近一次任务的结果。</summary>
public static class CanvasNodeStatus
{
    /// <summary>新建 / 编辑过但还没跑过。</summary>
    public const string Idle = "idle";
    /// <summary>已入队，等后台 worker 领走。</summary>
    public const string Queued = "queued";
    /// <summary>正在出图。</summary>
    public const string Running = "running";
    /// <summary>出好了，ImageUrl 有值。</summary>
    public const string Done = "done";
    /// <summary>失败，ErrorMsg 有原因。</summary>
    public const string Failed = "failed";

    /// <summary>只有这两个状态才允许再入队，避免同一次批量里被重复跑。</summary>
    public static bool CanEnqueue(string? status) =>
        !string.Equals(status, Queued, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(status, Running, StringComparison.OrdinalIgnoreCase);
}

public static class CanvasTaskStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>端口名。目前每种节点最多一个输出、一个（可多连的）参考图输入，字段名保留以便将来扩展多端口。</summary>
public static class CanvasPort
{
    public const string Out = "out";
    public const string Ref = "ref";
}

public static class CanvasNodeType
{
    /// <summary>文生图：只吃提示词。</summary>
    public const string Text2Image = "text2image";
    /// <summary>图生图：上游连进来的图当参考图 + 提示词。没接上游时自动退化成文生图。</summary>
    public const string Image2Image = "image2image";
    /// <summary>素材节点：引用素材库已有图片，本身不出图，只作为参考图来源。</summary>
    public const string Asset = "asset";
}

/// <summary>
/// 节点类型目录 —— 无限画布的「扩展点」。
///
/// 前后端共用同一份定义：前端通过 /api/canvas/node-types 拉这份目录渲染工具栏和端口，
/// 后端 Runner 按 Generates / Inputs 决定怎么跑。新增节点类型只改这里 + Runner 的一个分支。
/// </summary>
public static class CanvasNodeCatalog
{
    public sealed record NodeTypeSpec(
        string Type,
        string Label,
        string Icon,
        string Hint,
        string[] Inputs,
        string[] Outputs,
        bool Generates,
        bool PromptEditable,
        string DefaultSize);

    private static readonly Dictionary<string, NodeTypeSpec> Types =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [CanvasNodeType.Text2Image] = new NodeTypeSpec(
                CanvasNodeType.Text2Image, "文生图", "image",
                "填提示词直接出图；可自由选画幅",
                Array.Empty<string>(), new[] { CanvasPort.Out },
                Generates: true, PromptEditable: true, DefaultSize: CanvasSizes.Wide),

            [CanvasNodeType.Image2Image] = new NodeTypeSpec(
                CanvasNodeType.Image2Image, "图生图", "auto_awesome",
                "以上游节点的图作参考再出图；不连线时等同文生图",
                new[] { CanvasPort.Ref }, new[] { CanvasPort.Out },
                Generates: true, PromptEditable: true, DefaultSize: CanvasSizes.Wide),

            [CanvasNodeType.Asset] = new NodeTypeSpec(
                CanvasNodeType.Asset, "素材", "photo_library",
                "引用素材库已有图片，作为参考图来源（本身不出图）",
                Array.Empty<string>(), new[] { CanvasPort.Out },
                Generates: false, PromptEditable: false, DefaultSize: CanvasSizes.Wide),
        };

    public static IEnumerable<NodeTypeSpec> All => Types.Values;

    public static NodeTypeSpec? Resolve(string? type) =>
        type != null && Types.TryGetValue(type, out var spec) ? spec : null;

    public static bool IsValid(string? type) => Resolve(type) != null;

    /// <summary>该类型是否有这个输入端口（连线时校验用）。</summary>
    public static bool HasInput(string? type, string port) =>
        Resolve(type)?.Inputs.Contains(port, StringComparer.OrdinalIgnoreCase) == true;

    public static bool HasOutput(string? type, string port) =>
        Resolve(type)?.Outputs.Contains(port, StringComparer.OrdinalIgnoreCase) == true;

    public static string DefaultSizeFor(string? type) =>
        Resolve(type)?.DefaultSize ?? CanvasSizes.Wide;
}

/// <summary>
/// 出图画幅。前端给的是比例（16:9 / 1:1 / 3:2 …），这里换成中转接口要的 WxH。
/// 加新比例只改 ImageGenOptions.Ratios，前端下拉框直接读 /api/canvas/node-types 里的 sizes。
/// </summary>
public static class CanvasSizes
{
    public const string Square = "1024x1024";    // 1:1
    public const string Landscape = "1536x1024"; // 3:2 横
    public const string Portrait = "1024x1536";  // 2:3 竖
    public const string Wide = "1536x864";       // 16:9 横
    public const string Tall = "864x1536";       // 9:16 竖

    /// <summary>
    /// 一个画幅选项。Size 是发给接口的请求值，Width/Height 是中转「实际会吐回来」的分辨率
    /// （gpt-image-2.5 把总像素锁在 ~1.573MP 再按比例分配，请求值≠实际输出，见 ImageGenOptions）。
    /// 页面上显示 Width×Height，不显示请求值，免得误导。
    /// </summary>
    public sealed record SizeOption(string Ratio, string Label, string Size, int Width, int Height);

    public static readonly IReadOnlyList<SizeOption> Options = BuildOptions();

    private static SizeOption[] BuildOptions() =>
        ImageGenOptions.Ratios.Select(r =>
        {
            var (w, h) = ImageGenOptions.ActualSizeForRatio(r);
            return new SizeOption(r, ImageGenOptions.RatioLabel(r), ImageGenOptions.SizeForRatio(r), w, h);
        }).ToArray();

    /// <summary>默认画幅：16:9。</summary>
    public const string Default = Wide;

    /// <summary>
    /// 把比例（"16:9"）或尺寸（"1536x864"）统一成本系统用的尺寸字符串。
    /// 认不出来的按宽高比归到最接近的标准比例，再认不出才退回 16:9 —— 这样老数据里
    /// 同样 16:9 的 1280x720 / 1536x864 两种写法会被归一，前端下拉不会选不中。
    /// </summary>
    public static string Normalize(string? ratioOrSize)
    {
        var v = (ratioOrSize ?? "").Trim();
        if (v.Length == 0) return Wide;

        foreach (var o in Options)
        {
            if (string.Equals(o.Ratio, v, StringComparison.OrdinalIgnoreCase)) return o.Size;
            if (string.Equals(o.Size, v, StringComparison.OrdinalIgnoreCase)) return o.Size;
        }

        var target = AspectOf(v);
        if (target > 0)
        {
            var best = Options[0];
            var bestDiff = double.MaxValue;
            foreach (var o in Options)
            {
                var diff = Math.Abs(Math.Log(target / AspectOf(o.Ratio)));
                if (diff < bestDiff) { bestDiff = diff; best = o; }
            }
            return best.Size;
        }
        return Wide;
    }

    /// <summary>尺寸（"1536x864"）或比例（"16:9"）→ 标准比例串（"16:9"）；认不出来按 16:9。</summary>
    public static string RatioOf(string? ratioOrSize)
    {
        var size = Normalize(ratioOrSize);
        foreach (var o in Options)
            if (string.Equals(o.Size, size, StringComparison.OrdinalIgnoreCase)) return o.Ratio;
        return "16:9";
    }

    /// <summary>"16:9" 或 "1536x864" → 宽/高；解析不出来返回 0（表示不是这两种写法）。</summary>
    private static double AspectOf(string v)
    {
        var sep = v.Contains(':') ? ':' : (v.Contains('x') ? 'x' : '\0');
        if (sep == '\0') return 0;

        var p = v.Split(sep);
        if (p.Length != 2) return 0;
        if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return 0;
        return a > 0 && b > 0 ? a / b : 0;
    }
}

/// <summary>
/// 单个画布节点的出图参数 —— 存在 CanvasNodes.ExtraJson 里。
///
/// 为什么要按节点存：同一块画布上不同节点的需求经常不一样（这张要方版透明底、
/// 那张要 16:9 白底），全局一个默认值不够用。配置页那份（ImageGenOptions）退化成
/// 「新建节点时的初始值」，节点上可以各自改。
///
/// 每个字段都可空：null = 没单独设过，出图时用全局默认值兜底。
/// </summary>
public class CanvasNodeOptions
{
    /// <summary>画面比例（1:1 / 3:2 / 2:3 / 16:9 / 9:16）。null = 跟随全局默认。</summary>
    public string? AspectRatio { get; set; }

    /// <summary>质量档位（auto / low / medium / high / xhigh / max）。</summary>
    public string? Quality { get; set; }

    /// <summary>一次出几张（1–10）。null = 跟随全局默认。</summary>
    public int? ImageCount { get; set; }

    /// <summary>背景（auto / opaque / transparent）。</summary>
    public string? Background { get; set; }

    /// <summary>输出格式（png / jpeg / webp）。</summary>
    public string? OutputFormat { get; set; }

    /// <summary>
    /// 图片风格来源：project = 跟随所属项目的画风（就是生成视频用的那套风格提示词），
    /// none = 不套风格，custom = 用下面 StyleText 自己写的一段。默认 project。
    /// gpt-image 系列没有 style 参数，风格只能拼进提示词，所以这里只是决定「拼哪一段文本」。
    /// </summary>
    public string? StyleMode { get; set; }

    /// <summary>StyleMode=custom 时的风格文本。</summary>
    public string? StyleText { get; set; }

    /// <summary>
    /// StyleMode=library 时挑的那条风格（ImageStyles.StyleId）。
    /// 出图时取它的 StyleDesc 拼进提示词；风格从风格库来，跟画布挂不挂项目无关。
    /// </summary>
    public int? StyleId { get; set; }

    /// <summary>
    /// 用哪个出图渠道（LLMConfigs.ConfigId，Provider='image' 的其中一条）。
    /// null / 0 = 跟随全局默认（就是配置页上「设为默认」的那条）。
    /// 这样同一块画布上可以 A 节点用 seedream 出 2K、B 节点用 gpt-image 试画风。
    /// </summary>
    public int? ConfigId { get; set; }

    /// <summary>早期版本留下的值：风格来自画布所属项目的画风。已不再提供，见 NormalizedStyleMode。</summary>
    public const string StyleModeProject = "project";
    public const string StyleModeNone = "none";
    public const string StyleModeCustom = "custom";

    /// <summary>风格来自图片风格库（ImageStyles），节点上记的是 StyleId。</summary>
    public const string StyleModeLibrary = "library";

    /// <summary>
    /// 夹回「不套用 / 风格库 / 自定义」之一。
    /// project（跟随项目画风）已经不提供了 —— 风格统一从图片风格库挑，跟项目解耦；
    /// 老节点上存着 project 的一律退化成「不套用」，不会再跑去取项目画风。
    /// </summary>
    public string NormalizedStyleMode()
    {
        var m = (StyleMode ?? "").Trim().ToLowerInvariant();
        return m is StyleModeNone or StyleModeCustom or StyleModeLibrary ? m : StyleModeNone;
    }

    /// <summary>
    /// 一次出多张时落盘的全部图片（按顺序）。节点显示其中一张，可在卡片上左右切。
    /// </summary>
    public List<string>? Candidates { get; set; }

    /// <summary>当前展示第几张（0 起）。</summary>
    public int CandidateIndex { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CanvasNodeOptions Parse(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return new CanvasNodeOptions();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<CanvasNodeOptions>(extraJson!, Json)
                   ?? new CanvasNodeOptions();
        }
        catch (System.Text.Json.JsonException)
        {
            // ExtraJson 里塞过别的东西（或手改坏了）不能让节点直接打不开，当没设过处理
            return new CanvasNodeOptions();
        }
    }

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, Json);

    /// <summary>用全局默认值做一份「节点初始值」：新建节点后用户看到的就是配置页那套，可以再改。</summary>
    public static CanvasNodeOptions FromDefaults(ImageGenOptions defaults) => new()
    {
        AspectRatio = defaults.AspectRatio,
        Quality = defaults.Quality,
        ImageCount = defaults.ImageCount,
        Background = defaults.Background,
        OutputFormat = defaults.OutputFormat
    };

    /// <summary>与全局默认值合并成一份完整参数（节点没设的用全局补），并夹回合法范围。</summary>
    public ImageGenOptions Merge(ImageGenOptions defaults)
    {
        var merged = new ImageGenOptions
        {
            UserId = defaults.UserId,
            AspectRatio = string.IsNullOrWhiteSpace(AspectRatio) ? defaults.AspectRatio : AspectRatio,
            Quality = string.IsNullOrWhiteSpace(Quality) ? defaults.Quality : Quality,
            ImageCount = ImageCount is > 0 ? ImageCount.Value : defaults.ImageCount,
            Background = string.IsNullOrWhiteSpace(Background) ? defaults.Background : Background,
            OutputFormat = string.IsNullOrWhiteSpace(OutputFormat) ? defaults.OutputFormat : OutputFormat
        };
        return merged.Normalized();
    }
}

/// <summary>
/// 画布图结构的纯逻辑：不碰数据库，只回答「谁依赖谁、上游齐了没、该按什么顺序跑」。
///
/// Controller（决定入队哪些）和 CanvasImageRunner（跑完一个之后带起下游）共用同一份判定，
/// 避免两边各写一套规则、跑出来依赖顺序不一致。
/// </summary>
public static class CanvasGraph
{
    /// <summary>指向某节点的连线（即它的上游），按连线创建顺序 —— 参考图的先后顺序就是这么定的。</summary>
    public static List<CanvasEdge> UpstreamEdges(int nodeId, List<CanvasEdge> edges) =>
        edges.Where(e => e.ToNodeId == nodeId).OrderBy(e => e.Id).ToList();

    public static List<int> DownstreamIds(int nodeId, List<CanvasEdge> edges) =>
        edges.Where(e => e.FromNodeId == nodeId).Select(e => e.ToNodeId).Distinct().ToList();

    /// <summary>上游是不是都已经有图了：会出图的必须 done，素材类只要有图就算就绪。</summary>
    public static bool UpstreamReady(CanvasNode node, List<CanvasNode> all, List<CanvasEdge> edges)
    {
        foreach (var id in UpstreamEdges(node.Id, edges).Select(e => e.FromNodeId).Distinct())
        {
            var up = all.FirstOrDefault(n => n.Id == id);
            if (up == null) return false;

            var spec = CanvasNodeCatalog.Resolve(up.NodeType);
            var ready = spec != null && !spec.Generates
                ? !string.IsNullOrWhiteSpace(up.ImageUrl)                                    // 素材节点
                : string.Equals(up.Status, CanvasNodeStatus.Done, StringComparison.OrdinalIgnoreCase);

            if (!ready) return false;
        }
        return true;
    }

    /// <summary>这个节点现在能不能入队：类型要会出图 + 状态允许 + 上游齐了。</summary>
    public static bool CanEnqueue(CanvasNode node, List<CanvasNode> all, List<CanvasEdge> edges)
    {
        var spec = CanvasNodeCatalog.Resolve(node.NodeType);
        if (spec == null || !spec.Generates) return false;            // 素材节点不用跑
        if (!CanvasNodeStatus.CanEnqueue(node.Status)) return false;  // 已在队列里 / 正在跑
        return UpstreamReady(node, all, edges);
    }

    /// <summary>
    /// 目标节点 + 它们所有上游，按「上游排在前」的顺序返回（深度优先 + 去重）。
    /// 点一个下游节点「生成」时靠它把缺的上游一起补上 —— 否则下游拿不到参考图，
    /// 会静默退化成文生图，结果跟用户连的线对不上。
    /// </summary>
    public static List<CanvasNode> CollectWithUpstream(
        IEnumerable<int> ids, List<CanvasNode> all, List<CanvasEdge> edges)
    {
        var ordered = new List<CanvasNode>();
        var seen = new HashSet<int>();

        void Visit(int id)
        {
            if (!seen.Add(id)) return;
            var n = all.FirstOrDefault(x => x.Id == id);
            if (n == null) return;
            foreach (var e in UpstreamEdges(id, edges)) Visit(e.FromNodeId);
            ordered.Add(n);
        }

        foreach (var id in ids) Visit(id);
        return ordered;
    }
}
