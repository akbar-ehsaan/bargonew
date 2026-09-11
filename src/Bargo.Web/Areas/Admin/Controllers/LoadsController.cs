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
/// «مدیریت بارها» — همهٔ بارهای ثبت‌شده، به تفکیک مرحله، بارهای گزارش‌شده و پروندهٔ هر بار.
///
/// مدیر فقط بارِ قطعی‌نشده را از اینجا لغو می‌کند (<see cref="TripFlow.CancelLoadAsync"/>)؛
/// بارِ قطعی‌شده روی خودروست و لغوش از پروندهٔ سفر می‌گذرد تا استرداد و اعلان‌ها
/// از قلم نیفتد. وضعیت بار هیچ‌جا مستقیم نوشته نمی‌شود.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Operations)]
public class LoadsController(BargoDbContext db, TripFlow flow, NotificationService notify, AuditService audit, CurrentUser me) : Controller
{
    /// <summary>سربرگ‌های فهرست — همان کلیدهایی که منو با ?status= می‌فرستد.</summary>
    public static readonly (string Key, string Label, string Icon)[] Tabs =
    [
        ("waiting", "در انتظار", "bi-hourglass"),
        ("active", "فعال", "bi-lightning"),
        ("transit", "در حال حمل", "bi-truck"),
        ("completed", "تکمیل‌شده", "bi-check2-all"),
        ("cancelled", "لغوشده", "bi-x-octagon"),
    ];

    private static IQueryable<Load> Filter(IQueryable<Load> q, string? status) => status switch
    {
        "waiting" => q.Where(l => LoadStatus.Market.Contains(l.Status)),
        // قطعی‌شده ولی هنوز راننده در راه نیست (تخصیص، قبول، یا سفرِ لغوشده پس از بارگیری)
        "active" => q.Where(l => l.Status == LoadStatus.Booked && !l.Trips.Any(t => TripStatus.Live.Contains(t.Status))),
        "transit" => q.Where(l => l.Trips.Any(t => TripStatus.Live.Contains(t.Status))),
        "completed" => q.Where(l => l.Status == LoadStatus.Completed),
        "cancelled" => q.Where(l => l.Status == LoadStatus.Cancelled || l.Status == LoadStatus.Expired),
        _ => q
    };

    private static IQueryable<Load> Search(IQueryable<Load> q, string term) =>
        term.Length == 0
            ? q
            : q.Where(l => l.Code.Contains(term) || l.Title.Contains(term) || l.CargoType.Contains(term) ||
                           l.OriginCity!.Name.Contains(term) || l.DestCity!.Name.Contains(term) ||
                           (l.Shipper != null && (l.Shipper.FullName.Contains(term) || l.Shipper.Mobile.Contains(term) ||
                                                  (l.Shipper.BusinessName != null && l.Shipper.BusinessName.Contains(term)))) ||
                           (l.Company != null && (l.Company.Name.Contains(term) || l.Company.Mobile.Contains(term))));

    // ------------------------------------------------------------------
    //  فهرست
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(string? status, string? q, int page = 1, CancellationToken ct = default)
    {
        status = Tabs.Any(t => t.Key == status) ? status : null;
        ViewData["Title"] = status is null ? "بارهای ثبت‌شده" : "بارهای " + Tabs.First(t => t.Key == status).Label;

        var term = AdminOps.Term(q);
        var src = Search(db.Loads.AsNoTracking(), term);

        // شمارندهٔ سربرگ‌ها با جستجو ولی بدون فیلتر وضعیت
        var counts = new Dictionary<string, int> { ["all"] = await src.CountAsync(ct) };
        foreach (var t in Tabs) counts[t.Key] = await Filter(src, t.Key).CountAsync(ct);

        var rows = AdminOps.LoadRows(Filter(src, status).OrderByDescending(l => l.CreatedAt));
        var vm = await PageVm<LoadRow>.FromAsync(rows, page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        ViewBag.Status = status;
        ViewBag.Counts = counts;
        ViewBag.Reported = await db.Loads.CountAsync(l => l.IsReported, ct);
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  بارهای گزارش‌شده
    // ------------------------------------------------------------------

    public async Task<IActionResult> Reported(string? q, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "بارهای گزارش‌شده";
        var term = AdminOps.Term(q);
        var src = Search(db.Loads.AsNoTracking().Where(l => l.IsReported), term);
        var vm = await PageVm<LoadRow>.FromAsync(AdminOps.LoadRows(src.OrderByDescending(l => l.CreatedAt)), page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        return View(vm);
    }

    /// <summary>گزارش علامت‌گذاری داخلی مدیر است، نه وضعیت بار؛ برداشتنش فقط ردّ حسابرسی می‌خواهد.</summary>
    [HttpPost]
    public async Task<IActionResult> ClearReport(int id, string? returnUrl, CancellationToken ct)
    {
        var l = await db.Loads.FirstOrDefaultAsync(x => x.LoadId == id, ct);
        if (l is null) return NotFound();
        if (!l.IsReported)
        {
            TempData["err"] = $"بار {l.Code} گزارش‌شده نیست.";
            return AdminOps.Back(this, returnUrl, "/Admin/Loads/Reported");
        }
        var note = l.ReportNote;
        l.IsReported = false;
        l.ReportNote = null;
        audit.Add("Load", id, "report:clear", $"برداشتن گزارش از بار {l.Code}", new { note });
        await db.SaveChangesAsync(ct);
        TempData["ok"] = $"گزارش بار {l.Code} برداشته شد.";
        return AdminOps.Back(this, returnUrl, "/Admin/Loads/Reported");
    }

    [HttpPost]
    public async Task<IActionResult> Report(int id, string? note, CancellationToken ct)
    {
        var l = await db.Loads.FirstOrDefaultAsync(x => x.LoadId == id, ct);
        if (l is null) return NotFound();
        var n = AdminOps.Note(note);
        if (n is null)
        {
            TempData["err"] = "علت گزارش را بنویسید تا در صف «بارهای گزارش‌شده» معلوم باشد چرا اینجاست.";
            return RedirectToAction(nameof(Detail), new { id });
        }
        var was = l.IsReported;
        l.IsReported = true;
        l.ReportNote = n;
        audit.Add("Load", id, "report", $"{(was ? "به‌روزرسانی گزارش" : "گزارش")} بار {l.Code}: {n}", new { note = n, was });
        await db.SaveChangesAsync(ct);
        TempData["ok"] = $"بار {l.Code} در صف بارهای گزارش‌شده قرار گرفت.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    // ------------------------------------------------------------------
    //  پرونده
    // ------------------------------------------------------------------

    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        var l = await db.Loads.AsNoTracking()
            .Include(x => x.OriginCity).ThenInclude(c => c!.Province)
            .Include(x => x.DestCity).ThenInclude(c => c!.Province)
            .Include(x => x.Shipper).Include(x => x.Company).Include(x => x.TargetCompany)
            .Include(x => x.VehicleType).Include(x => x.Photos)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.LoadId == id, ct);
        if (l is null) return NotFound();
        ViewData["Title"] = $"پروندهٔ بار {l.Code}";

        var vm = new LoadCaseVm { Load = l };
        vm.Offers = await db.Offers.AsNoTracking()
            .Include(o => o.Driver).Include(o => o.Company).Include(o => o.Vehicle).ThenInclude(v => v!.VehicleType)
            .Where(o => o.LoadId == id)
            .OrderBy(o => o.Status == OfferStatus.Accepted ? 0 : o.Status == OfferStatus.Pending ? 1 : 2).ThenBy(o => o.Amount)
            .ToListAsync(ct);
        vm.Trips = await AdminOps.TripRows(db.Trips.AsNoTracking().Where(t => t.LoadId == id).OrderByDescending(t => t.TripId)).ToListAsync(ct);
        vm.MessageCount = await db.Messages.CountAsync(m => m.LoadId == id, ct);

        var ownerKeys = new List<(string Kind, int Id)>();
        if (l.ShipperId is int s) ownerKeys.Add((OwnerKind.Shipper, s));
        else if (l.CompanyId is int c) ownerKeys.Add((OwnerKind.Company, c));
        var owners = await AdminOps.OwnerRefsAsync(db, ownerKeys, ct);
        vm.Owner = owners.Values.FirstOrDefault() ?? new OwnerRef("—");

        var oLat = l.OriginLat ?? l.OriginCity?.Lat;
        var oLng = l.OriginLng ?? l.OriginCity?.Lng;
        var dLat = l.DestLat ?? l.DestCity?.Lat;
        var dLng = l.DestLng ?? l.DestCity?.Lng;
        if (oLat is double ola && oLng is double olg) vm.Markers.Add(new MapMarker(ola, olg, "origin", $"مبدا: {l.OriginCity?.Name}\n{l.OriginAddress}"));
        if (dLat is double dla && dLng is double dlg) vm.Markers.Add(new MapMarker(dla, dlg, "dest", $"مقصد: {l.DestCity?.Name}\n{l.DestAddress}"));

        return View(vm);
    }

    /// <summary>لغو بار قطعی‌نشده. پیشنهادهای در انتظار رد و صاحبانشان باخبر می‌شوند (TripFlow)؛ صاحب بار هم اعلان می‌گیرد.</summary>
    [HttpPost]
    public async Task<IActionResult> Cancel(int id, string? reason, CancellationToken ct)
    {
        var l = await db.Loads.AsNoTracking().FirstOrDefaultAsync(x => x.LoadId == id, ct);
        if (l is null) return NotFound();
        var r = AdminOps.Note(reason, 500);
        try
        {
            if (r is null) throw new UserError("علت لغو را بنویسید؛ برای صاحب بار ارسال می‌شود.");
            await flow.CancelLoadAsync(id, me.ToActor(), r, ct);

            notify.ToLoadOwner(l, $"بار {l.Code} توسط مدیر سامانه لغو شد", r, kind: "load");
            audit.Add("Load", id, "cancel", $"لغو بار {l.Code} توسط مدیر: {r}", new { from = l.Status, reason = r });
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"بار {l.Code} لغو شد و صاحب بار باخبر شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }
}
