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
/// شکایات و تخلفات: صفِ شکایت‌ها (به تفکیک شاکی، نوع و وضعیت)، پروندهٔ هر شکایت با
/// سفرِ گره‌خورده و سوابق طرفین، و تعیین نتیجه.
///
/// جبران خسارت مثل استرداد است: از <see cref="WalletService"/> با نوع Refund و داخل
/// یک تراکنش، تا ردیف کیف پول و تغییر وضعیت شکایت یا با هم بنشینند یا هیچ‌کدام.
/// ثبت تخلف (اخطار) هم اختیاری و در همان تراکنش است؛ جریمه و تعلیق از «مدیریت
/// رانندگان ← تخلفات» انجام می‌شود.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Support)]
public class ComplaintsController(BargoDbContext db, WalletService wallet, NotificationService notify, AuditService audit, CurrentUser me) : Controller
{
    private static readonly string[] Froms = [OwnerKind.Shipper, OwnerKind.Driver, OwnerKind.Company];
    private const int MaxViolationTitle = 60;

    // ------------------------------------------------------------------
    //  فهرست
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(string? from, string? kind, string? status, int page = 1, CancellationToken ct = default)
    {
        from = from is not null && Froms.Contains(from) ? from : null;
        kind = kind is not null && ComplaintKind.All.Contains(kind) ? kind : null;
        status = status is ComplaintStatus.New or ComplaintStatus.Reviewing or ComplaintStatus.Resolved or ComplaintStatus.Rejected or "all" ? status : "open";

        ViewData["Title"] = from switch
        {
            OwnerKind.Shipper => "شکایات صاحبان بار",
            OwnerKind.Driver => "شکایات رانندگان",
            OwnerKind.Company => "شکایات شرکت‌ها",
            _ => kind == ComplaintKind.Financial ? "اختلافات مالی"
                : status == ComplaintStatus.Reviewing ? "رسیدگی و تعیین نتیجه" : "شکایات"
        };

        var all = db.Complaints.AsNoTracking();
        if (from is not null) all = all.Where(c => c.FromKind == from);
        if (kind is not null) all = all.Where(c => c.Kind == kind);

        var counts = await all.GroupBy(c => c.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        counts["open"] = counts.GetValueOrDefault(ComplaintStatus.New) + counts.GetValueOrDefault(ComplaintStatus.Reviewing);
        counts["all"] = counts.Where(kv => kv.Key is not ("open" or "all")).Sum(kv => kv.Value);

        var q = status switch
        {
            "open" => all.Where(c => c.Status == ComplaintStatus.New || c.Status == ComplaintStatus.Reviewing),
            "all" => all,
            _ => all.Where(c => c.Status == status)
        };
        // صف باز از قدیمی‌ترین (کسی که بیشتر منتظر مانده اول)؛ سوابق از جدیدترین
        q = status is "open" or ComplaintStatus.New or ComplaintStatus.Reviewing
            ? q.OrderBy(c => c.ComplaintId)
            : q.OrderByDescending(c => c.ComplaintId);

        var pageVm = await PageVm<Complaint>.FromAsync(q, page, PageLink.For(Request), ct: ct);
        var parties = pageVm.Rows.Select(r => (r.FromKind, r.FromId))
            .Concat(pageVm.Rows.Where(r => r.AgainstKind != null && r.AgainstId != null).Select(r => (r.AgainstKind!, r.AgainstId!.Value)));

        var vm = new ComplaintsVm
        {
            Page = pageVm,
            Names = await BackofficeLookup.NamesAsync(db, parties, ct),
            TripCodes = await BackofficeLookup.TripCodesAsync(db, pageVm.Rows.Select(r => r.TripId), ct),
            From = from, Kind = kind, Status = status, Counts = counts
        };
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  پرونده
    // ------------------------------------------------------------------

    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        var c = await db.Complaints.AsNoTracking().FirstOrDefaultAsync(x => x.ComplaintId == id, ct);
        if (c is null) return NotFound();
        ViewData["Title"] = $"شکایت {c.Code}";

        TripSummaryVm? trip = null;
        if (c.TripId is int tid)
            trip = await db.Trips.AsNoTracking().Where(t => t.TripId == tid).Select(t => new TripSummaryVm
            {
                TripId = t.TripId,
                Code = t.Code,
                Status = t.Status,
                From = t.Load!.OriginCity!.Name,
                To = t.Load.DestCity!.Name,
                CargoTitle = t.Load.Title,
                WeightTon = t.Load.WeightTon,
                Fare = t.Fare,
                Commission = t.Commission,
                CarrierKind = t.CarrierKind,
                DriverName = t.Driver != null ? t.Driver.FirstName + " " + t.Driver.LastName : null,
                CompanyName = t.Company != null ? t.Company.Name : null,
                ShipperName = t.Load.Shipper != null
                    ? (t.Load.Shipper.Kind == "business" && t.Load.Shipper.BusinessName != null && t.Load.Shipper.BusinessName != "" ? t.Load.Shipper.BusinessName : t.Load.Shipper.FullName)
                    : t.Load.Company != null ? t.Load.Company.Name : null,
                IsPaid = t.IsPaid,
                IsProblem = t.IsProblem,
                ProblemNote = t.ProblemNote,
                CreatedAt = t.CreatedAt,
                LoadedAt = t.LoadedAt,
                DeliveredAt = t.DeliveredAt,
                SettledAt = t.SettledAt,
                CancelledAt = t.CancelledAt
            }).FirstOrDefaultAsync(ct);

        // سوابق طرفین: هر شکایتی که یکی از دو طرفِ این پرونده در آن شاکی یا متشاکی بوده
        var ak = c.AgainstKind;
        var aid = c.AgainstId;
        var related = await db.Complaints.AsNoTracking()
            .Where(x => x.ComplaintId != id && (
                (x.FromKind == c.FromKind && x.FromId == c.FromId) ||
                (x.AgainstKind == c.FromKind && x.AgainstId == c.FromId) ||
                (ak != null && ((x.FromKind == ak && x.FromId == aid) || (x.AgainstKind == ak && x.AgainstId == aid)))))
            .OrderByDescending(x => x.ComplaintId).Take(10).ToListAsync(ct);

        var againstViolations = ak switch
        {
            OwnerKind.Driver => await db.Violations.CountAsync(v => v.DriverId == aid, ct),
            OwnerKind.Company => await db.Violations.CountAsync(v => v.CompanyId == aid, ct),
            _ => 0
        };

        var parties = new List<(string, int)> { (c.FromKind, c.FromId) };
        if (ak is not null && aid is int a) parties.Add((ak, a));
        parties.AddRange(related.Select(r => (r.FromKind, r.FromId)));
        parties.AddRange(related.Where(r => r.AgainstKind != null && r.AgainstId != null).Select(r => (r.AgainstKind!, r.AgainstId!.Value)));

        var vm = new ComplaintDetailVm
        {
            C = c,
            Names = await BackofficeLookup.NamesAsync(db, parties, ct),
            Trip = trip,
            Related = related,
            AgainstViolations = againstViolations,
            ResolvedBy = c.ResolvedByAdminId is int rid
                ? (await BackofficeLookup.AdminNamesAsync(db, [rid], ct)).GetValueOrDefault(rid)
                : null
        };
        ViewBag.TripCodes = await BackofficeLookup.TripCodesAsync(db, related.Select(r => r.TripId), ct);
        return View(vm);
    }

    /// <summary>شروع رسیدگی — شاکی می‌فهمد پرونده‌اش دیده شده است.</summary>
    [HttpPost]
    public async Task<IActionResult> Start(int id, CancellationToken ct)
    {
        var c = await db.Complaints.AsNoTracking().FirstOrDefaultAsync(x => x.ComplaintId == id, ct);
        if (c is null) return NotFound();
        var back = RedirectToAction(nameof(Detail), new { id });

        var n = await db.Complaints.Where(x => x.ComplaintId == id && x.Status == ComplaintStatus.New)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ComplaintStatus.Reviewing), ct);
        if (n == 0) { TempData["err"] = "این شکایت پیش‌تر در دست رسیدگی یا بسته شده است."; return back; }

        audit.Add("Complaint", id, "start", $"شروع رسیدگی به شکایت {c.Code} — {c.Title}", new { c.FromKind, c.FromId, c.TripId });
        notify.Add(c.FromKind, c.FromId, "شکایت شما در دست رسیدگی است",
            $"پروندهٔ {c.Code} توسط پشتیبانی بارگو باز شد؛ نتیجه به شما اعلان می‌شود.",
            $"/{BackofficeLookup.PanelOf(c.FromKind)}/Complaints/Detail/{id}");
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"رسیدگی به شکایت {c.Code} شروع شد.";
        return back;
    }

    /// <summary>
    /// تعیین نتیجه. جبران خسارت فقط با «وارد است» و فقط به کیف پول شاکی؛ ردِ شکایت هیچ
    /// پولی جابه‌جا نمی‌کند. تغییر وضعیت با شرطِ «هنوز باز است» در همان تراکنش است تا دو
    /// مدیر هم‌زمان دو بار جبران ثبت نکنند.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Resolve(int id, string decision, string? resolution, string? compensationToman, bool registerViolation = false,
        string? violationTitle = null, CancellationToken ct = default)
    {
        var back = RedirectToAction(nameof(Detail), new { id });
        var c = await db.Complaints.AsNoTracking().FirstOrDefaultAsync(x => x.ComplaintId == id, ct);
        if (c is null) return NotFound();

        if (decision is not (ComplaintStatus.Resolved or ComplaintStatus.Rejected))
        {
            TempData["err"] = "نتیجهٔ رسیدگی (وارد است / رد می‌شود) را انتخاب کنید.";
            return back;
        }
        resolution = AdminOps.Note(resolution, 2000);
        if (resolution is null || resolution.Length < 5)
        {
            TempData["err"] = "شرح نتیجه (دست‌کم ۵ نویسه) اجباری است؛ برای هر دو طرف ارسال می‌شود.";
            return back;
        }

        var compensation = 0L;
        if (!string.IsNullOrWhiteSpace(compensationToman))
        {
            var parsed = Fa.ParseToman(compensationToman);
            if (parsed is null or < 0) { TempData["err"] = "مبلغ جبران خسارت را به تومان و فقط با رقم وارد کنید."; return back; }
            compensation = parsed.Value;
        }
        if (compensation > 0 && decision == ComplaintStatus.Rejected)
        {
            TempData["err"] = "برای شکایتِ ردشده جبران خسارت معنا ندارد؛ مبلغ را خالی بگذارید یا نتیجه را «وارد است» بزنید.";
            return back;
        }
        if (compensation > 0 && !BackofficeLookup.WalletOwners.Contains(c.FromKind))
        {
            TempData["err"] = "شاکی این پرونده کیف پول ندارد؛ جبران خسارت ممکن نیست.";
            return back;
        }

        var canViolate = c.AgainstKind is OwnerKind.Driver or OwnerKind.Company && c.AgainstId is > 0;
        if (registerViolation && !canViolate)
        {
            TempData["err"] = "طرفِ این شکایت راننده یا شرکت نیست؛ تخلفی قابل ثبت نیست.";
            return back;
        }
        if (registerViolation && decision == ComplaintStatus.Rejected)
        {
            TempData["err"] = "برای شکایتِ ردشده تخلفی ثبت نمی‌شود.";
            return back;
        }

        var parties = new List<(string Kind, int Id)> { (c.FromKind, c.FromId) };
        if (c.AgainstKind is not null && c.AgainstId is int ag) parties.Add((c.AgainstKind, ag));
        var names = await BackofficeLookup.NamesAsync(db, parties, ct);
        var fromName = names.Name(c.FromKind, c.FromId);
        var now = DateTime.UtcNow;
        long? compensationOrNull = compensation > 0 ? compensation : null;

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var n = await db.Complaints
                .Where(x => x.ComplaintId == id && (x.Status == ComplaintStatus.New || x.Status == ComplaintStatus.Reviewing))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, decision)
                    .SetProperty(x => x.Resolution, resolution)
                    .SetProperty(x => x.CompensationAmount, compensationOrNull)
                    .SetProperty(x => x.ResolvedByAdminId, me.Id)
                    .SetProperty(x => x.ResolvedAt, now), ct);
            if (n == 0) { TempData["err"] = "نتیجهٔ این شکایت پیش‌تر ثبت شده است."; return back; }

            long? balanceAfter = null;
            if (compensation > 0)
            {
                var row = await wallet.PostAsync(c.FromKind, c.FromId, compensation, WalletTxnKind.Refund, c.TripId,
                    $"جبران خسارت شکایت {c.Code}: {c.Title}", ct);
                balanceAfter = row.BalanceAfter;
            }

            int? violationId = null;
            if (registerViolation)
            {
                var title = AdminOps.Note(violationTitle, MaxViolationTitle) ?? (c.Title.Length > MaxViolationTitle ? c.Title[..MaxViolationTitle] : c.Title);
                var v = new Violation
                {
                    DriverId = c.AgainstKind == OwnerKind.Driver ? c.AgainstId : null,
                    CompanyId = c.AgainstKind == OwnerKind.Company ? c.AgainstId : null,
                    TripId = c.TripId,
                    ComplaintId = c.ComplaintId,
                    Title = title,
                    Description = $"از شکایت {c.Code}: {resolution}",
                    Penalty = "warning",
                    CreatedByAdminId = me.Id
                };
                db.Violations.Add(v);
                await db.SaveChangesAsync(ct);
                violationId = v.ViolationId;
            }

            var tripCode = c.TripId is int tid ? await db.Trips.Where(t => t.TripId == tid).Select(t => t.Code).FirstOrDefaultAsync(ct) : null;
            var label = decision == ComplaintStatus.Resolved ? "وارد دانسته شد" : "رد شد";
            var complainantLink = $"/{BackofficeLookup.PanelOf(c.FromKind)}/Complaints/Detail/{id}";

            notify.Add(c.FromKind, c.FromId,
                $"نتیجهٔ شکایت {c.Code}: {label}",
                compensation > 0 ? $"{resolution}\nجبران خسارت {Fa.Toman(compensation)} به کیف پول شما واریز شد." : resolution,
                complainantLink);

            if (c.AgainstKind is not null && c.AgainstId is int againstId && BackofficeLookup.WalletOwners.Contains(c.AgainstKind))
            {
                var againstLink = await AgainstLinkAsync(c.AgainstKind, c.TripId, ct);
                var body = registerViolation
                    ? $"{resolution}\nبابت این موضوع یک اخطار در سوابق شما ثبت شد."
                    : resolution;
                notify.Add(c.AgainstKind, againstId,
                    decision == ComplaintStatus.Resolved
                        ? $"شکایت دربارهٔ {(tripCode is null ? "شما" : "سفر " + tripCode)} وارد دانسته شد"
                        : $"شکایت دربارهٔ {(tripCode is null ? "شما" : "سفر " + tripCode)} رد شد",
                    body, againstLink);
            }

            audit.Add("Complaint", id, "resolve:" + decision,
                $"شکایت {c.Code} «{c.Title}» {label}" + (compensation > 0 ? $" — جبران {Fa.Toman(compensation)} به {fromName}" : "") + (registerViolation ? " — با ثبت اخطار" : ""),
                new { decision, resolution, compensation, balanceAfter, violationId, c.FromKind, c.FromId, c.AgainstKind, c.AgainstId, c.TripId });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (UserError e)
        {
            TempData["err"] = $"{e.Message} نتیجهٔ شکایت ثبت نشد.";
            return back;
        }

        TempData["ok"] = decision == ComplaintStatus.Resolved
            ? $"شکایت {c.Code} وارد دانسته شد." + (compensation > 0 ? $" {Fa.Toman(compensation)} به کیف پول «{fromName}» واریز شد." : "") + (registerViolation ? " اخطار ثبت شد." : "")
            : $"شکایت {c.Code} رد شد و هر دو طرف مطلع شدند.";
        return back;
    }

    /// <summary>پیوند اعلانِ طرفِ شکایت: صفحهٔ همان سفر در پنل خودش؛ بی‌سفر، صندوق اعلان‌ها.</summary>
    private async Task<string> AgainstLinkAsync(string kind, int? tripId, CancellationToken ct)
    {
        var panel = BackofficeLookup.PanelOf(kind);
        if (tripId is not int tid) return $"/{panel}/Notifications";
        return kind switch
        {
            OwnerKind.Driver => $"/Driver/Trips/Detail/{tid}",
            OwnerKind.Company => $"/Company/Trips/Detail/{tid}",
            OwnerKind.Shipper => await db.Trips.Where(t => t.TripId == tid).Select(t => (int?)t.LoadId).FirstOrDefaultAsync(ct) is int lid
                ? $"/Shipper/Loads/Detail/{lid}" : "/Shipper/Notifications",
            _ => $"/{panel}/Notifications"
        };
    }

    // ------------------------------------------------------------------
    //  تخلفات
    // ------------------------------------------------------------------

    public async Task<IActionResult> Violations(string? penalty, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "تخلفات";
        penalty = penalty is "warning" or "fine" or "suspension" ? penalty : null;
        var all = db.Violations.AsNoTracking();
        var q = penalty is null ? all : all.Where(v => v.Penalty == penalty);

        var rows = q.OrderByDescending(v => v.ViolationId).Select(v => new ViolationRowVm
        {
            Id = v.ViolationId,
            DriverId = v.DriverId,
            DriverName = v.Driver != null ? v.Driver.FirstName + " " + v.Driver.LastName : null,
            DriverMobile = v.Driver != null ? v.Driver.Mobile : null,
            CompanyName = db.Companies.Where(c => c.CompanyId == v.CompanyId).Select(c => c.Name).FirstOrDefault(),
            TripId = v.TripId,
            TripCode = db.Trips.Where(t => t.TripId == v.TripId).Select(t => t.Code).FirstOrDefault(),
            ComplaintId = v.ComplaintId,
            ComplaintCode = db.Complaints.Where(c => c.ComplaintId == v.ComplaintId).Select(c => c.Code).FirstOrDefault(),
            Title = v.Title,
            Description = v.Description,
            Penalty = v.Penalty,
            FineAmount = v.FineAmount,
            SuspendedUntil = v.SuspendedUntil,
            CreatedBy = db.Admins.Where(a => a.AdminId == v.CreatedByAdminId).Select(a => a.Name).FirstOrDefault(),
            CreatedAt = v.CreatedAt
        });

        var month = Fa.MonthStartUtc;
        var vm = new ViolationsVm
        {
            Page = await PageVm<ViolationRowVm>.FromAsync(rows, page, PageLink.For(Request), ct: ct),
            Penalty = penalty,
            Total = await all.CountAsync(ct),
            Month = await all.CountAsync(v => v.CreatedAt >= month, ct),
            ByPenalty = await all.GroupBy(v => v.Penalty).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct)
        };
        return View(vm);
    }
}
