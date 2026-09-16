using System.Text.RegularExpressions;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

/// <summary>
/// 决定性 dump 测试：用真实 StoryboardAutoFixer.Fix 处理 1.2 单元的完整 LLM 返回，
/// 再用复刻的 SaveStoryboardFramesFromResult 状态机解析，把中间产物输出到文件。
/// </summary>
public class ReproDumpTests
{
    private const string LogPath = @"d:\CodexProject\ManhuaPipeline\logs\storyboard_planning_p30_s5_20260825_222519_313.log";
    private const string OutDir = @"d:\CodexProject\ManhuaPipeline\logs";

    [Fact]
    public void Dump_FixOutput_1_2()
    {
        var (chunk, _, _) = ExtractLastLLMReturn("1.2", "===== Unit 1.2 完成");
        File.WriteAllText(Path.Combine(OutDir, "_chunk_1_2_exact.txt"), chunk);

        var fixer = new StoryboardAutoFixer();
        var fixedText = fixer.Fix(chunk, "", new List<SkillLibraryItem>(), out var fixes);
        File.WriteAllText(Path.Combine(OutDir, "_fixed_1_2_exact.txt"), fixedText);

        var frames = ParseFrames(fixedText, "1.2", 1, "打斗/动作");
        var dump = string.Join("\n", frames.Select(f => $"{f.UnitNumber} | {f.ShotNumber} | Skills=[{f.Skills}] | Sort={f.SortOrder}"));
        File.WriteAllText(Path.Combine(OutDir, "_parsed_1_2_exact.txt"), dump);
    }

    [Fact]
    public void Dump_FixOutput_1_5()
    {
        var (chunk, _, _) = ExtractLastLLMReturn("1.5", "===== Unit 1.5 完成");
        File.WriteAllText(Path.Combine(OutDir, "_chunk_1_5_exact.txt"), chunk);

        var fixer = new StoryboardAutoFixer();
        var fixedText = fixer.Fix(chunk, "", new List<SkillLibraryItem>(), out var fixes);
        File.WriteAllText(Path.Combine(OutDir, "_fixed_1_5_exact.txt"), fixedText);

        var frames = ParseFrames(fixedText, "1.5", 1, "打斗/动作");
        var dump = string.Join("\n", frames.Select(f => $"{f.UnitNumber} | {f.ShotNumber} | Skills=[{f.Skills}] | Sort={f.SortOrder}"));
        File.WriteAllText(Path.Combine(OutDir, "_parsed_1_5_exact.txt"), dump);
    }

    /// <summary>取单元段内最后一次 LLM 返回的完整内容（不做任何截断）。</summary>
    private static (string Text, int Start, int End) ExtractLastLLMReturn(string unitNumber, string completeMarker)
    {
        var lines = File.ReadAllLines(LogPath);
        var start = Array.FindIndex(lines, l => l.StartsWith("===== Unit " + unitNumber + " 开始"));
        var end = Array.FindIndex(lines, l => l.StartsWith(completeMarker));
        if (start < 0 || end < 0) throw new InvalidOperationException($"markers not found: {unitNumber}");
        int llmStart = -1;
        for (var i = start; i < end; i++)
        {
            if (lines[i].StartsWith("---- LLM返回 ----")) llmStart = i;
        }
        if (llmStart < 0) throw new InvalidOperationException($"no LLM return for {unitNumber}");
        var from = llmStart + 1;
        var text = string.Join("\n", lines.Skip(from).Take(end - from));
        return (text, from, end);
    }

    // ========== 复刻 SaveStoryboardFramesFromResult 核心状态机（无 DB 保存） ==========
    private static List<StoryboardFrame> ParseFrames(string text, string defaultUnitNumber, int defaultEpisodeNumber, string defaultUnitType)
    {
        var lines = text.Split('\n');
        int currentEpisodeNum = defaultEpisodeNumber;
        string? currentUnitNumber = string.IsNullOrWhiteSpace(defaultUnitNumber) ? null : defaultUnitNumber.Trim();
        string? currentUnitType = string.IsNullOrWhiteSpace(defaultUnitType) ? null : defaultUnitType.Trim();
        string? currentUnitNote = null;
        string? currentUnitSkills = null;
        var currentFrames = new List<StoryboardFrame>();
        StoryboardFrame? currentFrame = null;
        var continuation = new StoryboardFrameFieldParser.ContinuationState();

        void AddCurrentFrame()
        {
            if (currentFrame == null) return;
            currentFrames.Add(currentFrame);
            currentFrame = null;
        }

        void FlushCurrentUnit()
        {
            AddCurrentFrame();
        }

        foreach (var rawLine in lines)
        {
            var l = rawLine.Trim();
            if (string.IsNullOrEmpty(l)) continue;

            var epMatch = Regex.Match(l, @"【?第\s*(\d+)\s*集】?");
            if (epMatch.Success)
            {
                FlushCurrentUnit();
                currentEpisodeNum = int.Parse(epMatch.Groups[1].Value);
                continue;
            }

            var unitMatch = Regex.Match(l, @"【?单元\s*([\d.]+[a-zA-Z]?)\s*】?");
            if (unitMatch.Success)
            {
                var newUnitNumber = unitMatch.Groups[1].Value.Trim();
                if (!string.Equals(currentUnitNumber, newUnitNumber, StringComparison.OrdinalIgnoreCase))
                {
                    FlushCurrentUnit();
                    currentUnitNumber = newUnitNumber;
                    if (!string.IsNullOrWhiteSpace(defaultUnitType)) currentUnitType = defaultUnitType.Trim();
                    currentUnitNote = null;
                    currentUnitSkills = null;
                }
                continue;
            }

            var shotNumber = StoryboardFrameParser.TryGetShotNumber(l);
            if (shotNumber != null)
            {
                AddCurrentFrame();
                continuation.TimelineOpen = false;
                continuation.CharacterListOpen = false;
                currentFrame = new StoryboardFrame
                {
                    ShotNumber = shotNumber,
                    UnitNumber = currentUnitNumber,
                    EpisodeNumber = currentEpisodeNum > 0 ? currentEpisodeNum : null,
                    Skills = currentUnitSkills,
                    SortOrder = currentFrames.Count
                };
                continue;
            }

            if (currentFrame == null)
            {
                var unitSkillLine = Regex.Match(l, @"^-\s*\*\*技能\*\*\s*[:：]\s*(.*)$");
                if (unitSkillLine.Success)
                {
                    var skillVal = unitSkillLine.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(skillVal)) currentUnitSkills = skillVal;
                }
                continue;
            }

            var unitNote = StoryboardFrameFieldParser.TryGetUnitNote(l);
            if (unitNote != null) currentUnitNote = unitNote;

            if (StoryboardFrameFieldParser.TryApplyField(currentFrame, l, continuation, ref currentUnitType))
                continue;

            StoryboardFrameFieldParser.TryAppendContinuation(currentFrame, l, continuation);
        }

        FlushCurrentUnit();
        return currentFrames;
    }
}
