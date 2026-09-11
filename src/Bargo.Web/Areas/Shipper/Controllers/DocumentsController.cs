using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.ShipperPanel.Controllers;

/// <summary>
/// اسناد و بارنامه‌های سفرهای من. بارنامه جدول خودش را دارد (Waybills)، بقیهٔ اسناد
/// در TripDocuments با نوعشان. شرط مالکیت همیشه «سفرهای قابل دیدنِ من» است.
/// </summary>
[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
public class DocumentsController(BargoDbContext db, CurrentUser me) : Controller
{
    public const string Waybill = "waybill";

    public static readonly (string Key, string Label, string Icon)[] Kinds =
    [
        (Waybill, "بارنامه‌ها", "bi-file-earmark-text"),
        (TripDocumentKind.CargoInsurance, "بیمه‌نامه‌ها", "bi-shield-check"),
        (TripDocumentKind.LoadingReceipt, "رسید بارگیری", "bi-box-arrow-in-down"),
        (TripDocumentKind.DeliveryReceipt, "رسید تحویل", "bi-receipt"),
    ];

    public async Task<IActionResult> Index(string? kind, int page = 1, CancellationToken ct = default)
    {
        kind = Kinds.Any(k => k.Key == kind) ? kind! : Waybill;
        ViewData["Title"] = Kinds.First(k => k.Key == kind).Label;

        var myTrips = db.Trips.AsNoTracking().VisibleTo(me.ToActor());
        var counts = new Dictionary<string, int>
        {
            [Waybill] = await db.Waybills.CountAsync(w => myTrips.Any(t => t.TripId == w.TripId), ct)
        };
        foreach (var k in Kinds.Where(k => k.Key != Waybill))
            counts[k.Key] = await db.TripDocuments.CountAsync(d => d.Kind == k.Key && myTrips.Any(t => t.TripId == d.TripId), ct);

        IQueryable<ShipperDocRow> rows = kind == Waybill
            ? db.Waybills.AsNoTracking()
                .Where(w => myTrips.Any(t => t.TripId == w.TripId))
                .OrderByDescending(w => w.IssuedAt)
                .Select(w => new ShipperDocRow
                {
                    TripId = w.TripId, TripCode = w.Trip!.Code, From = w.Trip.Load!.OriginCity!.Name, To = w.Trip.Load.DestCity!.Name,
                    Kind = Waybill, Title = "بارنامه", Number = w.Number, Status = w.Status, FilePath = w.FilePath,
                    ByKind = w.IssuedByKind, At = w.IssuedAt
                })
            : db.TripDocuments.AsNoTracking()
                .Where(d => d.Kind == kind && myTrips.Any(t => t.TripId == d.TripId))
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => new ShipperDocRow
                {
                    TripId = d.TripId, TripCode = d.Trip!.Code, From = d.Trip.Load!.OriginCity!.Name, To = d.Trip.Load.DestCity!.Name,
                    Kind = d.Kind, Title = d.Title, FilePath = d.FilePath, ByKind = d.UploadedByKind, At = d.CreatedAt
                });

        var vm = await PageVm<ShipperDocRow>.FromAsync(rows, page, PageLink.For(Request), 30, ct);

        if (kind == TripDocumentKind.CargoInsurance)
        {
            // بارهایی که بیمه خواسته‌اند ولی هنوز بیمه‌نامه‌ای روی سفرشان نیست
            ViewBag.InsuranceMissing = await myTrips
                .Where(t => t.Load!.InsuranceRequested && t.Status != TripStatus.Cancelled &&
                            !t.Documents.Any(d => d.Kind == TripDocumentKind.CargoInsurance))
                .OrderByDescending(t => t.TripId)
                .Select(ShipperTripRow.FromTrip)
                .ToListAsync(ct);
        }

        ViewBag.Kind = kind;
        ViewBag.Counts = counts;
        return View(vm);
    }

    public async Task<IActionResult> Download(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "دانلود اسناد";
        var myTrips = db.Trips.AsNoTracking().VisibleTo(me.ToActor())
            .Where(t => (t.Waybill != null && t.Waybill.FilePath != null) || t.Documents.Any(d => d.FilePath != null));

        var trips = await PageVm<ShipperTripRow>.FromAsync(myTrips.OrderByDescending(t => t.TripId).Select(ShipperTripRow.FromTrip),
            page, PageLink.For(Request), 15, ct);
        var ids = trips.Rows.Select(t => t.TripId).ToList();

        var waybills = await db.Waybills.AsNoTracking().Where(w => ids.Contains(w.TripId) && w.FilePath != null).ToListAsync(ct);
        var docs = await db.TripDocuments.AsNoTracking().Where(d => ids.Contains(d.TripId) && d.FilePath != null)
            .OrderBy(d => d.CreatedAt).ToListAsync(ct);

        var groups = trips.Rows.Select(t => new ShipperDocGroup
        {
            Trip = t,
            Docs = waybills.Where(w => w.TripId == t.TripId)
                .Select(w => new ShipperDocRow { TripId = t.TripId, TripCode = t.Code, Kind = Waybill, Title = "بارنامه", Number = w.Number, Status = w.Status, FilePath = w.FilePath, ByKind = w.IssuedByKind, At = w.IssuedAt })
                .Concat(docs.Where(d => d.TripId == t.TripId)
                    .Select(d => new ShipperDocRow { TripId = t.TripId, TripCode = t.Code, Kind = d.Kind, Title = d.Title, FilePath = d.FilePath, ByKind = d.UploadedByKind, At = d.CreatedAt }))
                .ToList()
        }).ToList();

        ViewBag.Groups = groups;
        return View(trips);
    }
}
