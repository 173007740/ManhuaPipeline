using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 把 P0 产出里的「N 集分集卡点 + Cliffhanger」表读出来。
///
/// 模型写的是 Markdown 表格，四列：集号 / 标题（暂定）/ 三幕骨架 / 本集卡点。
/// 这张表是建单集项目的唯一依据——项目名取标题，项目简介取三幕骨架，集号取第一列。
///
/// 容错原则：模型产出没有编译期保证，换个写法随时可能。
/// 读不出来就返回空列表，让上层退化成「第N集」占位，绝不抛异常打断整个立项流程。
/// </summary>
public static class EpisodeOutlineParser
{
    /// <summary>表头行：第一格是「集」，第二格里有「标题」。</summary>
    private static readonly Regex HeaderRegex = new(@"^\s*\|\s*集\s*\|.*标题", RegexOptions.Compiled);

    /// <summary>取单元格里第一段连续数字当集号：兼容「1」「第1集」「EP01」几种写法。</summary>
    private static readonly Regex NumberRegex = new(@"\d+", RegexOptions.Compiled);

    /// <summary>标题尾部标记终集的括注要剥掉——「够了就停（终集）」当项目名只留「够了就停」。
    /// 只认终集这类词，别的括注（如「风车（上）」）是标题的一部分，不能动。</summary>
    private static readonly Regex FinaleTagRegex =
        new(@"\s*[（(]\s*(终集|大结局|完结|最终集|Finale)\s*[)）]\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>表格里空着的标题常写成这些占位符，一律当没写。</summary>
    private static readonly string[] EmptyMarks = { "—", "–", "-", "/", "待定", "无", "N/A" };

    /// <summary>
    /// 解析分集表。返回的列表按集号升序，同集号只取第一次出现的那行。
    /// </summary>
    public static List<EpisodeOutline> Parse(string? text)
    {
        var list = new List<EpisodeOutline>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var seen = new HashSet<int>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            // 只认表格行。正文里带竖线的说明文字不会误入
            if (line.Length == 0 || line[0] != '|') continue;
            if (HeaderRegex.IsMatch(line)) continue;

            var cells = SplitRow(line);
            if (cells.Count < 3) continue;

            var no = EpisodeNumber(cells[0]);
            if (no <= 0 || no > 999) continue;   // 分隔行 |---|---| 在这里被滤掉
            if (!seen.Add(no)) continue;

            var title = CleanTitle(cells[1]);
            if (title.Length == 0) continue;

            list.Add(new EpisodeOutline
            {
                EpisodeNumber = no,
                Title = title,
                Outline = BlankToNull(cells[2]),
                Cliffhanger = cells.Count > 3 ? BlankToNull(cells[3]) : null
            });
        }

        return list.OrderBy(e => e.EpisodeNumber).ToList();
    }

    /// <summary>按竖线拆格，去首尾空格，顺手去掉 markdown 的加粗与代码符。</summary>
    private static List<string> SplitRow(string line)
    {
        var body = line.Trim();
        if (body.StartsWith('|')) body = body[1..];
        if (body.EndsWith('|')) body = body[..^1];

        return body.Split('|')
                   .Select(c => c.Replace("**", "").Replace("*", "").Replace("`", "").Trim())
                   .ToList();
    }

    private static int EpisodeNumber(string cell)
    {
        var m = NumberRegex.Match(cell ?? "");
        return m.Success && int.TryParse(m.Value, out var n) ? n : 0;
    }

    private static string CleanTitle(string cell)
    {
        var t = FinaleTagRegex.Replace((cell ?? "").Trim(), "").Trim();
        return EmptyMarks.Contains(t) ? "" : t;
    }

    private static string? BlankToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
