using System.Text.RegularExpressions;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services.Combat;

namespace ManhuaPipeline.Services;

/// <summary>
/// 「角色名单文本 → 角色资产」匹配器（Stage 4 单元绑定与 Stage 5 帧绑定共用同一口径）。
/// 三级匹配，长候选优先，多义不绑：
///   1) 规范名：规范名（去括号/空白、转小写后）被目标文本整段包含；
///   2) 别名：资产卡 Description 的「角色别名」任一被目标文本整段包含；
///   3) 反向兜底：把「名单型字段」（出镜角色 / 关键元素）按并列符、破折号切成名字片段，
///      片段被规范名包含时算命中——正文写「洛伊娅」而资产规范名是「洛伊娅维娜」也能绑上。
///      该级只跑名单型字段，绝不在叙事文本里猜角色；一个片段能对应多个资产时判为歧义，不绑并报问题。
/// </summary>
public static class CharacterNameMatcher
{
    public sealed class Result
    {
        /// <summary>命中的角色资产（去重，长候选优先）。</summary>
        public List<CharacterAsset> Matched { get; } = new();

        /// <summary>歧义片段等问题描述（不带镜头/单元前缀，由调用方追加）。</summary>
        public List<string> Issues { get; } = new();
    }

    /// <summary>名单片段的分隔：并列符、斜杠、破折号、波浪号、空白、换行。</summary>
    private static readonly Regex TokenSplitRegex = new(
        @"[、，,;；/／|｜+＋·・＆&\s]|—+|–+|-+|~+|～+", RegexOptions.Compiled);

    private static readonly Regex BracketSegmentRegex = new(@"[（(][^）)]*[）)]", RegexOptions.Compiled);
    private static readonly Regex UnclosedBracketTailRegex = new(@"[（(][^）)]*$", RegexOptions.Compiled);
    private static readonly Regex UnclosedBracketHeadRegex = new(@"^[^（(]*[）)]", RegexOptions.Compiled);
    private static readonly Regex EdgePunctuationRegex = new(
        @"^[。，,：:；;、\.·…!！?？~～\-—\s\u3000]+|[。，,：:；;、\.·…!！?？~～\-—\s\u3000]+$", RegexOptions.Compiled);

    /// <summary>反向兜底时忽略的通用词：代词、群体、类别名，避免把「她/众人/人物」当角色。</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "无", "无人", "无出镜", "无角色", "无人物", "无出镜角色", "无出镜人物", "无面部出镜",
        "空", "空镜", "黑屏", "黑场", "没有", "none", "n/a",
        "他", "她", "它", "他们", "她们", "它们", "此人", "两人", "三人", "双人",
        "众人", "群众", "人群", "群像", "大家", "所有", "全部", "各自", "彼此", "对方", "自己",
        "旁白", "画外音", "声音", "台词", "字幕", "画面", "镜头", "背景",
        "人物", "角色", "主角", "配角", "路人", "身影", "人影", "环境", "道具", "特效"
    };

    /// <summary>
    /// 在 <paramref name="scanText"/> 里匹配角色资产。
    /// </summary>
    /// <param name="characters">项目角色资产库。</param>
    /// <param name="scanText">正向匹配区：本镜/本单元的结构化文本（已含名单字段）。</param>
    /// <param name="rosterText">反向兜底区：名单型字段（出镜角色 / 关键元素），只在这里切片段猜简称；传 null 表示不做反向兜底。</param>
    public static Result Match(
        IEnumerable<CharacterAsset>? characters,
        string? scanText,
        string? rosterText)
    {
        var result = new Result();
        var entries = (characters ?? Enumerable.Empty<CharacterAsset>())
            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new Entry(c, CandidateNames(c)))
            .Where(e => e.Names.Count > 0)
            .OrderByDescending(e => e.Names.Max(n => n.Length))
            .ThenBy(e => e.Asset.Name, StringComparer.Ordinal)
            .ToList();
        if (entries.Count == 0) return result;

        var hay = Normalize(scanText);
        var boundIds = new HashSet<int>();
        var pending = new List<Entry>();

        foreach (var entry in entries)
        {
            if (hay.Length > 0 && entry.Names.Any(n => hay.Contains(n, StringComparison.Ordinal)))
            {
                if (boundIds.Add(entry.Asset.AssetId)) result.Matched.Add(entry.Asset);
            }
            else
            {
                pending.Add(entry);
            }
        }

        if (pending.Count > 0) ReverseMatch(rosterText, entries, pending, boundIds, result);
        return result;
    }

    /// <summary>反向兜底：名单片段是规范名的一部分（正文写简称）时命中；片段能对应多个资产则判为歧义。</summary>
    private static void ReverseMatch(
        string? rosterText,
        IReadOnlyList<Entry> all,
        List<Entry> pending,
        HashSet<int> boundIds,
        Result result)
    {
        if (string.IsNullOrWhiteSpace(rosterText)) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in ExtractTokens(rosterText))
        {
            if (token.Length < 2 || StopWords.Contains(token)) continue;
            if (!seen.Add(token)) continue;

            var hits = all.Where(e => e.Names.Any(n => n.Contains(token, StringComparison.Ordinal))).ToList();
            if (hits.Count == 0) continue;
            if (hits.Count > 1)
            {
                result.Issues.Add($"名单片段「{token}」可对应多个角色资产（{string.Join("、", hits.Select(h => h.Asset.Name))}），已跳过自动引用，请改写为资产规范名");
                continue;
            }

            var only = hits[0];
            if (!pending.Remove(only)) continue;   // 已被规范名/别名正向命中，无需重复
            if (boundIds.Add(only.Asset.AssetId)) result.Matched.Add(only.Asset);
        }
    }

    private static IEnumerable<string> ExtractTokens(string rosterText)
    {
        var text = BracketSegmentRegex.Replace(rosterText, " ");
        text = UnclosedBracketTailRegex.Replace(text, " ");
        text = UnclosedBracketHeadRegex.Replace(text, " ");
        foreach (var raw in TokenSplitRegex.Split(text))
        {
            var token = EdgePunctuationRegex.Replace(raw, "").Replace(" ", "").Replace("\u3000", "");
            token = token.ToLowerInvariant();
            if (token.Length > 0) yield return token;
        }
    }

    /// <summary>候选名 = 规范名 + 资产卡「角色别名」，统一去括号/空白/小写。</summary>
    private static List<string> CandidateNames(CharacterAsset asset)
    {
        var names = new List<string>();
        void Add(string? raw)
        {
            var n = NormalizeName(raw);
            if (n.Length > 0 && !names.Contains(n, StringComparer.Ordinal)) names.Add(n);
        }

        Add(asset.Name);
        var aliasLine = CharacterAliasCatalog.GetAliasLine(asset.Description);
        if (!string.IsNullOrWhiteSpace(aliasLine))
        {
            foreach (var alias in CharacterAliasCatalog.ParseAliases(aliasLine))
            {
                foreach (var part in alias.Split(new[] { '|', '｜' }, StringSplitOptions.RemoveEmptyEntries))
                    Add(part);
            }
        }
        return names;
    }

    /// <summary>匹配区归一化：去括号段（避免括号里的解释文字被当成出场）、去空白、转小写。</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = BracketSegmentRegex.Replace(text, "");
        return t.Replace(" ", "").Replace("\u3000", "").Trim().ToLowerInvariant();
    }

    /// <summary>资产名归一化：去括号段、去空白、转小写（与两个 Resolver 原有口径一致）。</summary>
    private static string NormalizeName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = BracketSegmentRegex.Replace(text, "");
        return t.Replace(" ", "").Replace("\u3000", "").Trim().ToLowerInvariant();
    }

    private sealed class Entry
    {
        public Entry(CharacterAsset asset, List<string> names)
        {
            Asset = asset;
            Names = names;
        }

        public CharacterAsset Asset { get; }
        public List<string> Names { get; }
    }
}
