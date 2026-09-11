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
/// «مدیریت شرکت‌های حمل‌ونقل» — درخواست عضویت (با چک‌لیست مدارک)، تأیید/رد/تعلیق،
/// مجوزها (نزدیک‌ترین انقضا اول)، قراردادها و وضعیت فعالیت.
///
/// تغییر وضعیت از <see cref="AdminOps.SetAccountStatusAsync"/> می‌گذرد تا علت، اعلان
/// و ردّ حسابرسی با پروندهٔ کاربر یکسان باشد.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Users)]
public class CompaniesController(
    BargoDbContext db,
    NotificationService notify,
    AuditService audit,
    SettingsService settings) : Controller
{
    private static bool ValidStatus(string? s) => s is not null && AdminOps.AccountStatuses.Contains(s);

    private static IQueryable<Company> Search(IQueryable<Company> src, string term) =>
        term.Length == 0 ? src : src.Where(c => c.Name.Contains(term) || c.NationalId.Contains(term) || c.ManagerName.Contains(term) || c.Mobile.Contains(term));

    // ------------------------------------------------------------------
    //  درخواست عضویت
    // ------------------------------------------------------------------

    /// <summary>شرکت‌های در انتظار تأیید — قدیمی‌ترین اول، هر کدام با چک‌لیست مدارک شرکت.</summary>
    public async Task<IActionResult> Requests(string? q, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "درخواست عضویت شرکت‌ها";
        var term = AdminOps.Term(q);
        var src = Search(db.Companies.AsNoTracking().Include(c => c.City).Where(c => c.Status == AccountStatus.Pending), term);
        var pv = await PageVm<Company>.FromAsync(src.OrderBy(c => c.CreatedAt), page, PageLink.For(Request), 10, ct);

        var ids = pv.Rows.Select(c => c.CompanyId).ToList();
        var docs = ids.Count == 0
            ? []
            : await db.Documents.AsNoTracking().Where(d => d.OwnerKind == OwnerKind.Company && ids.Contains(d.OwnerId)).ToListAsync(ct);
        var owners = ids.Count == 0
            ? []
            : await db.CompanyUsers.AsNoTracking().Where(u => ids.Contains(u.CompanyId))
                .OrderByDescending(u => u.IsOwner).ThenBy(u => u.CompanyUserId).ToListAsync(ct);

        var vm = new PageVm<CompanyRequestVm>
        {
            Rows = pv.Rows.Select(c =>
            {
                var mine = docs.Where(d => d.OwnerId == c.CompanyId).ToList();
                return new CompanyRequestVm
                {
                    Company = c,
                    Owner = owners.FirstOrDefault(u => u.CompanyId == c.CompanyId),
                    Checklist = DocCheck.For(DocumentKind.ForCompany, mine),
                    OtherDocs = mine.Count(d => !DocumentKind.ForCompany.Contains(d.Kind))
                };
            }).ToList(),
            Page = pv.Page, PageSize = pv.PageSize, Total = pv.Total, Link = pv.Link
        };
        ViewBag.Q = term;
        ViewBag.WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  تأیید شرکت
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(string? q, string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "تأیید شرکت‌ها";
        var term = AdminOps.Term(q);
        var src = Search(db.Companies.AsNoTracking(), term);

        ViewBag.Counts = await src.GroupBy(c => c.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        if (ValidStatus(status)) src = src.Where(c => c.Status == status);

        // در انتظارها اول تا صف تأیید بالای صفحه باشد، بعد تازه‌ترین‌ها
        var ordered = src.OrderBy(c => c.Status == AccountStatus.Pending ? 0 : 1).ThenByDescending(c => c.CreatedAt);
        var vm = await PageVm<CompanyRow>.FromAsync(AdminOps.CompanyRows(db, ordered, DateTime.UtcNow.AddDays(-30)), page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        ViewBag.Status = ValidStatus(status) ? status : null;
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> SetStatus(int id, string status, string? reason, string? returnUrl, CancellationToken ct)
    {
        try
        {
            TempData["ok"] = await AdminOps.SetAccountStatusAsync(db, notify, audit, OwnerKind.Company, id, status, reason, ct);
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return AdminOps.Back(this, returnUrl, "/Admin/Companies");
    }

    // ------------------------------------------------------------------
    //  مجوزها
    // ------------------------------------------------------------------

    /// <summary>
    /// مجوز فعالیت هر شرکت: شمارهٔ ثبت‌شده روی حساب + آخرین مدرک «مجوز فعالیت» بارگذاری‌شده.
    /// ترتیب: نزدیک‌ترین انقضا اول؛ شرکت‌های بدون تاریخ در انتها.
    /// </summary>
    public async Task<IActionResult> Licenses(string? q, string? filter, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "مجوزهای شرکت‌ها";
        var term = AdminOps.Term(q);
        var now = DateTime.UtcNow;
        var warnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        var soon = now.AddDays(warnDays);

        var src = Search(db.Companies.AsNoTracking().Where(c => c.Status != AccountStatus.Rejected), term);
        var rows = src.Select(c => new
            {
                c.CompanyId, c.Name, c.Status, c.LicenseNo, c.LicenseExpiresAt,
                Doc = db.Documents.Where(d => d.OwnerKind == OwnerKind.Company && d.OwnerId == c.CompanyId && d.Kind == DocumentKind.CompanyLicense && d.Status != AccountStatus.Rejected)
                    .OrderByDescending(d => d.UploadedAt).FirstOrDefault()
            })
            .Select(x => new CompanyLicenseRow
            {
                CompanyId = x.CompanyId,
                Name = x.Name,
                Status = x.Status,
                LicenseNo = x.LicenseNo,
                LicenseExpiresAt = x.LicenseExpiresAt,
                DocId = x.Doc == null ? null : (int?)x.Doc.DocumentId,
                DocStatus = x.Doc == null ? null : x.Doc.Status,
                DocNumber = x.Doc == null ? null : x.Doc.Number,
                DocPath = x.Doc == null ? null : x.Doc.FilePath,
                DocExpiresAt = x.Doc == null ? null : x.Doc.ExpiresAt,
                DocUploadedAt = x.Doc == null ? null : (DateTime?)x.Doc.UploadedAt
            });

        ViewBag.Counts = new Dictionary<string, int>
        {
            ["expired"] = await rows.CountAsync(r => (r.LicenseExpiresAt ?? r.DocExpiresAt) < now, ct),
            ["soon"] = await rows.CountAsync(r => (r.LicenseExpiresAt ?? r.DocExpiresAt) >= now && (r.LicenseExpiresAt ?? r.DocExpiresAt) < soon, ct),
            ["missing"] = await rows.CountAsync(r => r.DocId == null && r.LicenseNo == null, ct),
            ["pending"] = await rows.CountAsync(r => r.DocStatus == AccountStatus.Pending, ct)
        };

        rows = filter switch
        {
            "expired" => rows.Where(r => (r.LicenseExpiresAt ?? r.DocExpiresAt) < now),
            "soon" => rows.Where(r => (r.LicenseExpiresAt ?? r.DocExpiresAt) >= now && (r.LicenseExpiresAt ?? r.DocExpiresAt) < soon),
            "missing" => rows.Where(r => r.DocId == null && r.LicenseNo == null),
            "pending" => rows.Where(r => r.DocStatus == AccountStatus.Pending),
            _ => rows
        };

        var ordered = rows
            .OrderBy(r => (r.LicenseExpiresAt ?? r.DocExpiresAt) == null ? 1 : 0)
            .ThenBy(r => r.LicenseExpiresAt ?? r.DocExpiresAt)
            .ThenBy(r => r.Name);
        var vm = await PageVm<CompanyLicenseRow>.FromAsync(ordered, page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        ViewBag.Filter = filter is "expired" or "soon" or "missing" or "pending" ? filter : null;
        ViewBag.WarnDays = warnDays;
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  قراردادها
    // ------------------------------------------------------------------

    /// <summary>قراردادهای همهٔ شرکت‌ها با مشتریانشان (ثبت‌شده در پنل شرکت) — فقط نمایش.</summary>
    public async Task<IActionResult> Contracts(string? q, string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "قراردادهای شرکت‌ها";
        var term = AdminOps.Term(q);
        var src = db.Contracts.AsNoTracking().Include(x => x.Company).Include(x => x.Customer).AsQueryable();
        if (term.Length > 0)
            src = src.Where(x => x.Title.Contains(term) || x.Company!.Name.Contains(term) || (x.Customer != null && x.Customer.Name.Contains(term)));

        ViewBag.Counts = await src.GroupBy(x => x.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        string[] statuses = ["draft", "active", "expired", "terminated"];
        if (status is not null && statuses.Contains(status)) src = src.Where(x => x.Status == status);

        var vm = await PageVm<Contract>.FromAsync(src.OrderByDescending(x => x.StartsAt).ThenByDescending(x => x.ContractId), page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        ViewBag.Status = status is not null && statuses.Contains(status) ? status : null;
        ViewBag.Statuses = statuses;
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  وضعیت فعالیت
    // ------------------------------------------------------------------

    /// <summary>
    /// شرکت‌ها از نگاه فعالیت: رانندگان، خودروها، سفر در راه، سفر ۳۰ روز اخیر، آخرین ورود.
    /// «راکد» یعنی هیچ سفری در ۳۰ روز گذشته و هیچ ورودی در ۳۰ روز گذشته.
    /// </summary>
    public async Task<IActionResult> Activity(string? q, string sort = "active", int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "وضعیت فعالیت شرکت‌ها";
        var term = AdminOps.Term(q);
        var since = DateTime.UtcNow.AddDays(-30);
        var src = Search(db.Companies.AsNoTracking().Where(c => c.Status == AccountStatus.Approved || c.Status == AccountStatus.Suspended), term);
        var rows = AdminOps.CompanyRows(db, src, since);

        ViewBag.Stats = new Dictionary<string, int>
        {
            ["live"] = await rows.CountAsync(r => r.ActiveTrips > 0, ct),
            ["busy"] = await rows.CountAsync(r => r.Trips30 > 0, ct),
            ["idle"] = await rows.CountAsync(r => r.Trips30 == 0 && (r.LastLoginAt == null || r.LastLoginAt < since), ct),
            ["suspended"] = await rows.CountAsync(r => r.Status == AccountStatus.Suspended, ct)
        };

        sort = sort is "trips30" or "idle" or "fleet" ? sort : "active";
        var ordered = sort switch
        {
            "trips30" => rows.OrderByDescending(r => r.Trips30).ThenByDescending(r => r.ActiveTrips),
            "idle" => rows.OrderBy(r => r.LastLoginAt == null ? 0 : 1).ThenBy(r => r.LastLoginAt).ThenBy(r => r.Trips30),
            "fleet" => rows.OrderByDescending(r => r.Drivers + r.Vehicles).ThenByDescending(r => r.Trips30),
            _ => rows.OrderByDescending(r => r.ActiveTrips).ThenByDescending(r => r.Trips30).ThenByDescending(r => r.LastLoginAt)
        };
        var vm = await PageVm<CompanyRow>.FromAsync(ordered, page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        ViewBag.Sort = sort;
        return View(vm);
    }
}
