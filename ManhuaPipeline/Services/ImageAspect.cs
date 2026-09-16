using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Services;

/// <summary>
/// 出图画幅对齐：把模型返回的图居中裁成 16:9，保证资产图与成片画幅一致。
///
/// 为什么放在本地做：中转/模型对「图生图（images/edits）」的 size 支持不一致 ——
/// 同一个模型文生图回的是 1672x941（16:9），同样传 size=1280x720 的图生图却回了 1214x1295（近方形），
/// size 被直接忽略。与其赌每个模型的脾气，不如拿到图后统一对齐一次。
///
/// 实现依赖 GDI+，因此只支持 Windows：调用方需自行用 <c>OperatingSystem.IsWindows()</c> 守卫，
/// 其它平台直接按模型原图落盘即可（没有对齐也不会影响出图成败）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ImageAspect
{
    /// <summary>目标画幅（16:9），与 <see cref="ImageService.AssetImageSize"/> 对齐。</summary>
    public static readonly double TargetRatio = 16d / 9d;

    /// <summary>比例落在容差内就不动，避免为了零点几的误差把整张图重编码（文生图实测 1672x941 正好是 16:9）。</summary>
    private const double RatioTolerance = 0.01;

    /// <summary>对齐后宽度低于它时按比例放大一次，否则近方形图裁完会又扁又小。</summary>
    private const int MinWidth = 1280;

    /// <summary>
    /// 居中裁剪到 16:9，并按需放大到至少 <see cref="MinWidth"/> 宽。
    /// 已经合乎比例时原样返回（不重编码）；解码/编码出错也原样返回 ——
    /// 画幅对齐只是锦上添花，不该让整次出图失败。
    /// </summary>
    public static (byte[] Data, string Ext) AlignTo16By9(byte[] data, string ext, ILogger? logger = null)
    {
        if (data.Length == 0) return (data, ext);
        // GDI+ 只在 Windows 上可用；其它平台直接放行，出图流程不受影响。
        if (!OperatingSystem.IsWindows()) return (data, ext);

        try
        {
            using var srcStream = new MemoryStream(data, writable: false);
            using var src = Image.FromStream(srcStream);
            if (src.Width <= 0 || src.Height <= 0) return (data, ext);

            var ratio = (double)src.Width / src.Height;
            if (Math.Abs(ratio - TargetRatio) <= RatioTolerance) return (data, ext);

            // 居中裁剪：原图偏胖就裁左右，偏瘦就裁上下
            var cropW = ratio > TargetRatio ? (int)Math.Round(src.Height * TargetRatio) : src.Width;
            var cropH = ratio > TargetRatio ? src.Height : (int)Math.Round(src.Width / TargetRatio);
            cropW = Math.Clamp(cropW, 1, src.Width);
            cropH = Math.Clamp(cropH, 1, src.Height);
            var cropX = (src.Width - cropW) / 2;
            var cropY = (src.Height - cropH) / 2;

            var outW = cropW;
            var outH = cropH;
            if (outW < MinWidth)
            {
                outW = MinWidth;
                outH = (int)Math.Round(MinWidth / TargetRatio);
            }

            using var dst = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.White);
                g.DrawImage(src, new Rectangle(0, 0, outW, outH), cropX, cropY, cropW, cropH, GraphicsUnit.Pixel);
            }

            var target = CodecFor(ext);
            var isJpeg = target.Guid == ImageFormat.Jpeg.Guid;
            using var outStream = new MemoryStream();
            if (isJpeg)
            {
                var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                if (codec == null)
                {
                    dst.Save(outStream, ImageFormat.Jpeg);
                }
                else
                {
                    using var parameters = new EncoderParameters(1);
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality, 95L);
                    dst.Save(outStream, codec, parameters);
                }
            }
            else
            {
                dst.Save(outStream, target);
            }

            var bytes = outStream.ToArray();
            if (bytes.Length == 0) return (data, ext);

            logger?.LogInformation("[ImageAspect] 画幅对齐 {SrcW}x{SrcH} → {DstW}x{DstH}（16:9）",
                src.Width, src.Height, outW, outH);
            return (bytes, isJpeg ? ".jpg" : ".png");
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[ImageAspect] 画幅对齐失败，保留模型原图：{Message}", ex.Message);
            return (data, ext);
        }
    }

    /// <summary>只产出 PNG / JPEG 两种编码，避免「扩展名说 webp、内容却是 png」这类 MIME 错配。</summary>
    private static ImageFormat CodecFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        _ => ImageFormat.Png
    };
}
