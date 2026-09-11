using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Controllers.Api;

/// <summary>
/// دریافت موقعیت راننده. دو راه احراز: کوکی پنل راننده (مرورگر/WebView) یا کلید
/// TrackKey (سرویس پس‌زمینهٔ اپ اندروید که کوکی ندارد).
/// </summary>
[ApiController]
[Route("api/track")]
[IgnoreAntiforgeryToken]
public class TrackController(BargoDbContext db, TripFlow flow, CurrentUser me, SettingsService settings) : ControllerBase
{
    public sealed record PointDto(string? Key, double Lat, double Lng, double? Speed, double? Accuracy);

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] PointDto p, CancellationToken ct)
    {
        Driver? driver = null;
        if (me.IsAuthenticated && me.Role == Roles.Driver)
            driver = await db.Drivers.FirstOrDefaultAsync(d => d.DriverId == me.Id, ct);
        else if (!string.IsNullOrWhiteSpace(p.Key))
            driver = await db.Drivers.FirstOrDefaultAsync(d => d.TrackKey == p.Key, ct);
        if (driver is null) return Unauthorized();

        try
        {
            await flow.RecordPointAsync(driver, p.Lat, p.Lng, p.Speed, p.Accuracy, ct);
        }
        catch (UserError e)
        {
            return BadRequest(new { error = e.Message });
        }
        return Ok(new { ok = true, intervalMin = await settings.GetIntAsync(SettingsService.Keys.TrackIntervalMin, ct) });
    }

    [HttpGet("config")]
    [Authorize(Roles = Roles.Driver)]
    public async Task<IActionResult> Config(CancellationToken ct)
    {
        var key = await db.Drivers.Where(d => d.DriverId == me.Id).Select(d => d.TrackKey).FirstOrDefaultAsync(ct);
        return Ok(new { key, intervalMin = await settings.GetIntAsync(SettingsService.Keys.TrackIntervalMin, ct) });
    }
}
