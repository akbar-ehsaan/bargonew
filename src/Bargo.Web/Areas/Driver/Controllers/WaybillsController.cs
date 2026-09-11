using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.DriverPanel.Controllers;

/// <summary>
/// بارنامه و اسناد سفرهای راننده. ثبت بارنامه و بارگذاری سند در صفحهٔ سفر انجام می‌شود
/// (Trips/Waybill و Trips/Document)؛ اینجا نمای فهرستیِ همان‌هاست.
/// </summary>
[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
public class WaybillsController(BargoDbContext db, CurrentUser me, TripFlow flow, SettingsService settings) : Controller
{
    public async Task<IActionResult> Index(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "بارنامه‌های من";
        var meId = me.Id;
        var q = db.Waybills.AsNoTracking().Where(w => w.Trip!.DriverId == meId)
            .OrderByDescending(w => w.IssuedAt)
            .Select(w => new WaybillRowVm
            {
                WaybillId = w.WaybillId,
                Number = w.Number,
                Status = w.Status,
                FilePath = w.FilePath,
                IssuedAt = w.IssuedAt,
                TripId = w.TripId,
                TripCode = w.Trip!.Code,
                TripStatus = w.Trip!.Status,
                From = w.Trip!.Load!.OriginCity!.Name,
                To = w.Trip!.Load!.DestCity!.Name,
                Title = w.Trip!.Load!.Title
            });

        // سفری که به مرحلهٔ بارنامه رسیده ولی هنوز بارنامه ندارد — شروع سفر بدون آن بسته است
        ViewBag.Missing = await db.Trips.AsNoTracking()
            .Where(t => t.DriverId == meId && t.Waybill == null &&
                        (t.Status == TripStatus.AtOrigin || t.Status == TripStatus.Loaded))
            .ToTripRows().ToListAsync(ct);

        return View(await PageVm<WaybillRowVm>.FromAsync(q, page, PageLink.For(Request), ct: ct));
    }

    public async Task<IActionResult> Current(CancellationToken ct)
    {
        ViewData["Title"] = "مشاهده بارنامه";
        TripDetailVm? vm = null;
        if (await db.CurrentTripIdAsync(me.Id, ct) is int id && await flow.FindForAsync(id, me.ToActor(), ct) is { } trip)
            vm = await db.TripDetailAsync(trip, me.Id, settings, Request, ct);
        return View(vm);
    }

    public async Task<IActionResult> Documents(string? kind, int page = 1, CancellationToken ct = default)
    {
        kind = kind is TripDocumentKind.LoadingReceipt or TripDocumentKind.DeliveryReceipt or TripDocumentKind.Photo
            or TripDocumentKind.CargoInsurance or TripDocumentKind.Other ? kind : null;
        ViewData["Title"] = "اسناد بار";

        var meId = me.Id;
        var q = db.TripDocuments.AsNoTracking().Where(d => d.Trip!.DriverId == meId);
        if (kind is not null) q = q.Where(d => d.Kind == kind);
        var rows = q.OrderByDescending(d => d.CreatedAt).Select(d => new TripDocRowVm
        {
            TripDocumentId = d.TripDocumentId,
            Kind = d.Kind,
            Title = d.Title,
            FilePath = d.FilePath,
            UploadedByKind = d.UploadedByKind,
            UploadedById = d.UploadedById,
            CreatedAt = d.CreatedAt,
            TripId = d.TripId,
            TripCode = d.Trip!.Code,
            From = d.Trip!.Load!.OriginCity!.Name,
            To = d.Trip!.Load!.DestCity!.Name
        });

        return View(new TripDocsVm { Kind = kind, Page = await PageVm<TripDocRowVm>.FromAsync(rows, page, PageLink.For(Request), ct: ct) });
    }

    /// <summary>رسید تحویل: سفرهای تحویل‌شده با زمان تحویل و رسیدهای بارگذاری‌شده‌شان.</summary>
    public async Task<IActionResult> Receipts(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "رسید تحویل";
        var meId = me.Id;
        var q = db.Trips.AsNoTracking().Where(t => t.DriverId == meId && t.DeliveredAt != null)
            .OrderByDescending(t => t.DeliveredAt)
            .Select(t => new ReceiptRowVm
            {
                TripId = t.TripId,
                Code = t.Code,
                Title = t.Load!.Title,
                From = t.Load!.OriginCity!.Name,
                To = t.Load!.DestCity!.Name,
                Status = t.Status,
                ReceiverName = (t.IsPaid || t.PayMethod == PayMethods.Cash) ? t.Load!.ReceiverName : null,
                ReceiverMobile = (t.IsPaid || t.PayMethod == PayMethods.Cash) ? t.Load!.ReceiverMobile : null,
                DeliveredAt = t.DeliveredAt,
                WaybillNo = t.Waybill!.Number
            });
        var vm = await PageVm<ReceiptRowVm>.FromAsync(q, page, PageLink.For(Request), ct: ct);

        var ids = vm.Rows.Select(r => r.TripId).ToList();
        if (ids.Count > 0)
        {
            var docs = await db.TripDocuments.AsNoTracking()
                .Where(d => ids.Contains(d.TripId) && d.Kind == TripDocumentKind.DeliveryReceipt)
                .OrderBy(d => d.CreatedAt)
                .Select(d => new TripDocRowVm
                {
                    TripDocumentId = d.TripDocumentId, Kind = d.Kind, Title = d.Title, FilePath = d.FilePath,
                    UploadedByKind = d.UploadedByKind, UploadedById = d.UploadedById, CreatedAt = d.CreatedAt, TripId = d.TripId
                }).ToListAsync(ct);
            foreach (var r in vm.Rows) r.Receipts = docs.Where(d => d.TripId == r.TripId).ToList();
        }
        return View(vm);
    }
}
