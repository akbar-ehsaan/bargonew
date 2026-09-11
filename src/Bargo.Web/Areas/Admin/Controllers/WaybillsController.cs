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
/// «بارنامه و اسناد» — بارنامه‌های ثبت‌شده روی سفرها (جستجو با شماره، تأیید/ابطال) و
/// اسناد سفرها (رسید بارگیری، رسید تحویل، بیمه‌نامهٔ بار، تصویر).
///
/// بارنامه را راننده یا شرکت ثبت می‌کند؛ مدیر فقط وضعیتش را تعیین می‌کند. ابطال
/// بدون علت پذیرفته نمی‌شود و حمل‌کننده اعلان می‌گیرد.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Operations)]
public class WaybillsController(BargoDbContext db, NotificationService notify, AuditService audit) : Controller
{
    private static readonly string[] Statuses = ["registered", "verified", "void"];

    private static readonly string[] DocKinds =
        [TripDocumentKind.LoadingReceipt, TripDocumentKind.DeliveryReceipt, TripDocumentKind.CargoInsurance, TripDocumentKind.Photo, TripDocumentKind.Other];

    // ------------------------------------------------------------------
    //  بارنامه‌ها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(string? q, string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "بارنامه‌ها";
        var term = AdminOps.Term(q);
        var src = db.Waybills.AsNoTracking();
        if (term.Length > 0)
            src = src.Where(w => w.Number.Contains(term) || w.Trip!.Code.Contains(term) || w.Trip.Load!.Code.Contains(term));

        ViewBag.Counts = await src.GroupBy(w => w.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        if (status is not null && Statuses.Contains(status)) src = src.Where(w => w.Status == status);

        var rows = src.OrderBy(w => w.Status == "registered" ? 0 : 1).ThenByDescending(w => w.IssuedAt).Select(w => new WaybillRow
        {
            WaybillId = w.WaybillId,
            Number = w.Number,
            Status = w.Status,
            TripId = w.TripId,
            TripCode = w.Trip!.Code,
            TripStatus = w.Trip.Status,
            From = w.Trip.Load!.OriginCity!.Name,
            To = w.Trip.Load.DestCity!.Name,
            CarrierKind = w.Trip.CarrierKind,
            CompanyName = w.Trip.Company!.Name,
            DriverName = w.Trip.Driver != null ? w.Trip.Driver.FirstName + " " + w.Trip.Driver.LastName : null,
            Plate = w.Trip.Vehicle!.PlateNo,
            IssuedByKind = w.IssuedByKind,
            IssuedById = w.IssuedById,
            FilePath = w.FilePath,
            IssuedAt = w.IssuedAt
        });
        var vm = await PageVm<WaybillRow>.FromAsync(rows, page, PageLink.For(Request), ct: ct);
        ViewBag.Issuers = await AdminOps.OwnerRefsAsync(db, vm.Rows.Select(r => (r.IssuedByKind, r.IssuedById)), ct);
        ViewBag.Q = term;
        ViewBag.Status = status is not null && Statuses.Contains(status) ? status : null;
        ViewBag.Statuses = Statuses;
        return View(vm);
    }

    /// <summary>ثبت‌شده ← تأییدشده / باطل. ابطال علت می‌خواهد؛ حمل‌کنندهٔ سفر اعلان می‌گیرد.</summary>
    [HttpPost]
    public async Task<IActionResult> SetStatus(int id, string status, string? note, string? returnUrl, CancellationToken ct)
    {
        var w = await db.Waybills.Include(x => x.Trip).FirstOrDefaultAsync(x => x.WaybillId == id, ct);
        if (w is null || w.Trip is null) return NotFound();

        try
        {
            if (!Statuses.Contains(status)) throw new UserError("وضعیت انتخاب‌شده معتبر نیست.");
            note = AdminOps.Note(note);
            if (w.Status == status) throw new UserError($"بارنامهٔ {w.Number} از قبل «{OpsUi.WaybillLabel(status)}» است.");
            if (status == "void" && note is null) throw new UserError("برای ابطال بارنامه، علت را بنویسید؛ حمل‌کننده باید بداند چرا باید بارنامهٔ تازه ثبت کند.");

            var from = w.Status;
            w.Status = status;

            var (title, body) = status switch
            {
                "verified" => ($"بارنامهٔ {w.Number} تأیید شد", note ?? $"بارنامهٔ سفر {w.Trip.Code} توسط مدیر تأیید شد."),
                "void" => ($"بارنامهٔ {w.Number} باطل شد", note + $" — برای سفر {w.Trip.Code} بارنامهٔ معتبر ثبت کنید."),
                _ => ($"بارنامهٔ {w.Number} به حالت ثبت‌شده برگشت", note ?? $"وضعیت بارنامهٔ سفر {w.Trip.Code} به «ثبت‌شده» برگردانده شد.")
            };
            notify.ToCarrier(w.Trip, title, body, "document");

            audit.Add("Waybill", id, "status:" + status,
                $"بارنامهٔ {w.Number} (سفر {w.Trip.Code}): {OpsUi.WaybillLabel(from)} ← {OpsUi.WaybillLabel(status)}",
                new { from, to = status, note, w.TripId });
            await db.SaveChangesAsync(ct);

            var hint = status == "void" && TripStatus.Live.Contains(w.Trip.Status)
                ? " توجه: این سفر در حال انجام است؛ حمل‌کننده باید بارنامهٔ تازه ثبت کند."
                : "";
            TempData["ok"] = $"بارنامهٔ {w.Number} «{OpsUi.WaybillLabel(status)}» شد.{hint}";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return AdminOps.Back(this, returnUrl, "/Admin/Waybills");
    }

    // ------------------------------------------------------------------
    //  اسناد سفرها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Documents(string? q, string? kind, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "اسناد سفرها";
        var term = AdminOps.Term(q);
        var src = db.TripDocuments.AsNoTracking();
        if (term.Length > 0)
            src = src.Where(d => d.Title.Contains(term) || d.Trip!.Code.Contains(term) || d.Trip.Load!.Code.Contains(term));

        ViewBag.Counts = await src.GroupBy(d => d.Kind).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        if (kind is not null && DocKinds.Contains(kind)) src = src.Where(d => d.Kind == kind);

        var rows = src.OrderByDescending(d => d.CreatedAt).Select(d => new TripDocRow
        {
            TripDocumentId = d.TripDocumentId,
            TripId = d.TripId,
            TripCode = d.Trip!.Code,
            TripStatus = d.Trip.Status,
            From = d.Trip.Load!.OriginCity!.Name,
            To = d.Trip.Load.DestCity!.Name,
            Kind = d.Kind,
            Title = d.Title,
            FilePath = d.FilePath,
            UploadedByKind = d.UploadedByKind,
            UploadedById = d.UploadedById,
            CreatedAt = d.CreatedAt
        });
        var vm = await PageVm<TripDocRow>.FromAsync(rows, page, PageLink.For(Request), ct: ct);
        ViewBag.Uploaders = await AdminOps.OwnerRefsAsync(db, vm.Rows.Select(r => (r.UploadedByKind, r.UploadedById)), ct);
        ViewBag.Q = term;
        ViewBag.Kind = kind is not null && DocKinds.Contains(kind) ? kind : null;
        ViewBag.Kinds = DocKinds;
        return View(vm);
    }
}
