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
/// «مدیریت کاربران» — فهرست و پروندهٔ هر سه نوع حساب (راننده، صاحب بار، شرکت)،
/// کاربران مسدودشده و صف احراز هویت صاحبان بار.
///
/// پرونده عمداً همه‌چیز را یک‌جا نشان می‌دهد (مدارک، خودرو، سفر، کیف پول، امتیاز،
/// تخلف، شکایت): تصمیمِ «تأیید» یا «تعلیق» بدون دیدنِ سابقه، تصمیمِ کورکورانه است.
/// تغییر وضعیت از <see cref="AdminOps.SetAccountStatusAsync"/> می‌گذرد تا علت،
/// اعلان و ردّ حسابرسی هیچ‌وقت از قلم نیفتد.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Users)]
public class UsersController(
    BargoDbContext db,
    NotificationService notify,
    AuditService audit,
    DriverReadiness readiness,
    SettingsService settings,
    CurrentUser me) : Controller
{
    private static bool ValidStatus(string? s) => s is not null && AdminOps.AccountStatuses.Contains(s);

    // ------------------------------------------------------------------
    //  فهرست‌ها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Drivers(string? q, string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "رانندگان";
        var term = AdminOps.Term(q);
        var src = db.Drivers.AsNoTracking();
        if (term.Length > 0)
            src = src.Where(d => (d.FirstName + " " + d.LastName).Contains(term) || d.Mobile.Contains(term) || d.NationalCode.Contains(term));

        // شمارندهٔ سربرگ‌ها با جستجو ولی بدون فیلتر وضعیت — تا معلوم باشد در هر وضعیت چند نتیجه هست
        ViewBag.Counts = await src.GroupBy(d => d.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        if (ValidStatus(status)) src = src.Where(d => d.Status == status);

        var vm = await PageVm<DriverRow>.FromAsync(AdminOps.DriverRows(db, src.OrderByDescending(d => d.CreatedAt)), page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        ViewBag.Status = ValidStatus(status) ? status : null;
        return View(vm);
    }

    public async Task<IActionResult> Shippers(string? q, string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "صاحبان بار";
        var term = AdminOps.Term(q);
        var src = db.Shippers.AsNoTracking();
        if (term.Length > 0)
            src = src.Where(s => s.FullName.Contains(term) || (s.BusinessName != null && s.BusinessName.Contains(term)) ||
                                 s.Mobile.Contains(term) || (s.NationalCode != null && s.NationalCode.Contains(term)) ||
                                 (s.NationalId != null && s.NationalId.Contains(term)));

        ViewBag.Counts = await src.GroupBy(s => s.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        if (ValidStatus(status)) src = src.Where(s => s.Status == status);

        var rows = src.OrderByDescending(s => s.CreatedAt).Select(s => new ShipperRow
        {
            ShipperId = s.ShipperId,
            Name = s.Kind == "business" && s.BusinessName != null && s.BusinessName != "" ? s.BusinessName : s.FullName,
            FullName = s.FullName,
            Kind = s.Kind,
            Mobile = s.Mobile,
            NationalCode = s.NationalCode,
            NationalId = s.NationalId,
            City = s.City!.Name,
            Status = s.Status,
            VerifyStatus = s.VerifyStatus,
            WalletBalance = s.WalletBalance,
            Loads = db.Loads.Count(l => l.ShipperId == s.ShipperId),
            CreatedAt = s.CreatedAt
        });
        var vm = await PageVm<ShipperRow>.FromAsync(rows, page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        ViewBag.Status = ValidStatus(status) ? status : null;
        return View(vm);
    }

    public async Task<IActionResult> Companies(string? q, string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "شرکت‌های حمل‌ونقل";
        var term = AdminOps.Term(q);
        var src = db.Companies.AsNoTracking();
        if (term.Length > 0)
            src = src.Where(c => c.Name.Contains(term) || c.NationalId.Contains(term) || c.ManagerName.Contains(term) || c.Mobile.Contains(term));

        ViewBag.Counts = await src.GroupBy(c => c.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        if (ValidStatus(status)) src = src.Where(c => c.Status == status);

        var vm = await PageVm<CompanyRow>.FromAsync(
            AdminOps.CompanyRows(db, src.OrderByDescending(c => c.CreatedAt), DateTime.UtcNow.AddDays(-30)), page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        ViewBag.Status = ValidStatus(status) ? status : null;
        return View(vm);
    }

    /// <summary>
    /// حساب‌های تعلیق‌شده و ردشدهٔ هر سه نوع در یک جدول. سه جدولِ جدا را نمی‌شود در SQL
    /// صفحه‌بندیِ مشترک کرد بی‌آنکه شکلشان یکی شود؛ این مجموعه ذاتاً کوچک است، پس در
    /// حافظه ادغام و صفحه‌بندی می‌شود — هیچ ردیفی بریده نمی‌شود.
    /// </summary>
    public async Task<IActionResult> Blocked(string? q, string? kind, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "کاربران مسدودشده";
        var term = AdminOps.Term(q);
        string[] blocked = [AccountStatus.Suspended, AccountStatus.Rejected];

        var drivers = db.Drivers.AsNoTracking().Where(d => blocked.Contains(d.Status));
        var shippers = db.Shippers.AsNoTracking().Where(s => blocked.Contains(s.Status));
        var companies = db.Companies.AsNoTracking().Where(c => blocked.Contains(c.Status));
        if (term.Length > 0)
        {
            drivers = drivers.Where(d => (d.FirstName + " " + d.LastName).Contains(term) || d.Mobile.Contains(term) || d.NationalCode.Contains(term));
            shippers = shippers.Where(s => s.FullName.Contains(term) || (s.BusinessName != null && s.BusinessName.Contains(term)) || s.Mobile.Contains(term));
            companies = companies.Where(c => c.Name.Contains(term) || c.Mobile.Contains(term) || c.NationalId.Contains(term));
        }

        var all = new List<BlockedRow>();
        all.AddRange(await drivers.Select(d => new BlockedRow
        {
            Kind = OwnerKind.Driver, Id = d.DriverId, Name = d.FirstName + " " + d.LastName, Mobile = d.Mobile,
            Status = d.Status, Reason = d.StatusReason, CreatedAt = d.CreatedAt
        }).ToListAsync(ct));
        all.AddRange(await shippers.Select(s => new BlockedRow
        {
            Kind = OwnerKind.Shipper, Id = s.ShipperId,
            Name = s.Kind == "business" && s.BusinessName != null && s.BusinessName != "" ? s.BusinessName : s.FullName,
            Mobile = s.Mobile, Status = s.Status, Reason = s.StatusReason, CreatedAt = s.CreatedAt
        }).ToListAsync(ct));
        all.AddRange(await companies.Select(c => new BlockedRow
        {
            Kind = OwnerKind.Company, Id = c.CompanyId, Name = c.Name, Mobile = c.Mobile,
            Status = c.Status, Reason = c.StatusReason, CreatedAt = c.CreatedAt
        }).ToListAsync(ct));

        ViewBag.Counts = all.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count());
        var filtered = kind is OwnerKind.Driver or OwnerKind.Shipper or OwnerKind.Company ? all.Where(r => r.Kind == kind).ToList() : all;

        const int size = 30;
        var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)size));
        page = Math.Clamp(page, 1, pages);
        var vm = new PageVm<BlockedRow>
        {
            Rows = filtered.OrderBy(r => r.Status).ThenByDescending(r => r.CreatedAt).Skip((page - 1) * size).Take(size).ToList(),
            Page = page, PageSize = size, Total = filtered.Count, Link = PageLink.For(Request)
        };
        ViewBag.Q = term;
        ViewBag.Kind = kind is OwnerKind.Driver or OwnerKind.Shipper or OwnerKind.Company ? kind : null;
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  احراز هویت صاحبان بار
    // ------------------------------------------------------------------

    /// <summary>
    /// صاحب بار بی‌درنگ فعال است؛ احراز هویت فقط سقف‌ها را باز می‌کند. پس این صف
    /// «ورود» کسی را نمی‌بندد و ترتیبش «قدیمی‌ترین مدرکِ بارگذاری‌شده اول» است.
    /// </summary>
    public async Task<IActionResult> Verifications(string? q, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "احراز هویت صاحبان بار";
        var term = AdminOps.Term(q);
        var src = db.Shippers.AsNoTracking().Include(s => s.City).Where(s => s.VerifyStatus == AccountStatus.Pending);
        if (term.Length > 0)
            src = src.Where(s => s.FullName.Contains(term) || (s.BusinessName != null && s.BusinessName.Contains(term)) || s.Mobile.Contains(term));

        var pv = await PageVm<Shipper>.FromAsync(
            src.OrderBy(s => db.Documents.Where(d => d.OwnerKind == OwnerKind.Shipper && d.OwnerId == s.ShipperId).Max(d => (DateTime?)d.UploadedAt))
               .ThenBy(s => s.CreatedAt),
            page, PageLink.For(Request), 15, ct);

        var ids = pv.Rows.Select(s => s.ShipperId).ToList();
        var docs = ids.Count == 0
            ? []
            : await db.Documents.AsNoTracking().Where(d => d.OwnerKind == OwnerKind.Shipper && ids.Contains(d.OwnerId))
                .OrderByDescending(d => d.UploadedAt).ToListAsync(ct);

        var vm = new PageVm<ShipperVerifyVm>
        {
            Rows = pv.Rows.Select(s => new ShipperVerifyVm { Shipper = s, Documents = docs.Where(d => d.OwnerId == s.ShipperId).ToList() }).ToList(),
            Page = pv.Page, PageSize = pv.PageSize, Total = pv.Total, Link = pv.Link
        };
        ViewBag.Q = term;
        ViewBag.WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> VerifyShipper(int id, string decision, string? note, string? returnUrl, CancellationToken ct)
    {
        var s = await db.Shippers.FirstOrDefaultAsync(x => x.ShipperId == id, ct);
        if (s is null) return NotFound();

        try
        {
            if (decision is not (AccountStatus.Approved or AccountStatus.Rejected)) throw new UserError("تصمیم انتخاب‌شده معتبر نیست.");
            note = AdminOps.Note(note);
            if (decision == AccountStatus.Rejected && note is null)
                throw new UserError("برای رد احراز هویت، علت را بنویسید تا صاحب بار بداند کدام مدرک را اصلاح کند.");
            if (s.VerifyStatus == decision)
                throw new UserError($"احراز هویت این صاحب بار از قبل «{AccountStatus.Label(decision)}» است.");

            var docs = await db.Documents.Where(d => d.OwnerKind == OwnerKind.Shipper && d.OwnerId == id).ToListAsync(ct);
            if (decision == AccountStatus.Approved && !docs.Any(d => d.Status != AccountStatus.Rejected))
                throw new UserError("این صاحب بار مدرک معتبری بارگذاری نکرده است؛ تأیید بدون مدرک ممکن نیست.");

            var now = DateTime.UtcNow;
            var from = s.VerifyStatus;
            s.VerifyStatus = decision;
            s.VerifiedAt = decision == AccountStatus.Approved ? now : null;

            // مدارکِ در انتظارِ همین پرونده با همان تصمیم بسته می‌شوند — مدیر آن‌ها را در
            // همین صف دیده است؛ وگرنه مدرکِ «در انتظار» یک صاحب بارِ تأییدشده در صف مدارک می‌ماند.
            var closed = new List<int>();
            foreach (var d in docs.Where(d => d.Status == AccountStatus.Pending))
            {
                d.Status = decision;
                d.ReviewNote = note;
                d.ReviewedAt = now;
                d.ReviewedByAdminId = me.Id;
                closed.Add(d.DocumentId);
            }

            notify.Add(OwnerKind.Shipper, id,
                decision == AccountStatus.Approved ? "احراز هویت شما تأیید شد" : "احراز هویت شما تأیید نشد",
                decision == AccountStatus.Approved ? "امکانات و سقف‌های حساب تأییدشده برایتان باز شد." : note,
                "/Shipper/Profile/Verification", "document");
            audit.Add("Shipper", id, "verify:" + decision,
                $"احراز هویت صاحب بار «{s.DisplayName}»: {AccountStatus.Label(decision)}",
                new { from, to = decision, note, documents = closed });
            await db.SaveChangesAsync(ct);

            TempData["ok"] = decision == AccountStatus.Approved
                ? $"احراز هویت «{s.DisplayName}» تأیید شد."
                : $"احراز هویت «{s.DisplayName}» رد شد و علت برای او ارسال شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return AdminOps.Back(this, returnUrl, "/Admin/Users/Verifications");
    }

    // ------------------------------------------------------------------
    //  پرونده‌ها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Driver(int id, CancellationToken ct)
    {
        var d = await db.Drivers.AsNoTracking().Include(x => x.City).ThenInclude(c => c!.Province).Include(x => x.Company)
            .FirstOrDefaultAsync(x => x.DriverId == id, ct);
        if (d is null) return NotFound();
        ViewData["Title"] = "پروندهٔ راننده — " + d.FullName;

        var vm = new DriverDetailVm { Driver = d, WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct) };
        vm.Documents = await db.Documents.AsNoTracking().Where(x => x.OwnerKind == OwnerKind.Driver && x.OwnerId == id)
            .OrderByDescending(x => x.UploadedAt).ToListAsync(ct);
        vm.Checklist = DocCheck.For(DocumentKind.ForDriver, vm.Documents);
        vm.Vehicles = await db.Vehicles.AsNoTracking().Include(v => v.VehicleType).Where(v => v.DriverId == id)
            .OrderByDescending(v => v.VehicleId).ToListAsync(ct);
        var vehicleIds = vm.Vehicles.Select(v => v.VehicleId).ToList();
        vm.VehicleDocuments = vehicleIds.Count == 0
            ? []
            : await db.Documents.AsNoTracking().Where(x => x.OwnerKind == OwnerKind.Vehicle && vehicleIds.Contains(x.OwnerId))
                .OrderByDescending(x => x.UploadedAt).ToListAsync(ct);
        vm.Issues = await readiness.CheckAsync(id, null, ct);

        var trips = db.Trips.AsNoTracking().Where(t => t.DriverId == id);
        vm.TripTotal = await trips.CountAsync(ct);
        vm.TripDone = await trips.CountAsync(t => TripStatus.Done.Contains(t.Status), ct);
        vm.TripCancelled = await trips.CountAsync(t => t.Status == TripStatus.Cancelled, ct);
        vm.Trips = await AdminOps.TripRows(trips.OrderByDescending(t => t.CreatedAt)).Take(15).ToListAsync(ct);

        vm.Txns = await db.WalletTransactions.AsNoTracking().Where(t => t.OwnerKind == OwnerKind.Driver && t.OwnerId == id)
            .OrderByDescending(t => t.WalletTransactionId).Take(10).ToListAsync(ct);
        vm.Payouts = await db.PayoutRequests.AsNoTracking().Where(p => p.OwnerKind == OwnerKind.Driver && p.OwnerId == id)
            .OrderByDescending(p => p.CreatedAt).Take(5).ToListAsync(ct);
        vm.Ratings = await db.Ratings.AsNoTracking().Include(r => r.Trip).Where(r => r.ToKind == OwnerKind.Driver && r.ToId == id)
            .OrderByDescending(r => r.CreatedAt).Take(10).ToListAsync(ct);
        vm.Violations = await db.Violations.AsNoTracking().Where(v => v.DriverId == id).OrderByDescending(v => v.CreatedAt).ToListAsync(ct);
        vm.Complaints = await db.Complaints.AsNoTracking().Include(c => c.Trip)
            .Where(c => (c.FromKind == OwnerKind.Driver && c.FromId == id) || (c.AgainstKind == OwnerKind.Driver && c.AgainstId == id))
            .OrderByDescending(c => c.CreatedAt).Take(10).ToListAsync(ct);
        vm.AdminNames = await AdminOps.AdminNamesAsync(db,
            vm.Documents.Concat(vm.VehicleDocuments).Select(x => x.ReviewedByAdminId).Concat(vm.Violations.Select(v => (int?)v.CreatedByAdminId)), ct);
        return View(vm);
    }

    public async Task<IActionResult> Shipper(int id, CancellationToken ct)
    {
        var s = await db.Shippers.AsNoTracking().Include(x => x.City).ThenInclude(c => c!.Province).FirstOrDefaultAsync(x => x.ShipperId == id, ct);
        if (s is null) return NotFound();
        ViewData["Title"] = "پروندهٔ صاحب بار — " + s.DisplayName;

        var vm = new ShipperDetailVm { Shipper = s, WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct) };
        vm.Documents = await db.Documents.AsNoTracking().Where(x => x.OwnerKind == OwnerKind.Shipper && x.OwnerId == id)
            .OrderByDescending(x => x.UploadedAt).ToListAsync(ct);
        var loads = db.Loads.AsNoTracking().Where(l => l.ShipperId == id);
        vm.LoadTotal = await loads.CountAsync(ct);
        vm.Loads = await AdminOps.LoadRows(loads.OrderByDescending(l => l.CreatedAt)).Take(15).ToListAsync(ct);
        vm.TripTotal = await db.Trips.CountAsync(t => t.Load!.ShipperId == id, ct);
        vm.Txns = await db.WalletTransactions.AsNoTracking().Where(t => t.OwnerKind == OwnerKind.Shipper && t.OwnerId == id)
            .OrderByDescending(t => t.WalletTransactionId).Take(10).ToListAsync(ct);
        vm.Ratings = await db.Ratings.AsNoTracking().Include(r => r.Trip).Where(r => r.ToKind == OwnerKind.Shipper && r.ToId == id)
            .OrderByDescending(r => r.CreatedAt).Take(10).ToListAsync(ct);
        vm.Complaints = await db.Complaints.AsNoTracking().Include(c => c.Trip)
            .Where(c => (c.FromKind == OwnerKind.Shipper && c.FromId == id) || (c.AgainstKind == OwnerKind.Shipper && c.AgainstId == id))
            .OrderByDescending(c => c.CreatedAt).Take(10).ToListAsync(ct);
        vm.AdminNames = await AdminOps.AdminNamesAsync(db, vm.Documents.Select(x => x.ReviewedByAdminId), ct);
        return View(vm);
    }

    public async Task<IActionResult> Company(int id, CancellationToken ct)
    {
        var c = await db.Companies.AsNoTracking().Include(x => x.City).ThenInclude(x => x!.Province).FirstOrDefaultAsync(x => x.CompanyId == id, ct);
        if (c is null) return NotFound();
        ViewData["Title"] = "پروندهٔ شرکت — " + c.Name;

        var vm = new CompanyDetailVm { Company = c, WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct) };
        vm.Documents = await db.Documents.AsNoTracking().Where(x => x.OwnerKind == OwnerKind.Company && x.OwnerId == id)
            .OrderByDescending(x => x.UploadedAt).ToListAsync(ct);
        vm.Checklist = DocCheck.For(DocumentKind.ForCompany, vm.Documents);
        vm.Users = await db.CompanyUsers.AsNoTracking().Where(u => u.CompanyId == id)
            .OrderByDescending(u => u.IsOwner).ThenBy(u => u.Name).ToListAsync(ct);
        vm.Drivers = await AdminOps.DriverRows(db, db.Drivers.AsNoTracking().Where(d => d.CompanyId == id).OrderBy(d => d.LastName)).ToListAsync(ct);
        vm.Vehicles = await db.Vehicles.AsNoTracking().Include(v => v.VehicleType).Where(v => v.CompanyId == id)
            .OrderBy(v => v.PlateNo).ToListAsync(ct);

        var trips = db.Trips.AsNoTracking().Where(t => t.CompanyId == id || t.Load!.CompanyId == id);
        vm.TripTotal = await trips.CountAsync(ct);
        vm.ActiveTrips = await trips.CountAsync(t => TripStatus.Live.Contains(t.Status), ct);
        vm.Trips = await AdminOps.TripRows(trips.OrderByDescending(t => t.CreatedAt)).Take(15).ToListAsync(ct);

        vm.Txns = await db.WalletTransactions.AsNoTracking().Where(t => t.OwnerKind == OwnerKind.Company && t.OwnerId == id)
            .OrderByDescending(t => t.WalletTransactionId).Take(10).ToListAsync(ct);
        vm.Ratings = await db.Ratings.AsNoTracking().Include(r => r.Trip).Where(r => r.ToKind == OwnerKind.Company && r.ToId == id)
            .OrderByDescending(r => r.CreatedAt).Take(10).ToListAsync(ct);
        vm.Violations = await db.Violations.AsNoTracking().Include(v => v.Driver).Where(v => v.CompanyId == id)
            .OrderByDescending(v => v.CreatedAt).Take(20).ToListAsync(ct);
        vm.Complaints = await db.Complaints.AsNoTracking().Include(x => x.Trip)
            .Where(x => (x.FromKind == OwnerKind.Company && x.FromId == id) || (x.AgainstKind == OwnerKind.Company && x.AgainstId == id))
            .OrderByDescending(x => x.CreatedAt).Take(10).ToListAsync(ct);
        vm.Contracts = await db.Contracts.AsNoTracking().Include(x => x.Customer).Where(x => x.CompanyId == id)
            .OrderByDescending(x => x.StartsAt).Take(10).ToListAsync(ct);
        vm.AdminNames = await AdminOps.AdminNamesAsync(db, vm.Documents.Select(x => x.ReviewedByAdminId), ct);
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  تغییر وضعیت حساب
    // ------------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> SetStatus(string kind, int id, string status, string? reason, string? returnUrl, CancellationToken ct)
    {
        try
        {
            TempData["ok"] = await AdminOps.SetAccountStatusAsync(db, notify, audit, kind, id, status, reason, ct);
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        var fallback = kind switch
        {
            OwnerKind.Shipper => $"/Admin/Users/Shipper/{id}",
            OwnerKind.Company => $"/Admin/Users/Company/{id}",
            _ => $"/Admin/Users/Driver/{id}"
        };
        return AdminOps.Back(this, returnUrl, fallback);
    }
}
