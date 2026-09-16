using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/works")]
public class WorksController : ControllerBase
{
    private readonly DbService _db;
    public WorksController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;
    private string GetUploadDir()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "work");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteLocalFile(string? path) => UploadStorage.DeleteUploadFile(path, "/uploads/work/");

    private static bool IsSafeWorkUrl(string? url) => UploadStorage.IsSafeUploadUrl(url, "/uploads/work/");

    [HttpGet("list")]
    public IActionResult GetList()
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetWorks(uid));
    }

    [HttpGet("{workId}")]
    public IActionResult GetDetail(int workId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var w = _db.GetWorkDetail(workId);
        if (w == null) return NotFound();
        if (w.UserId != uid) return Unauthorized();
        return Ok(w);
    }

    [HttpPost("create")]
    [RequestSizeLimit(500L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 500L * 1024 * 1024)]
    public async Task<IActionResult> Create([FromForm] string title, [FromForm] string? description, [FromForm] string? workUrl, IFormFile? cover, IFormFile? video)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(title)) return BadRequest(new { message = "作品标题不能为空" });

        string? coverPath = null;
        if (cover != null && cover.Length > 0)
        {
            if (!UploadValidation.TryValidateImage(cover, UploadValidation.MaxProfileImageBytes, out _, out var error))
                return BadRequest(new { message = error });
            coverPath = await SaveFile(cover, GetUploadDir());
        }

        string? videoPath = null;
        if (video != null && video.Length > 0)
        {
            var vExt = Path.GetExtension(video.FileName).ToLower();
            if (vExt != ".mp4" && vExt != ".webm")
                return BadRequest(new { message = "视频仅支持 mp4/webm 格式" });
            if (!UploadValidation.TryValidateVideo(video, UploadValidation.MaxVideoBytes, out _, out var error))
                return BadRequest(new { message = error });
            videoPath = await SaveFile(video, GetUploadDir());
        }

        if (string.IsNullOrEmpty(videoPath) && !IsSafeWorkUrl(workUrl))
            return BadRequest(new { message = "作品链接仅支持 http/https 或 /uploads/work/ 本地上传路径" });
        var id = _db.SaveWork(uid, title.Trim(), description ?? "", coverPath, videoPath ?? workUrl);
        return Ok(new { workId = id, message = "上传成功" });
    }

    [HttpPut("{workId}")]
    [RequestSizeLimit(500L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 500L * 1024 * 1024)]
    public async Task<IActionResult> Update(int workId, [FromForm] string title, [FromForm] string? description, [FromForm] string? workUrl, IFormFile? cover, IFormFile? video)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var work = _db.GetWorkById(workId);
        if (work == null) return NotFound();
        if (work.UserId != uid) return Unauthorized();
        if (string.IsNullOrWhiteSpace(title)) return BadRequest(new { message = "作品标题不能为空" });

        string? coverPath = null;
        if (cover != null && cover.Length > 0)
        {
            if (!UploadValidation.TryValidateImage(cover, UploadValidation.MaxProfileImageBytes, out _, out var error))
                return BadRequest(new { message = error });
            coverPath = await SaveFile(cover, GetUploadDir());
            DeleteLocalFile(work.CoverImage);
        }

        string? videoPath = null;
        if (video != null && video.Length > 0)
        {
            var vExt = Path.GetExtension(video.FileName).ToLower();
            if (vExt != ".mp4" && vExt != ".webm")
                return BadRequest(new { message = "视频仅支持 mp4/webm 格式" });
            if (!UploadValidation.TryValidateVideo(video, UploadValidation.MaxVideoBytes, out _, out var error))
                return BadRequest(new { message = error });
            videoPath = await SaveFile(video, GetUploadDir());
            DeleteLocalFile(work.WorkUrl);
        }

        if (string.IsNullOrEmpty(videoPath) && !IsSafeWorkUrl(workUrl))
            return BadRequest(new { message = "作品链接仅支持 http/https 或 /uploads/work/ 本地上传路径" });
        _db.UpdateWork(workId, uid, title.Trim(), description ?? "", coverPath, videoPath ?? workUrl);
        return Ok(new { message = "修改成功" });
    }

    [HttpDelete("{workId}")]
    public IActionResult Delete(int workId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var work = _db.GetWorkById(workId);
        if (work == null) return NotFound();
        if (work.UserId != uid) return Unauthorized();

        DeleteLocalFile(work.CoverImage);
        DeleteLocalFile(work.WorkUrl);
        _db.DeleteWork(workId, uid);
        return Ok(new { message = "删除成功" });
    }

    private static async Task<string> SaveFile(IFormFile file, string dir)
    {
        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName).ToLower()}";
        var filePath = Path.Combine(dir, fileName);
        using (var stream = new FileStream(filePath, FileMode.Create))
            await file.CopyToAsync(stream);
        return $"/uploads/work/{fileName}";
    }
}
