using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

/// <summary>
/// L5 后期叠加识别器：分类命中、去重、误伤防护。
/// 触发词表是两个下游（阶段 9 守卫 + 项目页清单）的唯一来源，所以这里同时守住「措辞不漂移」。
/// </summary>
public class PostOverlayAnalyzerTests
{
    [Fact]
    public void Detect_SmsCue_ReturnsMobileCategoryWithCue()
    {
        var hits = PostOverlayAnalyzer.Detect("少年低头看手机，屏幕上跳出短信：宗门已灭。");

        Assert.Contains(hits, h => h.Category == "手机短信" && h.Cue == "短信");
    }

    [Fact]
    public void Detect_SameCategoryTwice_ReturnsOnlyOnce()
    {
        var hits = PostOverlayAnalyzer.Detect("先是一条短信，随后又是一条短信弹出。");

        Assert.Single(hits, h => h.Category == "手机短信");
    }

    [Fact]
    public void Detect_MultipleCategories_AreAllReported()
    {
        var hits = PostOverlayAnalyzer.Detect("他抓起桌上的密信，墙上的监控画面同时亮起。");

        Assert.Contains(hits, h => h.Category == "文件信件");
        Assert.Contains(hits, h => h.Category == "地图监控");
        Assert.Equal("文件信件 / 地图监控", PostOverlayAnalyzer.Describe(hits));
    }

    [Theory]
    [InlineData("我相信你说的每一句话。")]
    [InlineData("这条信息他已经反复确认过。")]
    [InlineData("信封里的东西不重要。")] // 「信封」不在触发词表：只认信件/密信/信纸/书信
    public void Detect_LookalikeWords_DoNotTrigger(string text)
    {
        Assert.False(PostOverlayAnalyzer.HasOverlay(text));
    }

    [Theory]
    [InlineData("镜头缓慢推近，桌上摊开的卷宗被风吹起一角。", "文件信件")]
    [InlineData("城门告示贴着悬赏令，围观者窃窃私语。", "公告榜单")]
    [InlineData("系统面板在眼前展开，进度条开始跳动。", "屏幕界面")]
    [InlineData("电视里正在播报头条新闻。", "新闻推送")]
    public void Detect_EachCategoryCue_IsRecognized(string text, string expectedCategory)
    {
        Assert.Contains(PostOverlayAnalyzer.Detect(text), h => h.Category == expectedCategory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("两人在雨中对视，谁也没有说话。")]
    public void Detect_NoOverlayContent_ReturnsEmpty(string? text)
    {
        Assert.Empty(PostOverlayAnalyzer.Detect(text));
        Assert.False(PostOverlayAnalyzer.HasOverlay(text));
        Assert.Equal("", PostOverlayAnalyzer.Describe(PostOverlayAnalyzer.Detect(text)));
    }
}
