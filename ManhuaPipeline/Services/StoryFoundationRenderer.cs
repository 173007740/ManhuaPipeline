using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// L1 故事基线的解析、序列化与渲染。
/// 负责把一次调用产出的 <see cref="StoryFoundation"/> 落成阶段 1/2/3 各自的文本产物，
/// 以及从阶段 1 落库的 StructuredJson 还原故事基线，供阶段 2/3 直接复用（不再重复调用大模型）。
/// </summary>
public static class StoryFoundationRenderer
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>序列化故事基线，供阶段 1 的 StructuredJson 落库。</summary>
    public static string Serialize(StoryFoundation foundation) => JsonSerializer.Serialize(foundation, SerializeOptions);

    /// <summary>
    /// 从大模型原始返回中解析故事基线：容忍 ```json 代码块包裹、前后说明文字，
    /// 以及「集号/数组元素」被写成字符串或对象等常见偏差（逐字段宽松取值，尽量不让整次产出作废）。
    /// </summary>
    public static StoryFoundation? Parse(string? raw)
    {
        var json = ExtractJsonObject(raw);
        return json == null ? null : Resolve(json);
    }

    /// <summary>从阶段 1 落库的 StructuredJson 还原故事基线；为空或不合法时返回 null（调用方回退单阶段路径）。</summary>
    public static StoryFoundation? TryLoad(string? structuredJson)
    {
        if (string.IsNullOrWhiteSpace(structuredJson)) return null;
        return Resolve(structuredJson);
    }

    /// <summary>至少要有故事核心或分集大纲，才算一次有效产出（避免「空 JSON」把阶段 2/3 写成空内容）。</summary>
    public static bool HasContent(StoryFoundation? foundation) =>
        foundation != null &&
        (!string.IsNullOrWhiteSpace(foundation.StoryCore) || (foundation.Episodes?.Count ?? 0) > 0);

    /// <summary>阶段 1「创意构思」产物：节 1 故事核心 + 节 2 主线因果链。</summary>
    public static string RenderStage1(StoryFoundation f)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### 故事主题（一句话故事核心）");
        sb.AppendLine(SingleLine(f.StoryCore) ?? "（模型未给出）");
        sb.AppendLine();
        sb.AppendLine("### 主线因果链");
        var chain = CleanList(f.MainChain);
        if (chain.Count == 0) sb.AppendLine("1. （模型未给出）");
        else for (var i = 0; i < chain.Count; i++) sb.AppendLine((i + 1) + ". " + chain[i]);
        return sb.ToString().TrimEnd();
    }

    /// <summary>阶段 2「故事分析」产物：节 3 观众必须看懂的信息 + 节 4 情绪曲线。</summary>
    public static string RenderStage2(StoryFoundation f)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### 观众必须看懂的信息");
        var mustKnow = CleanList(f.AudienceMustKnow);
        if (mustKnow.Count == 0) sb.AppendLine("1. （模型未给出）");
        else for (var i = 0; i < mustKnow.Count; i++) sb.AppendLine((i + 1) + ". " + mustKnow[i]);

        sb.AppendLine();
        sb.AppendLine("### 情绪曲线");
        var points = (f.EmotionCurve ?? new List<EmotionCurvePoint>())
            .Where(p => p != null && (!string.IsNullOrWhiteSpace(p.Position) || !string.IsNullOrWhiteSpace(p.Emotion) || !string.IsNullOrWhiteSpace(p.Beat)))
            .ToList();
        if (points.Count == 0)
        {
            sb.AppendLine("- （模型未给出）");
        }
        else
        {
            foreach (var p in points)
            {
                var parts = new List<string>();
                var position = SingleLine(p.Position);
                var emotion = SingleLine(p.Emotion);
                var beat = SingleLine(p.Beat);
                if (position != null) parts.Add(position);
                if (emotion != null) parts.Add("情绪：" + emotion);
                if (beat != null) parts.Add("节拍：" + beat);
                sb.AppendLine("- " + string.Join("｜", parts));
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 阶段 3「全局蓝图」产物：分集大纲 + 节 5 视觉锚点。
    /// 注意两点，否则会破坏下游解析：
    /// 1) 分集大纲必须放最前，且严格用「## 第N集: 标题 / 概要：…」，这是 BlueprintEpisodeParser 与分集列表落库的解析格式；
    /// 2) 视觉锚点一律以「- 」开头，这样锚点文本里即使出现「第N集: …」也不会被误判成集标题。
    /// </summary>
    public static string RenderStage3(StoryFoundation f)
    {
        var sb = new StringBuilder();
        var episodes = (f.Episodes ?? new List<StoryEpisodeOutline>())
            .Where(e => e != null)
            .OrderBy(e => e.EpisodeNumber <= 0 ? int.MaxValue : e.EpisodeNumber)
            .ToList();

        if (episodes.Count == 0)
        {
            sb.AppendLine("## 第1集: 未命名");
            sb.AppendLine("概要：（模型未给出分集大纲）");
        }
        else
        {
            foreach (var ep in episodes)
            {
                var number = ep.EpisodeNumber > 0 ? ep.EpisodeNumber : 1;
                sb.AppendLine("## 第" + number + "集: " + (SingleLine(ep.Title) ?? "未命名"));
                sb.AppendLine("概要：" + (SingleLine(ep.Summary) ?? "（模型未给出概要）"));
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("### 视觉锚点");
        var anchors = CleanList(f.VisualAnchors);
        if (anchors.Count == 0) sb.AppendLine("- （模型未给出）");
        else foreach (var anchor in anchors) sb.AppendLine("- " + anchor);
        return sb.ToString().TrimEnd();
    }

    // ========== JSON 宽松解析 ==========

    private static StoryFoundation? Resolve(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = doc.RootElement;
            var foundation = new StoryFoundation
            {
                StoryCore = ReadString(root, "storyCore", "story_core", "core"),
                MainChain = ReadStringList(root, "mainChain", "main_chain", "causalChain", "chain"),
                AudienceMustKnow = ReadStringList(root, "audienceMustKnow", "audience_must_know", "mustKnow"),
                VisualAnchors = ReadStringList(root, "visualAnchors", "visual_anchors", "anchors"),
                EmotionCurve = ReadEmotionCurve(root),
                Episodes = ReadEpisodes(root)
            };
            return HasContent(foundation) ? foundation : null;
        }
    }

    private static JsonElement? Find(JsonElement root, params string[] names)
    {
        foreach (var property in root.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
            }
        }
        return null;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        var element = Find(root, names);
        return element.HasValue ? StringOf(element.Value) : null;
    }

    private static List<string> ReadStringList(JsonElement root, params string[] names)
    {
        var result = new List<string>();
        var element = Find(root, names);
        if (!element.HasValue) return result;
        switch (element.Value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.Value.EnumerateArray())
                {
                    var text = StringOf(item);
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
                }
                break;
            case JsonValueKind.String:
                var single = StringOf(element.Value);
                if (!string.IsNullOrWhiteSpace(single)) result.Add(single);
                break;
        }
        return result;
    }

    private static List<EmotionCurvePoint> ReadEmotionCurve(JsonElement root)
    {
        var result = new List<EmotionCurvePoint>();
        var element = Find(root, "emotionCurve", "emotion_curve", "emotions");
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in element.Value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                result.Add(new EmotionCurvePoint
                {
                    Position = ReadString(item, "position", "at", "where", "位置"),
                    Emotion = ReadString(item, "emotion", "mood", "情绪"),
                    Beat = ReadString(item, "beat", "event", "plot", "节拍", "剧情")
                });
            }
            else
            {
                var text = StringOf(item);
                if (!string.IsNullOrWhiteSpace(text)) result.Add(new EmotionCurvePoint { Beat = text });
            }
        }
        return result;
    }

    private static List<StoryEpisodeOutline> ReadEpisodes(JsonElement root)
    {
        var result = new List<StoryEpisodeOutline>();
        var element = Find(root, "episodes", "episodeOutline", "outline");
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Array) return result;
        var index = 0;
        foreach (var item in element.Value.EnumerateArray())
        {
            index++;
            if (item.ValueKind == JsonValueKind.Object)
            {
                result.Add(new StoryEpisodeOutline
                {
                    EpisodeNumber = ReadInt(item, index, "episodeNumber", "episode_number", "number", "episode", "集号"),
                    Title = ReadString(item, "title", "name", "标题"),
                    Summary = ReadString(item, "summary", "brief", "概要", "摘要", "简介")
                });
            }
            else
            {
                var text = StringOf(item);
                if (!string.IsNullOrWhiteSpace(text)) result.Add(new StoryEpisodeOutline { EpisodeNumber = index, Title = text });
            }
        }
        return result;
    }

    private static int ReadInt(JsonElement root, int fallback, params string[] names)
    {
        var element = Find(root, names);
        if (!element.HasValue) return fallback;
        if (element.Value.ValueKind == JsonValueKind.Number && element.Value.TryGetInt32(out var number)) return number > 0 ? number : fallback;
        var text = StringOf(element.Value);
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    /// <summary>把任意 JSON 值取成单行文本：字符串原样；对象/数组按「键：值」或「值 → 值」拼接。</summary>
    private static string? StringOf(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return SingleLine(element.GetString());
            case JsonValueKind.Number:
                return element.ToString();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.GetBoolean() ? "是" : "否";
            case JsonValueKind.Object:
                var parts = new List<string>();
                foreach (var property in element.EnumerateObject())
                {
                    var value = StringOf(property.Value);
                    if (!string.IsNullOrWhiteSpace(value)) parts.Add(property.Name + "：" + value);
                }
                return parts.Count == 0 ? null : string.Join(" → ", parts);
            case JsonValueKind.Array:
                var items = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    var value = StringOf(item);
                    if (!string.IsNullOrWhiteSpace(value)) items.Add(value);
                }
                return items.Count == 0 ? null : string.Join(" → ", items);
            default:
                return null;
        }
    }

    private static List<string> CleanList(List<string>? items)
    {
        if (items == null) return new List<string>();
        return items
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => SingleLine(i)!)
            .Where(i => i.Length > 0)
            .ToList();
    }

    private static string? SingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    /// <summary>取出最外层 JSON 对象：剥离 ```json 代码块围栏，再截取第一个 { 到最后一个 } 之间内容。</summary>
    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();

        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var newline = text.IndexOf('\n', fenceStart);
            if (newline >= 0)
            {
                var fenceEnd = text.IndexOf("```", newline, StringComparison.Ordinal);
                if (fenceEnd > newline) text = text.Substring(newline + 1, fenceEnd - newline - 1).Trim();
            }
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text.Substring(start, end - start + 1);
    }
}
