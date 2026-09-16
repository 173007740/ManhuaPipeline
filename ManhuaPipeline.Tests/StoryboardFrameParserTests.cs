using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class StoryboardFrameParserTests
{
    [Theory]
    [InlineData("承接镜头1结束画面继续推进")]
    [InlineData(" 承接上一镜头结束画面 ")]
    [InlineData("镜头描述: 承接镜头1结束画面")]
    [InlineData("")]
    public void ContinuationOrBodyLine_ReturnsNull(string line)
    {
        Assert.Null(StoryboardFrameParser.TryGetShotNumber(line));
    }

    [Theory]
    [InlineData("镜头编号: 1.4-1", "1.4-1")]
    [InlineData("镜头编号： 1.4-1", "1.4-1")]
    [InlineData("- 镜头 1.4-1:", "1.4-1")]
    [InlineData("- **镜头编号**: 1.4-1", "1.4-1")]
    [InlineData("镜头 1.4-1：承接前镜", "1.4-1")]
    [InlineData("Shot 1.4-1:", "1.4-1")]
    public void ShotHeader_ReturnsShotNumber(string line, string expected)
    {
        Assert.Equal(expected, StoryboardFrameParser.TryGetShotNumber(line));
    }

    [Theory]
    [InlineData("- **节拍**: Beat1", 1)]
    [InlineData("节拍: 3", 3)]
    [InlineData("- 节拍序号：Beat 02", 2)]
    [InlineData("CombatBeatIndex: 4", 4)]
    [InlineData("Beat7", 7)]
    [InlineData("7", 7)]
    [InlineData("- **节拍**: Beat01 [T3_SURROUND_ATTACK] 合围压迫", 1)]
    [InlineData("- **节拍**: Beat02（T3_PIN_DOWN 近身压制）", 2)]
    // StageController 剥离字段名后只把值传给解析器，说明写在值后也要能识别。
    [InlineData("Beat1（蓄势·缓）", 1)]
    [InlineData("Beat4（爆发·第三缕→定格·夜云中分）", 4)]
    [InlineData("承接节拍: 无", null)]
    public void BeatIndexLine_ReturnsIndex(string line, int? expected)
    {
        Assert.Equal(expected, StoryboardFrameParser.TryGetCombatBeatIndex(line));
    }

    [Fact]
    public void HasAnyShot_RejectsPreambleOnlyText()
    {
        const string text = """
        ### 【单元1.1】七曜诛圣阵拔地而起
        - **控制模式**: 打斗模板
        - **技能**: 力破万法
        """;

        Assert.False(StoryboardFrameParser.HasAnyShot(text));
    }

    [Fact]
    public void HasAnyShot_AcceptsTextWithShotHeader()
    {
        const string text = """
        ### 【单元1.1】

        - **镜头编号**: 1.1-1
        - **描述**: 岳沉天格挡反击
        """;

        Assert.True(StoryboardFrameParser.HasAnyShot(text));
    }
}
