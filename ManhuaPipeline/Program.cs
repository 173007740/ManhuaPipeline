using ManhuaPipeline.Services;
using ManhuaPipeline.Services.Director;
using ManhuaPipeline.Services.Combat;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 500L * 1024 * 1024);
var _urls = builder.Configuration["Urls"];
if (!string.IsNullOrEmpty(_urls))
    builder.WebHost.UseUrls(_urls);
builder.Services.AddControllers(options => options.Filters.Add<SessionAuthorizeFilter>());
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(24);
    options.Cookie.Name = ".ManhuaPipeline.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
// LLM 单次请求超时。qwen3 系列默认开启思考链，分集细化（Stage 4）要输出上万 token，
// 思考链 + 长输出叠加实测会超过 10 分钟，原来的 600 秒会在模型还没返回时被 HttpClient 掐断
// （表现为 TaskCanceledException: "...HttpClient.Timeout of 600 seconds elapsing"）。
builder.Services.AddHttpClient<LLMService>(c => c.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddHttpClient<AgentService>();
builder.Services.AddHttpClient<VideoService>(c => c.Timeout = TimeSpan.FromMinutes(10));
// 中转出图：单张图几十秒到几分钟（高峰排队更久），并且成功响应里可能给的是图片 URL，
// 需要再发一次 GET 把图拉回来，所以超时给到 10 分钟。
builder.Services.AddHttpClient<ImageService>(c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient<ComfyService>(c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient<EnhanceService>(c => c.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddHostedService<VideoPollingService>();
// 资产卡出图后台队列：入队后由后台执行，关页面/刷新也会跑完（见 Database\Upgrade_资产出图任务队列.sql）
builder.Services.AddHostedService<AssetImageQueueService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<DbService>();
// 视频状态 SSE 订阅中心：后台巡检发现状态变化后主动推给页面，替代前端每个镜头一条 5 秒轮询链
builder.Services.AddSingleton<VideoEventHub>();
builder.Services.AddScoped<AssetImageRunner>();
builder.Services.AddScoped<StoryboardPlanningService>();
builder.Services.AddScoped<CombatGrammarEngine>();
builder.Services.AddScoped<DirectorService>();
builder.Services.AddScoped<DirectorSemanticValidator>();
builder.Services.AddScoped<StoryboardAutoFixer>();
// L2 连续性层：Stage 4 前抽取/复用六类连续性表并注入（见 Database\Upgrade_连续性表.sql）
builder.Services.AddScoped<ContinuityExtractionService>();
// L5 后期叠加清单：派生自阶段 9 提示词，只读
builder.Services.AddScoped<PostOverlayPlanService>();
// L3 关键帧层：每剧情节点一张（8-16 张/集），人工触发生成（见 Database\Upgrade_关键帧层.sql）
builder.Services.AddScoped<KeyframePlanService>();

var app = builder.Build();

// wwwroot 的真实绝对路径：上传落盘、参考图读取、资产库展示磁盘路径都从这里取。
// 用 WebRootPath 而不是 Directory.GetCurrentDirectory()：后者随「怎么启动的」而变
// （VS 调试指向 bin\Debug\net9.0，从仓库根 dotnet run 指向仓库根），会把 wwwroot 算到错误位置。
AppPaths.WebRoot = app.Environment.WebRootPath
    ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");

DatabaseSchemaValidator.Validate(app.Configuration);

// 静态资源缓存策略（原来完全没有 Cache-Control，浏览器只能靠启发式缓存，同一张参考图
// 会被反复整份下载）：
//   • /uploads/** —— 文件名一律带 GUID / 时间戳，重新出图必然换新名，旧文件永不复用，
//     所以可以放心长时间强缓存。参考图平均 1.5MB，这一条能把重复下载直接砍掉。
//   • 其它（html/js/css）—— no-cache：每次刷新都带 ETag 回源校验（命中即 304，几十字节），
//     保证页面前端改动刷新即生效，不会拿旧缓存。
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var isUpload = ctx.Context.Request.Path.StartsWithSegments("/uploads");
        ctx.Context.Response.Headers.CacheControl = isUpload
            ? "public, max-age=604800"
            : "no-cache";
    }
});
app.UseSession();
app.MapControllers();

// 缩略图：列表里参考图只显示 40×40，却要下载平均 1.5MB（最大 13.8MB）的原图，
// 39 行 × 最多 9 个槽位就是几百 MB。这里按需生成小图并落盘缓存，之后直接命中缓存文件。
var thumbGate = new SemaphoreSlim(4); // 限制并发解码，避免大量 GDI+ 同时解码把 CPU/内存打满
app.MapGet("/api/media/thumb", async (HttpContext ctx, string? path, int? w) =>
{
    var width = Math.Clamp(w.GetValueOrDefault(64), 16, 512);
    if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest();

    var uploadsRoot = Path.GetFullPath(Path.Combine(AppPaths.Root, "uploads"));
    var full = Path.GetFullPath(Path.Combine(AppPaths.Root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
    if (!full.StartsWith(uploadsRoot, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
    if (!File.Exists(full)) return Results.NotFound();

    var ext = Path.GetExtension(full).ToLowerInvariant();
    if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")) return Results.BadRequest();

    var thumbDir = Path.Combine(uploadsRoot, ".thumbs");
    Directory.CreateDirectory(thumbDir);
    var info = new FileInfo(full);
    var key = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
        System.Text.Encoding.UTF8.GetBytes(full + "|" + width + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length)))[..20];
    var thumbPath = Path.Combine(thumbDir, key + ".jpg");

    if (!File.Exists(thumbPath))
    {
        // GDI+ 只在 Windows 6.1+ 上可用；其它平台直接回退原图（页面照常显示，只是不省流量）
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return Results.Redirect(path);
        await thumbGate.WaitAsync();
        try
        {
            if (!File.Exists(thumbPath))
            {
                try
                {
                    // 先写到临时文件再改名：避免两个并发请求一个写一半、另一个直接读到半张图
                    var tmp = thumbPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                    using (var img = System.Drawing.Image.FromFile(full))
                    {
                        var tw = Math.Max(1, Math.Min(width, img.Width));
                        var th = Math.Max(1, (int)Math.Round(img.Height * (tw / (double)img.Width)));
                        using var bmp = new System.Drawing.Bitmap(tw, th);
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.DrawImage(img, 0, 0, tw, th);
                        }
                        System.Drawing.Imaging.ImageCodecInfo? jpeg = null;
                        var encoders = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders();
                        for (var i = 0; i < encoders.Length; i++)
                            if (encoders[i].FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid) { jpeg = encoders[i]; break; }
                        if (jpeg is null) return Results.Redirect(path);
                        using var ep = new System.Drawing.Imaging.EncoderParameters(1);
                        ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                        bmp.Save(tmp, jpeg, ep);
                    }
                    File.Move(tmp, thumbPath, overwrite: true);
                }
                catch
                {
                    // 解码失败（非 Windows / 文件损坏 / 格式特殊）→ 退回原图，保证页面不断图
                    return Results.Redirect(path);
                }
            }
        }
        finally { thumbGate.Release(); }
    }

    ctx.Response.Headers.CacheControl = "public, max-age=604800";
    return Results.File(thumbPath, "image/jpeg");
});

// SPA 路由回退
app.MapFallbackToFile("index.html");

app.Run();
