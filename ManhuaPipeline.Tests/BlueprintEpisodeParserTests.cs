using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class BlueprintEpisodeParserTests
{
    [Fact]
    public void PreambleTitleLine_IsNotParsedAsEpisodeHeader()
    {
        var text =
            """
            # 《镇世武圣·故事1〈赤焰宗〉第1集〈武圣归来〉》总集蓝图

            > **蓝图总纲**：本集完整讲述岳沉天从被七宗镇杀到绝灵根少年觉醒的过程。

            ---

            ## 第1集: 武圣归来 | 概要

            > 十八年前，镇世武圣岳沉天被七宗以七曜诛圣阵镇杀；十八年后，绝灵根杂役在破庙子时觉醒。

            """;

        var episode = Assert.Single(BlueprintEpisodeParser.Parse(text));

        Assert.Equal("武圣归来", episode.Title);
        Assert.StartsWith("十八年前", episode.Summary);
        Assert.DoesNotContain("概要", episode.Summary);
    }

    [Fact]
    public void TitleWithParenthesizedSummary_ExtractsBoth()
    {
        var text = "第1集: 武圣归来（十八年前镇世武圣被七宗镇杀，十八年后绝灵根少年觉醒。）";

        var episode = Assert.Single(BlueprintEpisodeParser.Parse(text));

        Assert.Equal("武圣归来", episode.Title);
        Assert.StartsWith("十八年前", episode.Summary);
    }

    [Fact]
    public void SummaryOnFollowingLine_IsRead()
    {
        var text =
            """
            ## 第1集: 武圣归来
            概要：十八年前，镇世武圣岳沉天被七宗以七曜诛圣阵镇杀；十八年后，绝灵根杂役在破庙子时觉醒。

            """;

        var episode = Assert.Single(BlueprintEpisodeParser.Parse(text));

        Assert.Equal("武圣归来", episode.Title);
        Assert.StartsWith("十八年前", episode.Summary);
    }

    [Fact]
    public void LegacyTitlePipeSummary_StillWorks()
    {
        var text = "第1集: 武圣归来 | 十八年前，镇世武圣岳沉天被七宗镇杀。";

        var episode = Assert.Single(BlueprintEpisodeParser.Parse(text));

        Assert.Equal("武圣归来", episode.Title);
        Assert.StartsWith("十八年前", episode.Summary);
    }

    [Fact]
    public void FullWidthPipe_And_DuplicateEpisodes_AreHandled()
    {
        var text =
            """
            第1集：武圣归来｜十八年前，镇世武圣被七宗镇杀。
            第1集：武圣归来｜重复行应被忽略。

            """;

        var episode = Assert.Single(BlueprintEpisodeParser.Parse(text));

        Assert.Equal("武圣归来", episode.Title);
        Assert.StartsWith("十八年前", episode.Summary);
    }

    [Fact]
    public void EnglishEpisodeHeader_IsParsed()
    {
        var episode = Assert.Single(BlueprintEpisodeParser.Parse("Episode 1: Martial Saint Returns | The saint was sealed by seven sects."));

        Assert.Equal(1, episode.EpisodeNumber);
        Assert.Equal("Martial Saint Returns", episode.Title);
        Assert.Equal("The saint was sealed by seven sects.", episode.Summary);
    }
}
