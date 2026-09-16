using ManhuaPipeline.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

public static class BlueprintEpisodeParser
{
    private static readonly Regex EpisodeHeaderRegex = new(
        @"^\s*(?:#{1,6}\s*)?第\s*(\d+)\s*集\s*[:：]\s*(.+)$",
        RegexOptions.Compiled);

    private static readonly Regex EnglishEpisodeHeaderRegex = new(
        @"^\s*(?:#{1,6}\s*)?Episode\s+(\d+)\s*[:：]\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ParenthesizedSummaryRegex = new(
        @"^(.*?)[（(](.+)[)）]$",
        RegexOptions.Compiled);

    private static readonly Regex SummaryLabelPrefixRegex = new(
        @"^(概要|摘要|简介|概述|Summary)[:：]?\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<Episode> Parse(string text)
    {
        var episodes = new List<Episode>();
        if (string.IsNullOrWhiteSpace(text)) return episodes;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var seen = new HashSet<int>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryParseHeader(lines[i], out var episodeNumber, out var rest)) continue;
            if (!seen.Add(episodeNumber)) continue;

            var (title, summary) = ParseTitleAndSummary(rest, lines, i + 1);
            if (string.IsNullOrWhiteSpace(title)) continue;

            episodes.Add(new Episode
            {
                EpisodeNumber = episodeNumber,
                Title = title,
                Summary = string.IsNullOrWhiteSpace(summary) ? null : summary
            });
        }

        return episodes;
    }

    private static bool TryParseHeader(string line, out int episodeNumber, out string rest)
    {
        episodeNumber = 0;
        rest = "";

        var match = EpisodeHeaderRegex.Match(line);
        if (!match.Success)
            match = EnglishEpisodeHeaderRegex.Match(line);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out episodeNumber)) return false;

        rest = match.Groups[2].Value.Trim();
        return rest.Length > 0;
    }

    private static (string Title, string? Summary) ParseTitleAndSummary(string rest, string[] lines, int nextLineIndex)
    {
        var separatorIndex = FirstSeparatorIndex(rest);
        if (separatorIndex >= 0)
        {
            var title = Clean(rest.Substring(0, separatorIndex));
            var summaryToken = Clean(rest.Substring(separatorIndex + 1));
            var summary = StripSummaryLabel(summaryToken);
            if (string.IsNullOrWhiteSpace(summary))
                summary = ReadFollowingSummary(lines, nextLineIndex);

            return (title, summary);
        }

        var parenMatch = ParenthesizedSummaryRegex.Match(rest);
        if (parenMatch.Success)
        {
            var inside = Clean(parenMatch.Groups[2].Value);
            if (inside.Length >= 4)
                return (Clean(parenMatch.Groups[1].Value), inside);
        }

        return (Clean(rest), ReadFollowingSummary(lines, nextLineIndex));
    }

    private static int FirstSeparatorIndex(string text)
    {
        var ascii = text.IndexOf('|');
        var fullWidth = text.IndexOf('｜');
        if (ascii < 0) return fullWidth;
        if (fullWidth < 0) return ascii;
        return Math.Min(ascii, fullWidth);
    }

    private static string? ReadFollowingSummary(string[] lines, int startIndex)
    {
        var builder = new StringBuilder();
        for (var i = startIndex; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (builder.Length > 0) break;
                continue;
            }

            if (TryParseHeader(trimmed) || trimmed.StartsWith("---", StringComparison.Ordinal)) break;

            var wasBlockQuote = trimmed.StartsWith(">", StringComparison.Ordinal);
            var text = StripSummaryLabel(Clean(trimmed));
            if (string.IsNullOrWhiteSpace(text))
            {
                if (builder.Length > 0) break;
                continue;
            }

            if (builder.Length > 0 && !wasBlockQuote) break;

            builder.Append(text);
            if (!wasBlockQuote) break;
        }

        return builder.Length == 0 ? null : builder.ToString().Trim();
    }

    private static bool TryParseHeader(string line) =>
        EpisodeHeaderRegex.IsMatch(line) || EnglishEpisodeHeaderRegex.IsMatch(line);

    private static string StripSummaryLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var match = SummaryLabelPrefixRegex.Match(text);
        return match.Success ? match.Groups[2].Value.Trim() : text;
    }

    private static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        return text
            .Replace("**", "")
            .Replace("*", "")
            .Replace("`", "")
            .Trim()
            .TrimStart('>', ' ')
            .Trim();
    }
}
