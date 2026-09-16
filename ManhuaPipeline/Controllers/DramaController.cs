using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DramaController : ControllerBase
{
    private readonly DbService _db;
    private readonly ILogger<DramaController> _logger;
    public DramaController(DbService db, ILogger<DramaController> logger) { _db = db; _logger = logger; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    [HttpGet]
    public IActionResult GetAll()
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetDramas(uid));
    }

    [HttpGet("{id}")]
    public IActionResult Get(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var d = _db.GetDrama(id, uid);
        if (d == null) return NotFound();
        return Ok(d);
    }

    [HttpPost]
    public IActionResult Create([FromBody] CreateDramaRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Title)) return BadRequest(new { message = "请输入漫剧名称" });
        var id = _db.CreateDrama(uid, req.Title, req.Description);
        return Ok(new { dramaId = id, message = "创建成功" });
    }

    [HttpPut("{id}")]
    public IActionResult Update(int id, [FromBody] CreateDramaRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.UpdateDrama(id, uid, req.Title, req.Description, null)) return NotFound(new { message = "漫剧不存在" });
        return Ok(new { message = "更新成功" });
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var drama = _db.GetDrama(id, uid);
        if (drama == null) return NotFound(new { message = "漫剧不存在" });
        var projects = _db.GetProjectsByDrama(id, uid);
        if (!_db.DeleteDrama(id, uid)) return NotFound(new { message = "漫剧不存在" });
        try
        {
            UploadStorage.DeleteUploadFile(drama.CoverImage, "/uploads/dramas/");
            foreach (var p in projects)
                UploadStorage.DeleteUploadFile(p.CoverImage, "/uploads/projects/");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cover files for drama {DramaId}", id);
        }
        return Ok(new { message = "删除成功" });
    }

    [HttpGet("{id}/projects")]
    public IActionResult GetProjects(int id)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetProjectsByDrama(id, uid));
    }

    [HttpPost("{id}/cover")]
    [RequestSizeLimit(UploadValidation.MaxProfileImageBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadValidation.MaxProfileImageBytes)]
    public async Task<IActionResult> UploadCover(int id, IFormFile file)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var drama = _db.GetDrama(id, uid);
        if (drama == null) return NotFound(new { message = "漫剧不存在" });
        if (!UploadValidation.TryValidateImage(file, UploadValidation.MaxProfileImageBytes, out var extension, out var error))
            return BadRequest(new { message = error });
        var fileName = $"drama_{id}_{DateTime.Now.Ticks}{extension}";
        var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "dramas");
        Directory.CreateDirectory(uploadDir);
        var filePath = Path.Combine(uploadDir, fileName);
        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }
        var coverUrl = $"/uploads/dramas/{fileName}";
        _db.UpdateDramaCover(id, uid, coverUrl);
        if (!string.Equals(drama.CoverImage, coverUrl, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                UploadStorage.DeleteUploadFile(drama.CoverImage, "/uploads/dramas/");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete old cover for drama {DramaId}", id);
            }
        }
        return Ok(new { coverUrl = coverUrl, message = "上传成功" });
    }
}

public class CreateDramaRequest
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
}
