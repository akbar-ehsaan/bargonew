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
/// «احراز هویت و مدارک» — صف بررسی مدارک همهٔ صاحبان (راننده، خودرو، شرکت، صاحب بار)
/// در یک جدول، مدارک تأییدشدهٔ منقضی، و هشدار انقضا با یادآوری دستی.
///
/// قاعده‌ها: صف «قدیمی‌ترین اول» تا هیچ مدرکی زیر مدارک تازه دفن نشود؛ رد بدون علت
/// پذیرفته نمی‌شود؛ هر تصمیم اعلان به صاحب و ردّ حسابرسی دارد. مدرکِ منقضی که
/// نسخهٔ تأییدشدهٔ تازه‌تری دارد (تمدیدشده) از فهرست‌های انقضا حذف می‌شود
/// (<see cref="AdminOps.NotSuperseded"/>).
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Verification)]
public class DocumentsController(
    BargoDbContext db,
    NotificationService notify,
    AuditService audit,
    SettingsService settings,
    CurrentUser me) : Controller
{
    private static readonly string[] Kinds =
    [
        DocumentKind.NationalCard, DocumentKind.License, DocumentKind.SmartCard, DocumentKind.VehicleCard, DocumentKind.Insurance,
        DocumentKind.Inspection, DocumentKind.CompanyLicense, DocumentKind.Registration, DocumentKind.Other
    ];

    private static readonly string[] Owners = [OwnerKind.Driver, OwnerKind.Vehicle, OwnerKind.Company, OwnerKind.Shipper];

    private static bool ValidStatus(string? s) => s is AccountStatus.Pending or AccountStatus.Approved or AccountStatus.Rejected;

    // ------------------------------------------------------------------
    //  صف بررسی / تأییدشده / ردشده
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(string? status, string? kind, string? owner, string? q, int page = 1, CancellationToken ct = default)
    {
        status = ValidStatus(status) ? status : AccountStatus.Pending;
        ViewData["Title"] = status switch
        {
            AccountStatus.Approved => "مدارک تأییدشده",
            AccountStatus.Rejected => "مدارک ردشده",
            _ => "مدارک در انتظار بررسی"
        };
        var term = AdminOps.Term(q);
        var (ownerKind, ownerId) = AdminOps.ParseOwner(owner);

        var src = db.Documents.AsNoTracking();
        if (ownerKind is not null) src = src.Where(d => d.OwnerKind == ownerKind);
        if (ownerId is int oid) src = src.Where(d => d.OwnerId == oid);
        if (kind is not null && Kinds.Contains(kind)) src = src.Where(d => d.Kind == kind);
        if (term.Length > 0)
            src = src.Where(d => (d.Number != null && d.Number.Contains(term)) ||
                                 (d.OwnerKind == OwnerKind.Driver && db.Drivers.Any(x => x.DriverId == d.OwnerId && ((x.FirstName + " " + x.LastName).Contains(term) || x.Mobile.Contains(term)))) ||
                                 (d.OwnerKind == OwnerKind.Shipper && db.Shippers.Any(x => x.ShipperId == d.OwnerId && (x.FullName.Contains(term) || (x.BusinessName != null && x.BusinessName.Contains(term)) || x.Mobile.Contains(term)))) ||
                                 (d.OwnerKind == OwnerKind.Company && db.Companies.Any(x => x.CompanyId == d.OwnerId && (x.Name.Contains(term) || x.Mobile.Contains(term)))) ||
                                 (d.OwnerKind == OwnerKind.Vehicle && db.Vehicles.Any(x => x.VehicleId == d.OwnerId && x.PlateNo.Contains(term))));

        ViewBag.Counts = await src.GroupBy(d => d.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        src = src.Where(d => d.Status == status);

        var ordered = status == AccountStatus.Pending
            ? src.OrderBy(d => d.UploadedAt)
            : src.OrderByDescending(d => d.ReviewedAt ?? d.UploadedAt);
        var pv = await PageVm<Document>.FromAsync(ordered, page, PageLink.For(Request), status == AccountStatus.Pending ? 15 : 30, ct);
        var vm = await RowsAsync(pv, withRenewal: status != AccountStatus.Pending, ct);

        ViewBag.Status = status;
        ViewBag.Kind = kind is not null && Kinds.Contains(kind) ? kind : null;
        ViewBag.Owner = ownerKind is null ? null : ownerId is int id2 ? $"{ownerKind}:{id2}" : ownerKind;
        ViewBag.OwnerKind = ownerKind;
        ViewBag.Q = term;
        ViewBag.Kinds = Kinds;
        ViewBag.Owners = Owners;
        ViewBag.WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        return View(vm);
    }

    /// <summary>
    /// تأیید/رد یک مدرک. تأیید کارت ملی صاحب بار، احراز هویت او را کامل می‌کند (همان
    /// کاری که «احراز هویت‌ها» در مدیریت کاربران می‌کند) تا دو صف با هم نخوانند.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Review(int id, string decision, string? note, string? returnUrl, CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.DocumentId == id, ct);
        if (doc is null) return NotFound();

        try
        {
            if (decision is not (AccountStatus.Approved or AccountStatus.Rejected)) throw new UserError("تصمیم انتخاب‌شده معتبر نیست.");
            note = AdminOps.Note(note);
            if (decision == AccountStatus.Rejected && note is null)
                throw new UserError("برای رد مدرک، علت را بنویسید تا صاحب مدرک بداند چه چیزی را اصلاح کند.");
            if (doc.Status == decision)
                throw new UserError($"این مدرک از قبل «{AccountStatus.Label(decision)}» است.");
            if (decision == AccountStatus.Approved && doc.IsExpired)
                throw new UserError($"تاریخ اعتبار این مدرک ({Fa.Date(doc.ExpiresAt)}) گذشته است؛ مدرک منقضی تأیید نمی‌شود — آن را رد کنید تا نسخهٔ تازه بارگذاری شود.");
            if (decision == AccountStatus.Approved && string.IsNullOrEmpty(doc.FilePath))
                throw new UserError("این مدرک فایلی ندارد؛ بدون دیدن فایل تأیید نمی‌شود.");

            var now = DateTime.UtcNow;
            var from = doc.Status;
            doc.Status = decision;
            doc.ReviewNote = note;
            doc.ReviewedAt = now;
            doc.ReviewedByAdminId = me.Id;

            var extra = "";
            if (doc.OwnerKind == OwnerKind.Shipper && doc.Kind == DocumentKind.NationalCard)
            {
                var s = await db.Shippers.FirstOrDefaultAsync(x => x.ShipperId == doc.OwnerId, ct);
                if (s is not null)
                {
                    if (decision == AccountStatus.Approved && s.VerifyStatus != AccountStatus.Approved)
                    {
                        s.VerifyStatus = AccountStatus.Approved;
                        s.VerifiedAt = now;
                        extra = " احراز هویت این صاحب بار کامل شد.";
                    }
                    else if (decision == AccountStatus.Rejected && s.VerifyStatus == AccountStatus.Pending &&
                             !await db.Documents.AnyAsync(x => x.OwnerKind == OwnerKind.Shipper && x.OwnerId == s.ShipperId && x.DocumentId != id && x.Status != AccountStatus.Rejected, ct))
                    {
                        // آخرین مدرک معتبرش رد شد — پرونده‌اش دیگر «در انتظار» نیست
                        s.VerifyStatus = AccountStatus.Rejected;
                        extra = " احراز هویت این صاحب بار رد شد.";
                    }
                }
            }

            var label = DocumentKind.Label(doc.Kind);
            var target = await AdminOps.NotifyTargetAsync(db, doc.OwnerKind, doc.OwnerId, doc.Kind, ct);
            if (target is { } t)
                notify.Add(t.Kind, t.Id,
                    decision == AccountStatus.Approved ? $"مدرک «{label}» تأیید شد" : $"مدرک «{label}» تأیید نشد",
                    decision == AccountStatus.Approved ? (note ?? "مدرک شما بررسی و تأیید شد.") : note,
                    t.Link, "document");

            var ownerRef = (await AdminOps.OwnerRefsAsync(db, [(doc.OwnerKind, doc.OwnerId)], ct)).GetValueOrDefault(AdminOps.Key(doc.OwnerKind, doc.OwnerId));
            audit.Add("Document", id, "review:" + decision,
                $"مدرک «{label}» {OwnerKind.Label(doc.OwnerKind)} «{ownerRef?.Name ?? "#" + doc.OwnerId}»: {AccountStatus.Label(from)} ← {AccountStatus.Label(decision)}",
                new { from, to = decision, note, doc.OwnerKind, doc.OwnerId, doc.Kind, doc.ExpiresAt });
            await db.SaveChangesAsync(ct);

            TempData["ok"] = (decision == AccountStatus.Approved
                ? $"مدرک «{label}» تأیید شد."
                : $"مدرک «{label}» رد شد و علت برای صاحب مدرک ارسال شد.") + extra;
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return AdminOps.Back(this, returnUrl, "/Admin/Documents");
    }

    // ------------------------------------------------------------------
    //  مدارک منقضی
    // ------------------------------------------------------------------

    /// <summary>مدارک تأییدشده‌ای که تاریخشان گذشته و نسخهٔ تأییدشدهٔ تازه‌تری ندارند — دیرترین اول.</summary>
    public async Task<IActionResult> Expired(string? kind, string? owner, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "مدارک منقضی";
        var now = DateTime.UtcNow;
        var src = AdminOps.NotSuperseded(db, db.Documents.AsNoTracking().Where(d => d.Status == AccountStatus.Approved && d.ExpiresAt != null && d.ExpiresAt < now));
        var (ownerKind, ownerId) = AdminOps.ParseOwner(owner);
        if (ownerKind is not null) src = src.Where(d => d.OwnerKind == ownerKind);
        if (ownerId is int oid) src = src.Where(d => d.OwnerId == oid);

        ViewBag.KindCounts = await src.GroupBy(d => d.Kind).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        if (kind is not null && Kinds.Contains(kind)) src = src.Where(d => d.Kind == kind);

        var pv = await PageVm<Document>.FromAsync(src.OrderBy(d => d.ExpiresAt), page, PageLink.For(Request), ct: ct);
        var vm = await RowsAsync(pv, withRenewal: true, ct);
        ViewBag.Kind = kind is not null && Kinds.Contains(kind) ? kind : null;
        ViewBag.Owner = ownerKind is null ? null : ownerId is int id2 ? $"{ownerKind}:{id2}" : ownerKind;
        ViewBag.Owners = Owners;
        ViewBag.Kinds = Kinds;
        ViewBag.WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  هشدارها
    // ------------------------------------------------------------------

    /// <summary>مدارک تأییدشده‌ای که تا «Documents.ExpiryWarnDays» روز دیگر منقضی می‌شوند — نزدیک‌ترین اول.</summary>
    public async Task<IActionResult> Alerts(string? kind, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "هشدارهای انقضای مدارک";
        var now = DateTime.UtcNow;
        var warnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        var until = now.AddDays(warnDays);
        var src = AdminOps.NotSuperseded(db, db.Documents.AsNoTracking()
            .Where(d => d.Status == AccountStatus.Approved && d.ExpiresAt != null && d.ExpiresAt >= now && d.ExpiresAt < until));

        ViewBag.KindCounts = await src.GroupBy(d => d.Kind).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        ViewBag.ExpiredCount = await AdminOps.NotSuperseded(db, db.Documents.AsNoTracking().Where(d => d.Status == AccountStatus.Approved && d.ExpiresAt < now)).CountAsync(ct);
        if (kind is not null && Kinds.Contains(kind)) src = src.Where(d => d.Kind == kind);

        var pv = await PageVm<Document>.FromAsync(src.OrderBy(d => d.ExpiresAt), page, PageLink.For(Request), ct: ct);
        var vm = await RowsAsync(pv, withRenewal: true, ct);

        // آخرین یادآوریِ هر مدرک — از ردّ حسابرسی
        var ids = pv.Rows.Select(d => d.DocumentId).ToList();
        if (ids.Count > 0)
        {
            var reminds = await db.AuditLogs.AsNoTracking()
                .Where(a => a.Entity == "Document" && a.Action == "remind" && ids.Contains(a.EntityId))
                .GroupBy(a => a.EntityId).Select(g => new { g.Key, At = g.Max(a => a.CreatedAt) })
                .ToDictionaryAsync(x => x.Key, x => x.At, ct);
            foreach (var r in vm.Rows) r.LastRemindAt = reminds.GetValueOrDefault(r.Doc.DocumentId);
        }

        ViewBag.Kind = kind is not null && Kinds.Contains(kind) ? kind : null;
        ViewBag.Kinds = Kinds;
        ViewBag.WarnDays = warnDays;
        return View(vm);
    }

    /// <summary>یادآوری دستی به صاحب مدرک: «تا فلان تاریخ اعتبار دارد؛ نسخهٔ تازه را بارگذاری کنید».</summary>
    [HttpPost]
    public async Task<IActionResult> Remind(int id, string? returnUrl, CancellationToken ct)
    {
        var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.DocumentId == id, ct);
        if (doc is null) return NotFound();

        try
        {
            var target = await AdminOps.NotifyTargetAsync(db, doc.OwnerKind, doc.OwnerId, doc.Kind, ct)
                         ?? throw new UserError("صاحب این مدرک حساب کاربری فعالی ندارد که اعلان بگیرد.");
            var label = DocumentKind.Label(doc.Kind) + (doc.OwnerKind == OwnerKind.Vehicle ? " خودرو" : "");
            var body = doc.ExpiresAt is DateTime e
                ? e < DateTime.UtcNow
                    ? $"{label} شما در {Fa.Date(e)} منقضی شده است. نسخهٔ تمدیدشده را بارگذاری کنید تا پذیرش بار متوقف نشود."
                    : $"{label} شما فقط تا {Fa.Date(e)} اعتبار دارد. پیش از انقضا نسخهٔ تمدیدشده را بارگذاری کنید."
                : $"لطفاً نسخهٔ به‌روز {label} خود را بارگذاری کنید.";
            notify.Add(target.Kind, target.Id, "یادآوری تمدید مدرک", body, target.Link, "document");
            audit.Add("Document", id, "remind", $"یادآوری تمدید «{DocumentKind.Label(doc.Kind)}» به {OwnerKind.Label(target.Kind)} #{target.Id}",
                new { doc.OwnerKind, doc.OwnerId, doc.Kind, doc.ExpiresAt });
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"یادآوری تمدید «{DocumentKind.Label(doc.Kind)}» برای {OwnerKind.Label(target.Kind)} فرستاده شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return AdminOps.Back(this, returnUrl, "/Admin/Documents/Alerts");
    }

    // ------------------------------------------------------------------
    //  ردیف‌سازی مشترک
    // ------------------------------------------------------------------

    /// <summary>نام صاحب، نام بررسی‌کننده، سفرهای زندهٔ صاحب (رد مدرک وسط سفر باید آگاهانه باشد) و وضعیت تمدید.</summary>
    private async Task<PageVm<DocumentRowVm>> RowsAsync(PageVm<Document> pv, bool withRenewal, CancellationToken ct)
    {
        var docs = pv.Rows;
        var owners = await AdminOps.OwnerRefsAsync(db, docs.Select(d => (d.OwnerKind, d.OwnerId)), ct);
        var admins = await AdminOps.AdminNamesAsync(db, docs.Select(d => d.ReviewedByAdminId), ct);

        var driverIds = docs.Where(d => d.OwnerKind == OwnerKind.Driver).Select(d => d.OwnerId).Distinct().ToList();
        var vehicleIds = docs.Where(d => d.OwnerKind == OwnerKind.Vehicle).Select(d => d.OwnerId).Distinct().ToList();
        var liveByDriver = driverIds.Count == 0 ? [] : await db.Trips.AsNoTracking()
            .Where(t => t.DriverId != null && driverIds.Contains(t.DriverId.Value) && TripStatus.Live.Contains(t.Status))
            .GroupBy(t => t.DriverId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var liveByVehicle = vehicleIds.Count == 0 ? [] : await db.Trips.AsNoTracking()
            .Where(t => t.VehicleId != null && vehicleIds.Contains(t.VehicleId.Value) && TripStatus.Live.Contains(t.Status))
            .GroupBy(t => t.VehicleId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        // «نسخهٔ تازه‌تر در صف بررسی» — فقط برای مدارک بسته‌شده معنا دارد
        var renewals = new HashSet<int>();
        if (withRenewal && docs.Count > 0)
        {
            var ids = docs.Select(d => d.DocumentId).ToList();
            renewals = (await db.Documents.AsNoTracking()
                .Where(d => ids.Contains(d.DocumentId) &&
                            db.Documents.Any(n => n.OwnerKind == d.OwnerKind && n.OwnerId == d.OwnerId && n.Kind == d.Kind && n.Status == AccountStatus.Pending && n.UploadedAt > d.UploadedAt))
                .Select(d => d.DocumentId).ToListAsync(ct)).ToHashSet();
        }

        return new PageVm<DocumentRowVm>
        {
            Rows = docs.Select(d => new DocumentRowVm
            {
                Doc = d,
                Owner = owners.GetValueOrDefault(AdminOps.Key(d.OwnerKind, d.OwnerId)) ?? new OwnerRef($"{OwnerKind.Label(d.OwnerKind)} #{d.OwnerId}", "حساب حذف‌شده"),
                ReviewerName = d.ReviewedByAdminId is int a ? admins.GetValueOrDefault(a) : null,
                LiveTrips = d.OwnerKind == OwnerKind.Driver ? liveByDriver.GetValueOrDefault(d.OwnerId)
                    : d.OwnerKind == OwnerKind.Vehicle ? liveByVehicle.GetValueOrDefault(d.OwnerId) : 0,
                RenewalPending = renewals.Contains(d.DocumentId)
            }).ToList(),
            Page = pv.Page, PageSize = pv.PageSize, Total = pv.Total, Link = pv.Link
        };
    }
}
