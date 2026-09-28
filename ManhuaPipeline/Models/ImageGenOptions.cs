namespace ManhuaPipeline.Models;

/// <summary>
/// 出图参数（页面上可配的那五个）：比例、质量、一次出几张、背景、输出格式。
/// 每个用户一份默认值，存在 dbo.ImageGenOptions；每次出图入队时再把快照写进任务表，
/// 所以出图过程中改配置不会影响已经在跑的任务。
/// </summary>
public class ImageGenOptions
{
    public int UserId { get; set; }

    /// <summary>画面比例：1:1 / 3:2 / 2:3 / 16:9 / 9:16。</summary>
    public string AspectRatio { get; set; } = "16:9";

    /// <summary>质量档位：auto / low / medium / high / xhigh / max。</summary>
    public string Quality { get; set; } = "high";

    /// <summary>一次出几张（1–10）。中转目前一次只回一张，由后端按次数循环实现。</summary>
    public int ImageCount { get; set; } = 1;

    /// <summary>背景：auto / opaque / transparent。</summary>
    public string Background { get; set; } = "opaque";

    /// <summary>输出格式：png / jpeg / webp。</summary>
    public string OutputFormat { get; set; } = "png";

    // ==================== 取值约束 ====================

    /// <summary>可选的画面比例（顺序即页面下拉顺序）。</summary>
    public static readonly string[] Ratios = { "1:1", "3:2", "2:3", "16:9", "9:16" };

    /// <summary>可选的质量档位。auto 由模型自己决定。</summary>
    public static readonly string[] Qualities = { "auto", "low", "medium", "high", "xhigh", "max" };

    /// <summary>可选的背景类型。</summary>
    public static readonly string[] Backgrounds = { "auto", "opaque", "transparent" };

    /// <summary>可选的输出格式。</summary>
    public static readonly string[] Formats = { "png", "jpeg", "webp" };

    /// <summary>一次最多出 10 张（接口上限）。</summary>
    public const int MaxCount = 10;

    // ==================== 比例 ↔ 尺寸 ====================

    /// <summary>
    /// 比例 → 发给接口的 size。取文档推荐值，全部满足「宽高为 16 的倍数」等约束。
    /// 注意这只是「请求值」，中转不一定照这个像素出（见 <see cref="ActualSizeForRatio"/>）。
    /// </summary>
    public static string SizeForRatio(string? ratio) => NormalizeRatio(ratio) switch
    {
        "1:1" => "1024x1024",
        "3:2" => "1536x1024",
        "2:3" => "1024x1536",
        "16:9" => "1536x864",
        "9:16" => "864x1536",
        _ => "1536x864"
    };

    /// <summary>
    /// 比例 → 中转实际吐回来的分辨率。
    /// 2026-09-19 实测 gpt-image-2.5：总像素被锁在 ~1.573MP，再按比例分配宽高
    /// （w = √(P×r)），所以不管请求 1536x864 还是 2560x1440，16:9 都出 1672x941。
    /// 页面上「显示分辨率」用这一份，免得写个请求值误导人。
    /// </summary>
    public static (int Width, int Height) ActualSizeForRatio(string? ratio) => NormalizeRatio(ratio) switch
    {
        "1:1" => (1254, 1254),
        "3:2" => (1536, 1024),
        "2:3" => (1024, 1536),
        "16:9" => (1672, 941),
        "9:16" => (940, 1672),
        _ => (1672, 941)
    };

    /// <summary>比例的中文名，页面上「3:2 横版」这样显示。</summary>
    public static string RatioLabel(string? ratio) => NormalizeRatio(ratio) switch
    {
        "1:1" => "1:1 方版",
        "3:2" => "3:2 横版",
        "2:3" => "2:3 竖版",
        "16:9" => "16:9 横版",
        "9:16" => "9:16 竖版",
        _ => "16:9 横版"
    };

    /// <summary>把各种写法归一成标准比例串；不认识的按 16:9 处理。</summary>
    public static string NormalizeRatio(string? ratio)
    {
        var r = (ratio ?? "").Trim().Replace(" ", "");
        foreach (var x in Ratios)
            if (string.Equals(r, x, StringComparison.OrdinalIgnoreCase)) return x;
        return "16:9";
    }

    /// <summary>把质量/背景/格式/张数都夹回合法范围，避免脏配置把出图打挂。</summary>
    public ImageGenOptions Normalized()
    {
        AspectRatio = NormalizeRatio(AspectRatio);
        Quality = Pick(Quality, Qualities, "high");
        Background = Pick(Background, Backgrounds, "opaque");
        OutputFormat = Pick(OutputFormat, Formats, "png");
        ImageCount = Math.Clamp(ImageCount <= 0 ? 1 : ImageCount, 1, MaxCount);
        return this;
    }

    private static string Pick(string? value, string[] allowed, string fallback)
    {
        var v = (value ?? "").Trim();
        foreach (var x in allowed)
            if (string.Equals(v, x, StringComparison.OrdinalIgnoreCase)) return x;
        return fallback;
    }

    // ==================== 出图渠道的分辨率提示 ====================

    /// <summary>
    /// 按渠道地址和模型名猜它能出多大，纯粹给页面当提示用，不参与出图。
    /// 猜不出来返回 null（页面就不显示），免得写个不准的数字误导人。
    ///
    /// 之所以需要它：不同渠道的规格差得离谱 —— 方舟 seedream 有像素下限、实际就是 2K，
    /// 而 gpt-image 系列总像素被锁在 ~1.57MP，连 1080p 都够不着。用户在画布挑渠道的时候
    /// 得先看见这个，不然会以为选哪个都一样。
    /// </summary>
    public static string? ResolutionHintFor(string? apiUrl, string? modelName)
    {
        var url = (apiUrl ?? "").Trim().ToLowerInvariant();
        var model = (modelName ?? "").Trim().ToLowerInvariant();

        if (url.Contains("volces.com") || model.Contains("seedream")) return "2560×1440（2K）";
        if (model.Contains("gpt-image")) return "1672×941（约 1.57MP，够不到 1080p）";
        return null;
    }
}
