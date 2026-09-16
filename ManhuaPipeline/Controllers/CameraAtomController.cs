using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/camera-atom")]
public class CameraAtomController : ControllerBase
{
    private readonly DbService _db;
    public CameraAtomController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    [HttpGet("list")]
    public IActionResult GetList([FromQuery] string? category, [FromQuery] string? search, [FromQuery] string? unitType)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetCameraAtoms(uid, category, search, unitType));
    }

    [HttpPost]
    public IActionResult Create([FromBody] CameraAtomRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "原子名不能为空" });
        if (string.IsNullOrWhiteSpace(req.Category)) return BadRequest(new { message = "分类不能为空" });
        var id = _db.SaveCameraAtom(uid, req.Name.Trim(), req.Category.Trim(), req.Description ?? "", req.Tags);
        return Ok(new { success = true, atomId = id });
    }

    [HttpPut("{atomId}")]
    public IActionResult Update(int atomId, [FromBody] CameraAtomRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "原子名不能为空" });
        if (string.IsNullOrWhiteSpace(req.Category)) return BadRequest(new { message = "分类不能为空" });
        var item = _db.GetCameraAtom(atomId);
        if (item == null || item.UserId != uid) return NotFound(new { message = "原子不存在" });
        _db.UpdateCameraAtom(atomId, uid, req.Name.Trim(), req.Category.Trim(), req.Description ?? "", req.Tags);
        return Ok(new { success = true });
    }

    [HttpDelete("{atomId}")]
    public IActionResult Delete(int atomId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var item = _db.GetCameraAtom(atomId);
        if (item == null || item.UserId != uid) return NotFound(new { message = "原子不存在" });
        _db.DeleteCameraAtom(atomId, uid);
        return Ok(new { message = "删除成功" });
    }
}

public class CameraAtomRequest
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Description { get; set; }
    public string? Tags { get; set; }
}
