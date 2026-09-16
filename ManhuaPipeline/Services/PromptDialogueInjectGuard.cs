using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 台词兜底：分镜「对话/台词」有原文、但 LLM 整段漏写台词时，
/// 按 Stage 5 时间轴/时间块语义把台词补回对应时间块，确保台词逐字不缺失。
/// </summary>
public static class PromptDialogueInjectGuard
{
    private static readonly Regex ShotHeaderRegex =
        new(@"(?:- \*\*)?镜头编号(?:\*\*)?\s*[:：]?\s*([\d.]+[a-zA-Z]?\-\d+)", RegexOptions.Compiled);

    private static readonly Regex DialogueLineRegex =
        new(@"(?:- \*\*)?(?:对话/台词|台词|对话)(?:\*\*)?\s*[:：]\s*(.*)$", RegexOptions.Compiled);

    private static readonly Regex QuoteRegex =
        new(@"[“""]([^“”""]*)[”""]", RegexOptions.Compiled);

    private static readonly Regex NewTimeBlockRegex =
        new(@"\[(?<start>\d+(?:\.\d+)?)\s*[-–]\s*(?<end>\d+(?:\.\d+)?)\s*s\]", RegexOptions.Compiled);

    private static readonly Regex OldTimeBlockRegex =
        new(@"(?m)^\s*(?<start>\d+(?:\.\d+)?)\s*[-–]\s*(?<end>\d+(?:\.\d+)?)\s*秒\s*\[\d+\]", RegexOptions.Compiled);

    private static readonly Regex TimelineSegmentRegex =
        new(@"(?<start>\d+(?:\.\d+)?)\s*[-–]\s*(?<end>\d+(?:\.\d+)?)\s*(?:s|秒)?\s*[:：]\s*(?<text>[^;；\r\n]+)", RegexOptions.Compiled);

    private static readonly string[] SpeechKeywords =
    {
        "说", "台词", "开口", "响起", "留音", "低喝", "怒吼", "喝道", "喊道", "念出",
        "声音", "吼出", "冷喝", "怒喝", "出拳", "轰出", "宣言"
    };

    private static readonly string[] SpeakerSuffixes = { "留音", "的声音", "之声" };

    private sealed record DialogueLine(string Speaker, string Text);

    private sealed record TimeBlock(double Start, double End, int LineIndex, string Text);

    public static string Apply(string promptText, string? unitContext)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;
        var stage5 = ParseDialogueMap(unitContext);
        if (stage5.Count == 0) return promptText;

        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var shotRegex = new Regex(@"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");
        var parts = shotRegex.Split(promptText);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var shotMatch = Regex.Match(part, @"【镜头([\d.]+[a-zA-Z]?\-\d+)】");
            if (!shotMatch.Success)
            {
                builder.Append(part);
                continue;
            }

            var shot = shotMatch.Groups[1].Value;
            if (!stage5.TryGetValue(shot, out var lines) || lines.Count == 0)
            {
                builder.Append(part);
                continue;
            }

            if (HasQuotes(part))
            {
                builder.Append(part);
                continue;
            }

            builder.Append(InjectDialogue(part, shot, lines, unitContext));
        }
        return builder.ToString();
    }

    private static Dictionary<string, List<DialogueLine>> ParseDialogueMap(string? unitContext)
    {
        var result = new Dictionary<string, List<DialogueLine>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(unitContext)) return result;

        string? currentShot = null;
        var currentLines = new List<DialogueLine>();
        var dialogueOpen = false;
        foreach (var rawLine in unitContext.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var shotMatch = ShotHeaderRegex.Match(line);
            if (shotMatch.Success)
            {
                Flush(result, ref currentShot, ref currentLines);
                currentShot = shotMatch.Groups[1].Value;
                currentLines = new List<DialogueLine>();
                dialogueOpen = false;
                continue;
            }

            if (currentShot == null) continue;

            // 「对话/台词」冒号后为空时，台词写在下一行缩进子项里，收集到下一个字段标题为止。
            if (dialogueOpen)
            {
                var continuation = StoryboardFrameFieldParser.TryGetDialogueContinuation(line);
                if (continuation == null)
                {
                    dialogueOpen = false;
                }
                else
                {
                    AppendDialogue(currentLines, continuation);
                    continue;
                }
            }

            var dialogueMatch = DialogueLineRegex.Match(line);
            if (!dialogueMatch.Success) continue;
            var value = dialogueMatch.Groups[1].Value.Trim();
            if (value.Length == 0)
            {
                dialogueOpen = true;   // 台词写在后续子项行
                continue;
            }
            if (value.Equals("无", StringComparison.OrdinalIgnoreCase)) continue;
            AppendDialogue(currentLines, value);
        }
        Flush(result, ref currentShot, ref currentLines);
        return result;
    }

    /// <summary>把「对话/台词」字段值切成台词（兼容内联引号、多说话人、无引号与子项列表四种写法）。</summary>
    private static void AppendDialogue(List<DialogueLine> lines, string value)
    {
        foreach (var segment in StoryboardFrameFieldParser.ExtractDialogueSegments(value))
            lines.Add(new DialogueLine(segment.Speaker, segment.Text));
    }

    private static void Flush(
        Dictionary<string, List<DialogueLine>> result,
        ref string? currentShot,
        ref List<DialogueLine> currentLines)
    {
        if (currentShot != null && currentLines.Count > 0)
            result[currentShot] = currentLines;
        currentShot = null;
        currentLines = new List<DialogueLine>();
    }

    private static string FormatDialogue(string speaker, string text)
    {
        if (speaker.StartsWith("内心独白", StringComparison.Ordinal)
            || speaker == "旁白" || speaker == "画外音")
        {
            var name = speaker.StartsWith("内心独白", StringComparison.Ordinal)
                ? speaker.Substring("内心独白".Length).TrimStart('-', '—', '－', ' ', ':')
                : "主角";
            if (name.Length == 0) name = "主角";
            return "内心独白-" + name + "：“" + text + "”";
        }
        return speaker + "说\"" + text + "\"";
    }

    private static bool HasQuotes(string shotText)
    {
        return QuoteRegex.IsMatch(shotText);
    }

    private static string InjectDialogue(
        string shotText,
        string shot,
        List<DialogueLine> lines,
        string? unitContext)
    {
        var resultLines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

        var dialogueFieldIndexes = new List<int>();
        for (var i = 0; i < resultLines.Count; i++)
        {
            var t = resultLines[i].TrimStart();
            if (t.StartsWith("对话:", StringComparison.Ordinal) || t.StartsWith("对话：", StringComparison.Ordinal))
                dialogueFieldIndexes.Add(i);
        }
        if (dialogueFieldIndexes.Count > 0)
            return InjectIntoDialogueFields(resultLines, dialogueFieldIndexes, lines);

        var blocks = new List<TimeBlock>();
        for (var i = 0; i < resultLines.Count; i++)
        {
            var newMatch = NewTimeBlockRegex.Match(resultLines[i]);
            if (newMatch.Success)
            {
                blocks.Add(new TimeBlock(
                    ParseSeconds(newMatch.Groups["start"].Value),
                    ParseSeconds(newMatch.Groups["end"].Value),
                    i,
                    resultLines[i]));
                continue;
            }

            var oldMatch = OldTimeBlockRegex.Match(resultLines[i]);
            if (oldMatch.Success)
            {
                blocks.Add(new TimeBlock(
                    ParseSeconds(oldMatch.Groups["start"].Value),
                    ParseSeconds(oldMatch.Groups["end"].Value),
                    i,
                    resultLines[i]));
            }
        }
        if (blocks.Count == 0) return shotText;

        var timelineRanges = ParseTimelineRanges(unitContext, shot);
        var usedBlocks = new HashSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var target = FindTargetBlock(lines[i], timelineRanges, blocks);
            while (usedBlocks.Contains(target) && target < blocks.Count - 1) target++;
            usedBlocks.Add(target);
            resultLines[blocks[target].LineIndex] += "；" + FormatDialogue(lines[i].Speaker, lines[i].Text);
        }
        return string.Join("\n", resultLines);
    }

    private static string InjectIntoDialogueFields(
        List<string> lines,
        List<int> dialogueFieldIndexes,
        List<DialogueLine> dialogueLines)
    {
        var fieldIndex = 0;
        foreach (var dialogue in dialogueLines)
        {
            while (fieldIndex < dialogueFieldIndexes.Count)
            {
                var idx = dialogueFieldIndexes[fieldIndex++];
                var colonIndex = lines[idx].IndexOfAny(new[] { ':', '：' });
                if (colonIndex < 0) continue;
                var value = lines[idx].Substring(colonIndex + 1).Trim();
                if (value.Equals("无", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
                {
                    var prefix = lines[idx].StartsWith("对话：", StringComparison.Ordinal) ? "对话：" : "对话:";
                    lines[idx] = prefix + FormatDialogue(dialogue.Speaker, dialogue.Text);
                    break;
                }
            }
        }
        return string.Join("\n", lines);
    }

    private static List<(double Start, double End, string Text)> ParseTimelineRanges(string? unitContext, string shot)
    {
        var result = new List<(double, double, string)>();
        if (string.IsNullOrWhiteSpace(unitContext)) return result;

        var lines = unitContext.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        string? currentShot = null;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var shotMatch = ShotHeaderRegex.Match(line);
            if (shotMatch.Success)
            {
                currentShot = shotMatch.Groups[1].Value;
                continue;
            }
            if (!string.Equals(currentShot, shot, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (Match m in TimelineSegmentRegex.Matches(line))
            {
                result.Add((
                    ParseSeconds(m.Groups["start"].Value),
                    ParseSeconds(m.Groups["end"].Value),
                    m.Groups["text"].Value.Trim()));
            }
        }
        return result;
    }

    private static int FindTargetBlock(
        DialogueLine line,
        List<(double Start, double End, string Text)> timelineRanges,
        List<TimeBlock> blocks)
    {
        foreach (var range in timelineRanges)
        {
            if (!SpeechKeywords.Any(k => range.Text.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
            var byTimeline = blocks.FindIndex(b => Overlaps(b, range.Start, range.End));
            if (byTimeline >= 0) return byTimeline;
        }

        var speakerCore = StripSpeakerCore(line.Speaker);
        if (speakerCore.Length > 0)
        {
            var bySpeaker = blocks.FindIndex(b =>
                b.Text.Contains(speakerCore, StringComparison.OrdinalIgnoreCase)
                && SpeechKeywords.Any(k => b.Text.Contains(k, StringComparison.OrdinalIgnoreCase)));
            if (bySpeaker >= 0) return bySpeaker;
        }

        var bySpeech = blocks.FindIndex(b =>
            SpeechKeywords.Any(k => b.Text.Contains(k, StringComparison.OrdinalIgnoreCase)));
        return bySpeech >= 0 ? bySpeech : 0;
    }

    private static string StripSpeakerCore(string speaker)
    {
        var core = speaker.Trim();
        foreach (var suffix in SpeakerSuffixes)
        {
            if (core.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && core.Length > suffix.Length)
                core = core.Substring(0, core.Length - suffix.Length);
        }
        return core;
    }

    private static bool Overlaps(TimeBlock block, double start, double end)
    {
        return block.Start < end && start < block.End;
    }

    private static double ParseSeconds(string value)
    {
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d
            : 0;
    }
}
