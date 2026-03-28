using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

[ApiController]
[Route("api/youtube/cookies")]
public class YouTubeCookieController : ControllerBase
{
    private readonly YouTubeCookieService _cookieService;

    public YouTubeCookieController(YouTubeCookieService cookieService)
    {
        _cookieService = cookieService;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UploadCookies([FromForm] IFormFile file)
    {
        if (HttpContext.Items["User"] is not User user || user.Role != UserRole.Admin)
        {
            return StatusCode(403, new { error = "Forbidden. Only admins can upload YouTube cookies." });
        }

        if (file.Length == 0)
        {
            return BadRequest(new { error = "Cookie file is empty" });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            await _cookieService.UploadCookiesAsync(stream, user.Id, HttpContext.RequestAborted);

            return Ok(new { message = "Cookies uploaded successfully" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus()
    {
        var info = await _cookieService.GetCookieInfoAsync(HttpContext.RequestAborted);

        return Ok(new
        {
            hasCookies = info != null,
            uploadedAt = info?.UploadedAt,
            uploadedBy = info?.UploadedBy,
            fileSize = info?.FileSize
        });
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteCookies()
    {
        if (HttpContext.Items["User"] is not User user || user.Role != UserRole.Admin)
        {
            return StatusCode(403, new { error = "Forbidden. Only admins can delete YouTube cookies." });
        }

        await _cookieService.DeleteCookiesAsync(HttpContext.RequestAborted);

        return Ok(new { message = "Cookies deleted" });
    }
}
