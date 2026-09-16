using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

/// <summary>
/// 文生图（资产卡出图）的纯逻辑测试：地址归一、提示词拼装、响应解析、格式识别。
/// 这几段是最容易被中转站差异打脸的地方，所以单独锁住。
/// </summary>
public class ImageServiceTests
{
    // ---------- 地址归一 ----------

    [Theory]
    [InlineData("https://relay.example.com/v1", "images/generations", "https://relay.example.com/v1/images/generations")]
    [InlineData("https://relay.example.com/v1/", "images/generations", "https://relay.example.com/v1/images/generations")]
    [InlineData("https://relay.example.com", "images/generations", "https://relay.example.com/v1/images/generations")]
    [InlineData("https://relay.example.com/v1/chat/completions", "images/generations", "https://relay.example.com/v1/images/generations")]
    [InlineData("https://relay.example.com/v1/images/generations", "images/generations", "https://relay.example.com/v1/images/generations")]
    [InlineData("https://relay.example.com/openai/v1/chat/completions", "chat/completions", "https://relay.example.com/openai/v1/chat/completions")]
    public void BuildEndpoint_NormalizesCommonUrlShapes(string input, string path, string expected)
        => Assert.Equal(expected, ImageService.BuildEndpoint(input, path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relay.example.com/v1")]
    public void BuildEndpoint_ReturnsEmptyForUnusableUrl(string? input)
        => Assert.Equal("", ImageService.BuildEndpoint(input, "images/generations"));

    // ---------- 尺寸 ----------

    [Fact]
    public void DefaultSizeFor_AlwaysUsesWide16By9()
    {
        // 四类资产统一 16:9，和成片画幅对齐（characters/props 原先单独用 1024x1024，已随画幅对齐一并取消）
        foreach (var category in new[] { "characters", "props", "environments", "effects" })
            Assert.Equal("1280x720", ImageService.DefaultSizeFor(category));
    }

    [Fact]
    public void ComposeAssetImagePrompt_AlwaysCarriesAspectHint()
    {
        // 画幅约束是「提示词引导 + 本地裁切兜底」的第一层，不能被后续修改悄悄丢掉
        var prompt = ImageService.ComposeAssetImagePrompt("角色设定正文", null, null, null, null);

        Assert.Contains("16:9", prompt);
        Assert.Contains("横向宽幅", prompt);
    }

    [Fact]
    public void BuildAssetImagePrompt_AlwaysCarriesAspectHint()
    {
        var prompt = ImageService.BuildAssetImagePrompt("props", "断魂刀", null, null, null, null);

        Assert.Contains("16:9", prompt);
        Assert.Contains("横向宽幅", prompt);
    }

    [Theory]
    [InlineData("1024x1024", true)]
    [InlineData("1280X720", true)]
    [InlineData("1024*1536", true)]
    [InlineData("1024", false)]
    [InlineData("big", false)]
    [InlineData(null, false)]
    public void IsValidSize_OnlyAcceptsWidthHeightPair(string? size, bool expected)
        => Assert.Equal(expected, ImageService.IsValidSize(size));

    // ---------- 描述清洗与提示词拼装 ----------

    [Fact]
    public void CleanDescription_DropsAliasLineAndLabelPrefixes()
    {
        var cleaned = ImageService.CleanDescription("角色别名：岳沉天、老岳\n角色描述：黑袍白发，左眉有一道旧疤。");

        Assert.DoesNotContain("别名", cleaned);
        Assert.DoesNotContain("老岳", cleaned);
        Assert.Contains("黑袍白发", cleaned);
        Assert.Contains("旧疤", cleaned);
    }

    [Fact]
    public void BuildAssetImagePrompt_KeepsVisualInfoAndStyle_ButNotAlias()
    {
        var prompt = ImageService.BuildAssetImagePrompt(
            "characters", "岳沉天", "角色别名：老岳\n角色描述：黑袍白发，眉有旧疤", "觉醒态",
            "写实3D动画电影，4K", "背景加一轮血月");

        Assert.Contains("岳沉天", prompt);
        Assert.Contains("黑袍白发", prompt);
        Assert.Contains("觉醒态", prompt);
        Assert.Contains("写实3D动画电影，4K", prompt);
        Assert.Contains("血月", prompt);
        Assert.DoesNotContain("老岳", prompt);
    }

    [Fact]
    public void BuildAssetImagePrompt_WorksWithEmptyDescriptionAndNoStyle()
    {
        var prompt = ImageService.BuildAssetImagePrompt("props", "断魂刀", null, null, null, null);

        Assert.Contains("断魂刀", prompt);
        Assert.Contains("道具", prompt);
        Assert.DoesNotContain("画面风格", prompt);
    }

    // ---------- /images/generations 响应解析 ----------

    [Fact]
    public void ParseImageData_ReadsUrl()
    {
        var (url, b64) = ImageService.ParseImageData("""{"created":1,"data":[{"url":"https://cdn.example.com/a.png"}]}""");

        Assert.Equal("https://cdn.example.com/a.png", url);
        Assert.Null(b64);
    }

    [Fact]
    public void ParseImageData_ReadsBase64()
    {
        var (url, b64) = ImageService.ParseImageData("""{"data":[{"b64_json":"AAAB"}]}""");

        Assert.Null(url);
        Assert.Equal("AAAB", b64);
    }

    [Fact]
    public void ParseImageData_TreatsDataUriInUrlAsBase64()
    {
        var (url, b64) = ImageService.ParseImageData("""{"data":[{"url":"data:image/png;base64,AAAB"}]}""");

        Assert.Null(url);
        Assert.Equal("AAAB", b64);
    }

    [Fact]
    public void ParseImageData_ReturnsNullsWhenResponseHasNoImage()
    {
        var (url, b64) = ImageService.ParseImageData("""{"error":{"message":"model not found"}}""");

        Assert.Null(url);
        Assert.Null(b64);
    }

    // ---------- /chat/completions 兜底解析 ----------

    [Fact]
    public void ParseChatImage_ReadsImagesArray()
    {
        var (url, _) = ImageService.ParseChatImage(
            """{"choices":[{"message":{"images":[{"image_url":{"url":"https://cdn.example.com/b.png"}}]}}]}""");

        Assert.Equal("https://cdn.example.com/b.png", url);
    }

    [Fact]
    public void ParseChatImage_ReadsMarkdownImageFromContent()
    {
        var (url, _) = ImageService.ParseChatImage(
            """{"choices":[{"message":{"content":"这是你要的图：![](https://cdn.example.com/c.png)"}}]}""");

        Assert.Equal("https://cdn.example.com/c.png", url);
    }

    [Fact]
    public void ParseChatImage_ReadsBase64FromContent()
    {
        var (url, b64) = ImageService.ParseChatImage(
            """{"choices":[{"message":{"content":"![img](data:image/jpeg;base64,QUJD)"}}]}""");

        Assert.Null(url);
        Assert.Equal("QUJD", b64);
    }

    [Fact]
    public void ParseChatImage_ReturnsNullsForPlainTextAnswer()
    {
        var (url, b64) = ImageService.ParseChatImage(
            """{"choices":[{"message":{"content":"抱歉，我无法生成图片。"}}]}""");

        Assert.Null(url);
        Assert.Null(b64);
    }

    // ---------- 格式识别与文件名净化 ----------

    [Fact]
    public void DetectExt_UsesMagicBytesBeforeContentType()
    {
        Assert.Equal(".png", ImageService.DetectExt(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        Assert.Equal(".jpg", ImageService.DetectExt(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Equal(".jpg", ImageService.DetectExt(new byte[] { 0x00, 0x01 }, "image/jpeg"));
        Assert.Equal(".png", ImageService.DetectExt(new byte[] { 0x00, 0x01 }));
    }

    [Theory]
    [InlineData("岳沉天", "岳沉天")]
    [InlineData("角色:测试", "角色_测试")]
    [InlineData("", "asset")]
    [InlineData("   ", "asset")]
    public void SanitizeName_KeepsChineseAndFallsBackToAsset(string input, string expected)
        => Assert.Equal(expected, ImageService.SanitizeName(input));
}
