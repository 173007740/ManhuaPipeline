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
    /// <summary>
    /// 分集表的表头：第一格是「集 / 集号 / 集数 / Episode」，后面得有「标题」两个字。
    /// 允许加粗（模型爱写 **集号**）和空格。
    /// </summary>
    private static readonly Regex EpisodeHeaderRegex =
        new(@"^\s*\|\s*\*{0,2}\s*(集|集号|集数|Episode|EP)\s*\*{0,2}\s*\|.*(标题|名称|题目)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// P0 产出末尾常常带一张「本次假设 / 待确认清单」表（| # | 假设项 | 取值 | 依据 |），
    /// 它的第一列也是序号 —— 宽松模式（不认表、逐行扫）最容易把它读成分集。
    /// 表头里出现这些词就说明这不是分集表，整张跳过。
    /// </summary>
    private static readonly Regex ChecklistHeaderRegex =
        new(@"^\s*\|\s*[#＃]\s*\||(假设|取值|依据|待确认|待定|决策|选项|优先级|结论|备注|锁定项)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
    ///
    /// 先走严格模式：认出分集表的表头，只收这张表里的行。
    /// 这是被 dramaId=27 那次逼出来的——旧写法是「所有以竖线开头、第一格有数字的行都收」，
    /// 于是 P0 产出末尾那张《本次假设》清单（| # | 假设项 | 取值 | 依据 |）被当成
    /// 五张分集卡：剧名、孩子角色定级、至冬雪原场景定级……全成了单集项目。
    /// 那部剧用户填的是 1 集，P0 压根没写分集表 —— 唯一一张首列是数字的表就是它。
    ///
    /// 严格模式什么都读不到时才退回旧的宽松写法：模型改了表头写法不至于整个立项流程停摆。
    /// </summary>
    public static List<EpisodeOutline> Parse(string? text)
    {
        var strict = ParseInEpisodeTable(text);
        return strict.Count > 0 ? strict : ParseLoose(text);
    }

    /// <summary>严格模式：只收「分集表」那一段里的行。</summary>
    private static List<EpisodeOutline> ParseInEpisodeTable(string? text)
    {
        var list = new List<EpisodeOutline>();
        var seen = new HashSet<int>();
        var inEpisodeTable = false;

        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '|') { inEpisodeTable = false; continue; }

            if (EpisodeHeaderRegex.IsMatch(line)) { inEpisodeTable = true; continue; }
            if (!inEpisodeTable) continue;      // 别的段落里的表格（假设清单、决策项…）一律不收

            AddRow(list, seen, line);
        }
        return list.OrderBy(e => e.EpisodeNumber).ToList();
    }

    /// <summary>宽松兜底：不看是哪张表，符合「首列有数字 + 有标题」的行就收。旧写法，仅作退化用。</summary>
    private static List<EpisodeOutline> ParseLoose(string? text)
    {
        var list = new List<EpisodeOutline>();
        var seen = new HashSet<int>();
        var skipTable = false;

        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '|') { skipTable = false; continue; }
            if (EpisodeHeaderRegex.IsMatch(line)) continue;

            // 清单类表格（本次假设 / 待确认项 …）：整张跳掉，不看它的行
            if (ChecklistHeaderRegex.IsMatch(line)) { skipTable = true; continue; }
            if (skipTable) continue;

            AddRow(list, seen, line);
        }
        return list.OrderBy(e => e.EpisodeNumber).ToList();
    }

    private static void AddRow(List<EpisodeOutline> list, HashSet<int> seen, string line)
    {
        var cells = SplitRow(line);
        if (cells.Count < 3) return;

        var no = EpisodeNumber(cells[0]);
        if (no <= 0 || no > 999) return;              // 分隔行 |---|---| 在这里被滤掉
        if (!seen.Add(no)) return;

        var title = CleanTitle(cells[1]);
        if (title.Length == 0) return;

        list.Add(new EpisodeOutline
        {
            EpisodeNumber = no,
            Title = title,
            Outline = BlankToNull(cells[2]),
            Cliffhanger = cells.Count > 3 ? BlankToNull(cells[3]) : null
        });
    }

    private static string[] SplitLines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

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
