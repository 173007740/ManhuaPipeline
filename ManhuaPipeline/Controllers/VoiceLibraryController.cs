using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

/// <summary>
/// 音色库：跨项目复用的参考音频。
/// 这里只管「素材」本身；项目里给角色绑定音色走 VideoController 的 voices/from-library，
/// 绑定时复制一份音频文件，所以删掉库里的音色不会把已绑定项目的音频一起弄坏。
/// </summary>
[ApiController]
[Route("api/voice-library")]
public class VoiceLibraryController : ControllerBase
{
    private static readonly string[] AllowedExt = { ".wav", ".mp3", ".m4a", ".flac", ".ogg", ".aac", ".opus" };

    /// <summary>封面图：显示在音色卡片背景上，可选。限制 8MB 以内。</summary>
    private static readonly string[] AllowedImageExt = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };
    private const long MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>大类：与资产库类型同一套（动漫 / 游戏 / 写实 / 仙侠）。留空 = 通用，不分类。</summary>
    public static readonly string[] Categories = { "动漫", "游戏", "写实", "仙侠" };

    /// <summary>分类值归一化：不在四选一里的按「未设置」处理。</summary>
    private static string? NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;
        var c = category.Trim();
        return Categories.Contains(c) ? c : null;
    }

    private readonly DbService _db;
    private readonly ILogger<VoiceLibraryController> _logger;

    public VoiceLibraryController(DbService db, ILogger<VoiceLibraryController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    private string GetUploadDir()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "voices", "library");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>保存封面图到本地，返回 (是否成功, 相对 URL, 错误信息)。</summary>
    private async Task<(bool Ok, string? Url, string? Err)> TrySaveCover(IFormFile image)
    {
        if (image.Length > MaxImageBytes) return (false, null, "封面图不能超过 8MB");
        var ext = Path.GetExtension(image.FileName).ToLowerInvariant();
        if (!AllowedImageExt.Contains(ext)) return (false, null, "封面图仅支持 jpg / png / webp / gif / bmp");

        var dir = GetUploadDir();
        var fileName = "cover_" + Guid.NewGuid().ToString("N").Substring(0, 12) + ext;
        var savePath = Path.Combine(dir, fileName);
        await using (var fs = new FileStream(savePath, FileMode.Create))
            await image.CopyToAsync(fs);
        return (true, "/uploads/voices/library/" + fileName, null);
    }

    /// <summary>删除本地素材文件（音频 / 封面图共用），删不掉也不影响主流程。</summary>
    private static void TryDeleteLocalFile(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("/uploads/")) return;
        try
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", url.TrimStart('/'));
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch { /* ignore */ }
    }

    /// <summary>单独上传 / 更换封面图（编辑弹窗里选了新图时用，不影响音频）。</summary>
    [HttpPost("{id:int}/cover")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> UploadCover(int id, [FromForm] IFormFile? image)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        try
        {
            var item = _db.GetVoiceLibraryItemById(id);
            if (item == null || item.UserId != uid) return NotFound(new { message = "音色不存在" });
            if (image == null || image.Length == 0) return BadRequest(new { message = "请选择封面图片" });

            var (ok, url, err) = await TrySaveCover(image);
            if (!ok) return BadRequest(new { message = err });

            _db.UpdateVoiceLibraryImage(uid, id, url);
            if (!string.IsNullOrWhiteSpace(item.ImageUrl) && item.ImageUrl != url) TryDeleteLocalFile(item.ImageUrl);
            return Ok(new { imageUrl = url });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VoiceLibrary] 封面图上传失败 Id={Id}", id);
            return StatusCode(500, new { message = "封面图上传失败，请稍后重试" });
        }
    }

    public sealed class UpdateRequest
    {
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Tag { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>音色列表：search 模糊匹配名称/标签/备注；category / tag 精确筛选。</summary>
    [HttpGet]
    public IActionResult GetList([FromQuery] string? search, [FromQuery] string? tag, [FromQuery] string? category)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        try
        {
            var items = _db.GetVoiceLibrary(uid, search, tag, NormalizeCategory(category))
                .Select(v => new
                {
                    id = v.Id,
                    name = v.Name,
                    category = v.Category,
                    tag = v.Tag,
                    note = v.Note,
                    audioUrl = v.AudioUrl,
                    imageUrl = v.ImageUrl,
                    originalFileName = v.OriginalFileName,
                    durationSec = v.DurationSec,
                    createdAt = v.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                    updatedAt = v.UpdatedAt.ToString("yyyy-MM-dd HH:mm")
                })
                .ToList();
            var tags = _db.GetVoiceLibraryTags(uid);
            return Ok(new { items, tags, categories = Categories });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VoiceLibrary] 读取音色库失败 UserId={UserId}", uid);
            return StatusCode(500, new { message = "读取音色库失败，请稍后重试" });
        }
    }

    /// <summary>上传一个新音色（同用户下音色名不能重复）</summary>
    [HttpPost]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] string? name, [FromForm] string? category, [FromForm] string? tag,
                                            [FromForm] string? note, [FromForm] IFormFile? file,
                                            [FromForm] IFormFile? image)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        try
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return BadRequest(new { message = "请填写音色名（如：陈默-沉稳男声）" });
            if (n.Length > 100) return BadRequest(new { message = "音色名最多 100 个字" });
            if (file == null || file.Length == 0) return BadRequest(new { message = "请选择音频文件" });
            if (file.Length > 25 * 1024 * 1024) return BadRequest(new { message = "音频文件不能超过 25MB" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExt.Contains(ext))
                return BadRequest(new { message = "仅支持 wav / mp3 / m4a / flac / ogg / aac / opus 音频" });

            if (_db.VoiceLibraryNameExists(uid, n))
                return Conflict(new { message = "已经存在同名音色「" + n + "」，换个名字或先删掉旧的" });

            var dir = GetUploadDir();
            var fileName = "vlib_" + Guid.NewGuid().ToString("N").Substring(0, 12) + ext;
            var savePath = Path.Combine(dir, fileName);
            await using (var fs = new FileStream(savePath, FileMode.Create))
                await file.CopyToAsync(fs);

            // 封面图可选：验证失败只忽略图片，不连带把音频上传也搞失败
            string? imageUrl = null;
            if (image != null && image.Length > 0)
            {
                var (ok, url, err) = await TrySaveCover(image);
                if (!ok) return BadRequest(new { message = err });
                imageUrl = url;
            }

            var id = _db.InsertVoiceLibraryItem(uid, n, NormalizeCategory(category),
                string.IsNullOrWhiteSpace(tag) ? null : tag.Trim(),
                string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                "/uploads/voices/library/" + fileName, file.FileName, null, imageUrl);

            _logger.LogInformation("[VoiceLibrary] 音色已上传 UserId={UserId} Id={Id} Name={Name}", uid, id, n);
            return Ok(new { id, name = n });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VoiceLibrary] 音色上传失败 UserId={UserId}", uid);
            return StatusCode(500, new { message = "音色上传失败，请稍后重试" });
        }
    }

    /// <summary>改名 / 改标签 / 改备注</summary>
    [HttpPut("{id:int}")]
    public IActionResult Update(int id, [FromBody] UpdateRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        try
        {
            var item = _db.GetVoiceLibraryItemById(id);
            if (item == null || item.UserId != uid) return NotFound(new { message = "音色不存在" });

            var n = (req.Name ?? "").Trim();
            if (n.Length == 0) return BadRequest(new { message = "音色名不能为空" });
            if (n.Length > 100) return BadRequest(new { message = "音色名最多 100 个字" });
            if (_db.VoiceLibraryNameExists(uid, n, id))
                return Conflict(new { message = "已经存在同名音色「" + n + "」" });

            var ok = _db.UpdateVoiceLibraryItem(uid, id, n, NormalizeCategory(req.Category),
                string.IsNullOrWhiteSpace(req.Tag) ? null : req.Tag.Trim(),
                string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim());
            return Ok(new { updated = ok });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VoiceLibrary] 更新音色失败 Id={Id}", id);
            return StatusCode(500, new { message = "保存失败，请稍后重试" });
        }
    }

    /// <summary>删除音色（连同本地音频文件；已绑定到项目的那份是复制件，不受影响）</summary>
    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        try
        {
            var item = _db.GetVoiceLibraryItemById(id);
            if (item == null || item.UserId != uid) return NotFound(new { message = "音色不存在" });

            var ok = _db.DeleteVoiceLibraryItem(uid, id);
            if (ok)
            {
                TryDeleteLocalFile(item.AudioUrl);
                TryDeleteLocalFile(item.ImageUrl);
            }
            return Ok(new { deleted = ok });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VoiceLibrary] 删除音色失败 Id={Id}", id);
            return StatusCode(500, new { message = "删除失败，请稍后重试" });
        }
    }
}
