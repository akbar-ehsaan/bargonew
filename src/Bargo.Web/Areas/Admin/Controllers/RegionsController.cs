using Bargo.Web.Areas.AdminPanel.Backoffice;
using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.AdminPanel.Controllers;

/// <summary>
/// مدیریت مناطق: استان (فقط خواندنی — از IranGeo می‌آید)، شهر، پایانه، مبادی و مقاصد
/// (RouteLane) و محدوده‌های جغرافیایی.
///
/// شهر حذف نمی‌شود، فقط غیرفعال می‌شود: بارها و حساب‌ها به CityId اشاره دارند و
/// حذفِ شهر یعنی گزارش‌های استانی سوراخ می‌شوند.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Content)]
public class RegionsController(BargoDbContext db, AuditService audit) : Controller
{
    private const int CityPageSize = 50;

    // ------------------------------------------------------------------
    //  استان
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "استان‌ها";
        var rows = await db.Provinces.AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new ProvinceRowVm
            {
                Id = p.ProvinceId,
                Name = p.Name,
                Cities = db.Cities.Count(c => c.ProvinceId == p.ProvinceId),
                Active = db.Cities.Count(c => c.ProvinceId == p.ProvinceId && c.IsActive),
                NoCoords = db.Cities.Count(c => c.ProvinceId == p.ProvinceId && (c.Lat == null || c.Lng == null)),
                Loads = db.Loads.Count(l => l.OriginCity!.ProvinceId == p.ProvinceId)
            })
            .ToListAsync(ct);
        return View(rows);
    }

    // ------------------------------------------------------------------
    //  شهر
    // ------------------------------------------------------------------

    public async Task<IActionResult> Cities(int? provinceId, string? q, string? filter, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "شهرها";
        var term = AdminOps.Term(q);
        filter = filter is "inactive" or "nocoords" ? filter : null;

        var query = db.Cities.AsNoTracking();
        if (provinceId is int pid) query = query.Where(c => c.ProvinceId == pid);
        if (term.Length > 0) query = query.Where(c => c.Name.Contains(term));
        if (filter == "inactive") query = query.Where(c => !c.IsActive);
        if (filter == "nocoords") query = query.Where(c => c.Lat == null || c.Lng == null);

        var rows = query.OrderBy(c => c.Province!.Name).ThenBy(c => c.Name)
            .Select(c => new CityRowVm
            {
                CityId = c.CityId,
                ProvinceId = c.ProvinceId,
                ProvinceName = c.Province!.Name,
                Name = c.Name,
                Lat = c.Lat,
                Lng = c.Lng,
                IsActive = c.IsActive,
                Loads = db.Loads.Count(l => l.OriginCityId == c.CityId)
            });

        return View(new CitiesVm
        {
            Page = await PageVm<CityRowVm>.FromAsync(rows, page, PageLink.For(Request), CityPageSize, ct),
            Provinces = await db.Provinces.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct),
            ProvinceId = provinceId,
            Q = term.Length == 0 ? null : term,
            Filter = filter
        });
    }

    /// <summary>ویرایش درجا (مختصات و فعال بودن) یا افزودن شهر تازه — هر دو با همین اکشن.</summary>
    [HttpPost]
    public async Task<IActionResult> SaveCity(int? id, int? provinceId, string? name, string? lat, string? lng, bool isActive, string? returnUrl, CancellationToken ct)
    {
        IActionResult Back() => AdminOps.Back(this, returnUrl, Url.Action(nameof(Cities), new { provinceId })!);

        var (la, ln, err) = ParseCoords(lat, lng, required: false);
        if (err is not null) { TempData["err"] = err; return Back(); }

        City? row = null;
        if (id is int cid)
        {
            row = await db.Cities.FirstOrDefaultAsync(c => c.CityId == cid, ct);
            if (row is null) return NotFound();
            var before = new { row.Lat, row.Lng, row.IsActive };
            if (before.Lat == la && before.Lng == ln && before.IsActive == isActive) { TempData["ok"] = "چیزی تغییر نکرد."; return Back(); }
            row.Lat = la;
            row.Lng = ln;
            row.IsActive = isActive;
            audit.Add("City", row.CityId, "update", $"شهر «{row.Name}»: مختصات {Coord(la, ln)}، {(isActive ? "فعال" : "غیرفعال")}", new { before, after = new { la, ln, isActive } });
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"شهر «{row.Name}» به‌روز شد.";
            return Back();
        }

        name = (name ?? "").Trim();
        if (name.Length is < 2 or > 40) { TempData["err"] = "نام شهر ۲ تا ۴۰ نویسه باشد."; return Back(); }
        if (provinceId is not int pv || !await db.Provinces.AnyAsync(p => p.ProvinceId == pv, ct)) { TempData["err"] = "استان را انتخاب کنید."; return Back(); }
        if (await db.Cities.AnyAsync(c => c.ProvinceId == pv && c.Name == name, ct)) { TempData["err"] = $"شهر «{name}» در این استان از قبل وجود دارد."; return Back(); }

        row = db.Cities.Add(new City { ProvinceId = pv, Name = name, Lat = la, Lng = ln, IsActive = isActive }).Entity;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("City", row.CityId, "create", $"افزودن شهر «{name}»", new { pv, name, la, ln, isActive });
        TempData["ok"] = $"شهر «{name}» افزوده شد.";
        return Back();
    }

    // ------------------------------------------------------------------
    //  پایانه
    // ------------------------------------------------------------------

    public async Task<IActionResult> Terminals(int? edit, CancellationToken ct)
    {
        ViewData["Title"] = "پایانه‌ها";
        return View(new TerminalsVm
        {
            Rows = await db.Terminals.AsNoTracking()
                .OrderBy(t => t.City!.Province!.Name).ThenBy(t => t.City!.Name).ThenBy(t => t.Name)
                .Select(t => new TerminalRowVm
                {
                    Id = t.TerminalId, CityId = t.CityId, City = t.City!.Name, Province = t.City.Province!.Name,
                    Name = t.Name, Address = t.Address, Lat = t.Lat, Lng = t.Lng, IsActive = t.IsActive
                }).ToListAsync(ct),
            Edit = edit is int id ? await db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == id, ct) : null,
            Cities = await CityOptionsAsync(ct)
        });
    }

    [HttpPost]
    public async Task<IActionResult> SaveTerminal(int? id, int? cityId, string? name, string? address, string? lat, string? lng, bool isActive, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Terminals), new { edit = id });
        name = (name ?? "").Trim();
        if (name.Length is < 2 or > 100) { TempData["err"] = "نام پایانه ۲ تا ۱۰۰ نویسه باشد."; return back; }
        if (cityId is not int cid || !await db.Cities.AnyAsync(c => c.CityId == cid, ct)) { TempData["err"] = "شهر پایانه را انتخاب کنید."; return back; }
        var (la, ln, err) = ParseCoords(lat, lng, required: false);
        if (err is not null) { TempData["err"] = err; return back; }

        Terminal? row = null;
        if (id is int tid)
        {
            row = await db.Terminals.FirstOrDefaultAsync(t => t.TerminalId == tid, ct);
            if (row is null) return NotFound();
        }
        var before = row is null ? null : new { row.CityId, row.Name, row.Address, row.Lat, row.Lng, row.IsActive };
        row ??= db.Terminals.Add(new Terminal()).Entity;
        row.CityId = cid;
        row.Name = name;
        row.Address = AdminOps.Note(address, 500);
        row.Lat = la;
        row.Lng = ln;
        row.IsActive = isActive;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("Terminal", row.TerminalId, before is null ? "create" : "update", $"پایانهٔ «{name}»", new { before, after = new { cid, name, la, ln, isActive } });
        TempData["ok"] = $"پایانهٔ «{name}» ذخیره شد.";
        return RedirectToAction(nameof(Terminals));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteTerminal(int id, CancellationToken ct)
    {
        var row = await db.Terminals.FirstOrDefaultAsync(t => t.TerminalId == id, ct);
        if (row is null) return NotFound();
        db.Terminals.Remove(row);
        audit.Add("Terminal", id, "delete", $"حذف پایانهٔ «{row.Name}»", new { row.CityId, row.Name });
        await db.SaveChangesAsync(ct);
        TempData["ok"] = "پایانه حذف شد.";
        return RedirectToAction(nameof(Terminals));
    }

    // ------------------------------------------------------------------
    //  مبادی و مقاصد
    // ------------------------------------------------------------------

    public async Task<IActionResult> Lanes(int? edit, CancellationToken ct)
    {
        ViewData["Title"] = "مبادی و مقاصد";
        return View(new LanesVm
        {
            Rows = await db.RouteLanes.AsNoTracking()
                .OrderBy(l => l.OriginCity!.Name).ThenBy(l => l.DestCity!.Name)
                .Select(l => new LaneRowVm
                {
                    Id = l.RouteLaneId,
                    From = l.OriginCity!.Name + " (" + l.OriginCity.Province!.Name + ")",
                    To = l.DestCity!.Name + " (" + l.DestCity.Province!.Name + ")",
                    DistanceKm = l.DistanceKm,
                    BaseRatePerTon = l.BaseRatePerTon,
                    IsActive = l.IsActive,
                    Loads = db.Loads.Count(x => x.OriginCityId == l.OriginCityId && x.DestCityId == l.DestCityId)
                }).ToListAsync(ct),
            Edit = edit is int id ? await db.RouteLanes.AsNoTracking().FirstOrDefaultAsync(l => l.RouteLaneId == id, ct) : null,
            Cities = await CityOptionsAsync(ct)
        });
    }

    [HttpPost]
    public async Task<IActionResult> SaveLane(int? id, int? originCityId, int? destCityId, string? distanceKm, string? baseRateToman, bool isActive, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Lanes), new { edit = id });
        if (originCityId is not int o || destCityId is not int d) { TempData["err"] = "مبدا و مقصد را انتخاب کنید."; return back; }
        if (o == d) { TempData["err"] = "مبدا و مقصد نمی‌توانند یک شهر باشند."; return back; }
        var km = BackofficeLookup.ParseDouble(distanceKm);
        if (km is null || km.Value <= 0 || km.Value > 5000) { TempData["err"] = "فاصله را به کیلومتر (بین ۱ و ۵۰۰۰) وارد کنید."; return back; }
        long? rate = null;
        if (!string.IsNullOrWhiteSpace(baseRateToman))
        {
            rate = Fa.ParseToman(baseRateToman);
            if (rate is null || rate.Value < 0) { TempData["err"] = "کرایهٔ مرجع هر تن را به تومان وارد کنید (یا خالی بگذارید)."; return back; }
            if (rate == 0) rate = null;
        }
        var cityCount = await db.Cities.CountAsync(c => c.CityId == o || c.CityId == d, ct);
        if (cityCount != 2) { TempData["err"] = "شهر انتخاب‌شده پیدا نشد."; return back; }
        if (await db.RouteLanes.AnyAsync(l => l.OriginCityId == o && l.DestCityId == d && l.RouteLaneId != (id ?? 0), ct))
        { TempData["err"] = "برای این مبدا و مقصد از قبل مسیری تعریف شده است؛ همان را ویرایش کنید."; return back; }

        RouteLane? row = null;
        if (id is int lid)
        {
            row = await db.RouteLanes.FirstOrDefaultAsync(l => l.RouteLaneId == lid, ct);
            if (row is null) return NotFound();
        }
        var before = row is null ? null : new { row.OriginCityId, row.DestCityId, row.DistanceKm, row.BaseRatePerTon, row.IsActive };
        row ??= db.RouteLanes.Add(new RouteLane()).Entity;
        row.OriginCityId = o;
        row.DestCityId = d;
        row.DistanceKm = Math.Round(km.Value, 1);
        row.BaseRatePerTon = rate;
        row.IsActive = isActive;
        await db.SaveChangesAsync(ct);

        var names = await db.Cities.AsNoTracking().Where(c => c.CityId == o || c.CityId == d).ToDictionaryAsync(c => c.CityId, c => c.Name, ct);
        await audit.LogAsync("RouteLane", row.RouteLaneId, before is null ? "create" : "update",
            $"مسیر {names.GetValueOrDefault(o)} ← {names.GetValueOrDefault(d)}: {Fa.N(row.DistanceKm, 1)} کیلومتر{(rate is long rt ? "، " + Fa.Toman(rt) + " هر تن" : "")}",
            new { before, after = new { o, d, km = row.DistanceKm, rate, isActive } });
        TempData["ok"] = "مسیر ذخیره شد.";
        return RedirectToAction(nameof(Lanes));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteLane(int id, CancellationToken ct)
    {
        var row = await db.RouteLanes.FirstOrDefaultAsync(l => l.RouteLaneId == id, ct);
        if (row is null) return NotFound();
        db.RouteLanes.Remove(row);
        audit.Add("RouteLane", id, "delete", "حذف مسیر", new { row.OriginCityId, row.DestCityId, row.DistanceKm, row.BaseRatePerTon });
        await db.SaveChangesAsync(ct);
        TempData["ok"] = "مسیر حذف شد.";
        return RedirectToAction(nameof(Lanes));
    }

    // ------------------------------------------------------------------
    //  محدوده‌های جغرافیایی
    // ------------------------------------------------------------------

    public async Task<IActionResult> Geofences(int? edit, CancellationToken ct)
    {
        ViewData["Title"] = "محدوده‌های جغرافیایی";
        var items = await db.Geofences.AsNoTracking().OrderBy(g => g.Kind).ThenBy(g => g.Title).ToListAsync(ct);
        return View(new GeofencesVm
        {
            Items = items,
            Edit = edit is int id ? items.FirstOrDefault(g => g.GeofenceId == id) : null
        });
    }

    [HttpPost]
    public async Task<IActionResult> SaveGeofence(int? id, string? title, string? kind, string? lat, string? lng, string? radiusKm, bool isActive, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Geofences), new { edit = id });
        title = (title ?? "").Trim();
        kind = GeofenceKind.All.Contains(kind ?? "") ? kind! : "custom";
        if (title.Length is < 2 or > 100) { TempData["err"] = "عنوان محدوده ۲ تا ۱۰۰ نویسه باشد."; return back; }
        var (la, ln, err) = ParseCoords(lat, lng, required: true);
        if (err is not null) { TempData["err"] = err; return back; }
        var radius = BackofficeLookup.ParseDouble(radiusKm);
        if (radius is null || radius.Value <= 0 || radius.Value > 500) { TempData["err"] = "شعاع را به کیلومتر (بین ۰٫۱ و ۵۰۰) وارد کنید."; return back; }

        Geofence? row = null;
        if (id is int gid)
        {
            row = await db.Geofences.FirstOrDefaultAsync(g => g.GeofenceId == gid, ct);
            if (row is null) return NotFound();
        }
        var before = row is null ? null : new { row.Title, row.Kind, row.CenterLat, row.CenterLng, row.RadiusKm, row.IsActive };
        row ??= db.Geofences.Add(new Geofence()).Entity;
        row.Title = title;
        row.Kind = kind;
        row.CenterLat = la!.Value;
        row.CenterLng = ln!.Value;
        row.RadiusKm = Math.Round(radius.Value, 2);
        row.IsActive = isActive;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("Geofence", row.GeofenceId, before is null ? "create" : "update",
            $"{GeofenceKind.Label(kind)} «{title}»: شعاع {Fa.N(row.RadiusKm, 1)} کیلومتر", new { before, after = new { title, kind, la, ln, radius = row.RadiusKm, isActive } });
        TempData["ok"] = $"محدودهٔ «{title}» ذخیره شد.";
        return RedirectToAction(nameof(Geofences));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteGeofence(int id, CancellationToken ct)
    {
        var row = await db.Geofences.FirstOrDefaultAsync(g => g.GeofenceId == id, ct);
        if (row is null) return NotFound();
        db.Geofences.Remove(row);
        audit.Add("Geofence", id, "delete", $"حذف محدودهٔ «{row.Title}»", new { row.Title, row.Kind, row.CenterLat, row.CenterLng, row.RadiusKm });
        await db.SaveChangesAsync(ct);
        TempData["ok"] = "محدوده حذف شد.";
        return RedirectToAction(nameof(Geofences));
    }

    // ------------------------------------------------------------------
    //  کمکی
    // ------------------------------------------------------------------

    private Task<List<CityOption>> CityOptionsAsync(CancellationToken ct) =>
        db.Cities.AsNoTracking().Where(c => c.IsActive)
            .OrderBy(c => c.Province!.Name).ThenBy(c => c.Name)
            .Select(c => new CityOption(c.CityId, c.Name, c.Province!.Name))
            .ToListAsync(ct);

    /// <summary>
    /// عرض و طول جغرافیایی؛ هر دو یا هیچ‌کدام. محدودهٔ ایران تقریبی است (۲۴ تا ۴۰ شمالی،
    /// ۴۴ تا ۶۴ شرقی) تا جابه‌جا نوشتنِ عرض و طول همان لحظه گرفته شود.
    /// </summary>
    private static (double? Lat, double? Lng, string? Error) ParseCoords(string? lat, string? lng, bool required)
    {
        var la = BackofficeLookup.ParseDouble(lat);
        var ln = BackofficeLookup.ParseDouble(lng);
        var hasLat = !string.IsNullOrWhiteSpace(lat);
        var hasLng = !string.IsNullOrWhiteSpace(lng);
        if (!hasLat && !hasLng) return required ? (null, null, "مختصات مرکز را وارد کنید.") : (null, null, null);
        if (la is null || ln is null || hasLat != hasLng) return (null, null, "عرض و طول جغرافیایی هر دو باید عدد باشند (مثلاً ۳۵٫۶۸۹۲ و ۵۱٫۳۸۹۰).");
        if (la is < 24 or > 40 || ln is < 44 or > 64) return (null, null, "مختصات بیرون از محدودهٔ ایران است؛ عرض ۲۴ تا ۴۰ و طول ۴۴ تا ۶۴.");
        return (Math.Round(la.Value, 6), Math.Round(ln.Value, 6), null);
    }

    private static string Coord(double? la, double? ln) => la is null || ln is null ? "—" : $"{BackofficeLookup.Invariant(la)}, {BackofficeLookup.Invariant(ln)}";
}
