using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.CompanyPanel.Controllers;

/// <summary>
/// «مشتریان» — دو منبع:
///   ۱) صاحبان بارِ بارگو که شرکت برایشان حمل کرده (از روی سفرها، بی‌نیاز از ثبت دستی)
///   ۲) دفتر مشتریان شرکت (CompanyCustomer): حقیقی یا شرکتی، عضو بارگو یا بیرونی، با قرارداد.
///
/// محدودیت فعلی مدل: Load ستون CompanyCustomerId ندارد، پس سوابق سفرِ یک مشتریِ
/// بیرونی (باری که شرکت برای او ثبت کرده) قابل تفکیک نیست؛ برای مشتریِ عضو بارگو
/// (ShipperId پر) سوابق از روی سفرهای همان صاحب بار ساخته می‌شود.
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[RequireCompanyPermission(CompanyPermission.Customers)]
public class CustomersController(BargoDbContext db, CurrentUser me, DocumentStorage storage, AuditService audit) : Controller
{
    // ------------------------------------------------------------------
    //  فهرست
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(string? kind, string? q, int page = 1, CancellationToken ct = default)
    {
        var cid = me.CompanyId;
        kind = kind is CompanyLabels.Corporate or CompanyLabels.Person ? kind : null;
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var ql = Fa.Latin(q);
        ViewBag.Kind = kind;
        ViewBag.Q = q;
        await CountsAsync(cid, ct);

        if (kind is null)
        {
            ViewData["Title"] = "صاحبان بار";
            // فقط صاحبان باری که دست‌کم یک سفر پرداخت‌شده با شرکت دارند — هویت پیش از پرداخت مخفی است (Privacy)
            var trips = db.Trips.AsNoTracking().Where(t => t.CompanyId == cid && t.IsPaid && t.Load!.ShipperId != null);
            if (q is not null)
                trips = trips.Where(t => t.Load!.Shipper!.FullName.Contains(q)
                                         || (t.Load.Shipper.BusinessName != null && t.Load.Shipper.BusinessName.Contains(q))
                                         || t.Load.Shipper.Mobile.Contains(ql));

            var agg = trips.GroupBy(t => t.Load!.ShipperId)
                .Select(g => new ShipperAggRow
                {
                    ShipperId = g.Key ?? 0,
                    Trips = g.Count(),
                    Done = g.Count(t => t.Status == TripStatus.Delivered || t.Status == TripStatus.Settled),
                    Cancelled = g.Count(t => t.Status == TripStatus.Cancelled),
                    Fare = g.Sum(t => t.Status != TripStatus.Cancelled ? t.Fare : 0),
                    FirstTripAt = g.Min(t => t.CreatedAt),
                    LastTripAt = g.Max(t => t.CreatedAt)
                })
                .OrderByDescending(r => r.LastTripAt);

            var vm = await PageVm<ShipperAggRow>.FromAsync(agg, page, PageLink.For(Request), ct: ct);
            var ids = vm.Rows.Select(r => r.ShipperId).ToList();
            ViewBag.Shippers = await ShipperInfoQuery(db.Shippers.Where(s => ids.Contains(s.ShipperId)))
                .ToDictionaryAsync(s => s.ShipperId, ct);
            ViewBag.Saved = (await db.CompanyCustomers.AsNoTracking()
                    .Where(c => c.CompanyId == cid && c.ShipperId != null && ids.Contains(c.ShipperId!.Value))
                    .Select(c => new { S = c.ShipperId!.Value, c.CompanyCustomerId }).ToListAsync(ct))
                .GroupBy(x => x.S).ToDictionary(g => g.Key, g => g.First().CompanyCustomerId);
            return View(vm);
        }

        ViewData["Title"] = kind == CompanyLabels.Corporate ? "مشتریان شرکتی" : "مشتریان حقیقی";
        var cq = db.CompanyCustomers.AsNoTracking().Where(c => c.CompanyId == cid && c.Kind == kind);
        if (q is not null)
            cq = cq.Where(c => c.Name.Contains(q) || (c.Mobile != null && c.Mobile.Contains(ql)) || (c.NationalId != null && c.NationalId.Contains(ql)));

        var rows = cq.OrderBy(c => c.Name).Select(c => new CustomerRow
        {
            CompanyCustomerId = c.CompanyCustomerId, Name = c.Name, Kind = c.Kind, Mobile = c.Mobile, NationalId = c.NationalId,
            Note = c.Note, ShipperId = c.ShipperId, CreatedAt = c.CreatedAt,
            Contracts = db.Contracts.Count(k => k.CompanyId == cid && k.CompanyCustomerId == c.CompanyCustomerId),
            ActiveContracts = db.Contracts.Count(k => k.CompanyId == cid && k.CompanyCustomerId == c.CompanyCustomerId && k.Status == "active")
        });
        return View("List", await PageVm<CustomerRow>.FromAsync(rows, page, PageLink.For(Request), ct: ct));
    }

    private async Task CountsAsync(int cid, CancellationToken ct)
    {
        ViewBag.ShipperCount = await db.Trips.Where(t => t.CompanyId == cid && t.Load!.ShipperId != null)
            .Select(t => t.Load!.ShipperId).Distinct().CountAsync(ct);
        var kinds = await db.CompanyCustomers.Where(c => c.CompanyId == cid)
            .GroupBy(c => c.Kind).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        ViewBag.PersonCount = kinds.FirstOrDefault(k => k.Key == CompanyLabels.Person)?.N ?? 0;
        ViewBag.CorporateCount = kinds.FirstOrDefault(k => k.Key == CompanyLabels.Corporate)?.N ?? 0;
    }

    private static IQueryable<ShipperInfo> ShipperInfoQuery(IQueryable<Shipper> q) => q.AsNoTracking().Select(s => new ShipperInfo
    {
        ShipperId = s.ShipperId,
        Name = s.Kind == "business" && s.BusinessName != null && s.BusinessName != "" ? s.BusinessName : s.FullName,
        Mobile = s.Mobile, Kind = s.Kind, City = s.City != null ? s.City.Name : null,
        RatingAvg = s.RatingAvg, RatingCount = s.RatingCount
    });

    // ------------------------------------------------------------------
    //  افزودن / حذف مشتری
    // ------------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> Add(string? name, string? kind, string? mobile, string? nationalId, string? note, int? shipperId,
        CancellationToken ct)
    {
        var cid = me.CompanyId;
        kind = kind == CompanyLabels.Corporate ? CompanyLabels.Corporate : CompanyLabels.Person;

        try
        {
            var c = new CompanyCustomer { CompanyId = cid, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() };

            if (shipperId is int sid)
            {
                // فقط صاحب باری که با این شرکت کار کرده — شناسهٔ دلخواه اطلاعات کسی را لو نمی‌دهد
                var worked = await db.Trips.AnyAsync(t => t.CompanyId == cid && t.Load!.ShipperId == sid, ct);
                var sh = worked ? await db.Shippers.AsNoTracking().FirstOrDefaultAsync(s => s.ShipperId == sid, ct) : null;
                if (sh is null) return NotFound();
                if (await db.CompanyCustomers.AnyAsync(x => x.CompanyId == cid && x.ShipperId == sid, ct))
                    throw new UserError("این صاحب بار قبلاً در فهرست مشتریان شرکت است.");

                c.ShipperId = sid;
                c.Name = sh.DisplayName;
                c.Mobile = sh.Mobile;
                c.Kind = sh.Kind == "business" ? CompanyLabels.Corporate : CompanyLabels.Person;
                c.NationalId = sh.Kind == "business" ? sh.NationalId : sh.NationalCode;
            }
            else
            {
                name = (name ?? "").Trim();
                if (name.Length is < 2 or > 150) throw new UserError("نام مشتری را (۲ تا ۱۵۰ نویسه) وارد کنید.");

                string? mob = null;
                if (!string.IsNullOrWhiteSpace(mobile))
                {
                    mob = Fa.NormMobile(mobile);
                    if (mob.Length == 0) throw new UserError("شمارهٔ موبایل معتبر نیست.");
                }

                string? nid = null;
                if (!string.IsNullOrWhiteSpace(nationalId))
                {
                    nid = Fa.Latin(nationalId);
                    if (kind == CompanyLabels.Corporate && !IranId.IsCompanyNationalId(nid))
                        throw new UserError("شناسهٔ ملی شرکت (۱۱ رقم) معتبر نیست.");
                    if (kind == CompanyLabels.Person && !IranId.IsNationalCode(nid))
                        throw new UserError("کد ملی (۱۰ رقم) معتبر نیست.");
                    if (await db.CompanyCustomers.AnyAsync(x => x.CompanyId == cid && x.NationalId == nid, ct))
                        throw new UserError("مشتری دیگری با همین شناسه در فهرست شرکت هست.");
                }

                c.Name = name;
                c.Kind = kind;
                c.Mobile = mob;
                c.NationalId = nid;
            }

            db.CompanyCustomers.Add(c);
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"«{c.Name}» به مشتریان شرکت افزوده شد.";
            return RedirectToAction(nameof(Index), new { kind = c.Kind });
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
            return shipperId is null ? RedirectToAction(nameof(Index), new { kind }) : RedirectToAction(nameof(Index));
        }
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var c = await db.CompanyCustomers.FirstOrDefaultAsync(x => x.CompanyCustomerId == id && x.CompanyId == cid, ct);
        if (c is null) return NotFound();

        // قراردادِ ثبت‌شده سند است و نباید با حذف مشتری بی‌صاحب بماند
        var contracts = await db.Contracts.CountAsync(k => k.CompanyId == cid && k.CompanyCustomerId == id, ct);
        if (contracts > 0)
        {
            TempData["err"] = $"برای «{c.Name}» {Fa.N(contracts)} قرارداد ثبت شده است؛ مشتریِ دارای قرارداد حذف نمی‌شود.";
            return RedirectToAction(nameof(Index), new { kind = c.Kind });
        }

        audit.Add("CompanyCustomer", c.CompanyCustomerId, "delete", $"حذف مشتری «{c.Name}»",
            new { c.CompanyId, c.Name, c.Kind, c.Mobile, c.NationalId, c.ShipperId });
        db.CompanyCustomers.Remove(c);
        await db.SaveChangesAsync(ct);
        TempData["ok"] = $"«{c.Name}» از مشتریان حذف شد.";
        return RedirectToAction(nameof(Index), new { kind = c.Kind });
    }

    // ------------------------------------------------------------------
    //  سوابق همکاری
    // ------------------------------------------------------------------

    public async Task<IActionResult> History(int? customerId, int? shipperId, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "سوابق همکاری";
        var cid = me.CompanyId;
        // تب‌های بالای صفحه (_Tabs) شمارنده‌ها را از ViewBag می‌خوانند
        await CountsAsync(cid, ct);

        ViewBag.ShipperOptions = (await db.Trips.AsNoTracking()
                .Where(t => t.CompanyId == cid && t.Load!.ShipperId != null)
                .Select(t => new
                {
                    Id = t.Load!.ShipperId!.Value,
                    Name = t.Load.Shipper!.Kind == "business" && t.Load.Shipper.BusinessName != null && t.Load.Shipper.BusinessName != ""
                        ? t.Load.Shipper.BusinessName : t.Load.Shipper.FullName
                })
                .Distinct().ToListAsync(ct))
            .OrderBy(x => x.Name).Select(x => new SelectItem { Id = x.Id, Text = x.Name }).ToList();
        ViewBag.CustomerOptions = await db.CompanyCustomers.AsNoTracking().Where(c => c.CompanyId == cid)
            .OrderBy(c => c.Name).Select(c => new SelectItem { Id = c.CompanyCustomerId, Text = c.Name }).ToListAsync(ct);

        var trips = db.Trips.AsNoTracking().Where(t => t.CompanyId == cid);
        CompanyCustomer? customer = null;
        ShipperInfo? shipper = null;

        if (customerId is int ccid)
        {
            customer = await db.CompanyCustomers.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyCustomerId == ccid && c.CompanyId == cid, ct);
            if (customer is null) return NotFound();
            var customerName = customer.Name;
            ViewBag.Contracts = await db.Contracts.AsNoTracking()
                .Where(k => k.CompanyId == cid && k.CompanyCustomerId == ccid)
                .OrderByDescending(k => k.StartsAt)
                .Select(k => new ContractRow
                {
                    ContractId = k.ContractId, Title = k.Title, CustomerId = k.CompanyCustomerId, CustomerName = customerName,
                    StartsAt = k.StartsAt, EndsAt = k.EndsAt, Amount = k.Amount, FilePath = k.FilePath, Status = k.Status,
                    Note = k.Note, CreatedAt = k.CreatedAt
                }).ToListAsync(ct);
            shipperId = customer.ShipperId;
            if (shipperId is null) trips = trips.Where(_ => false);
        }

        if (shipperId is int sid)
        {
            shipper = await ShipperInfoQuery(db.Shippers.Where(s => s.ShipperId == sid)).FirstOrDefaultAsync(ct);
            var worked = await db.Trips.AnyAsync(t => t.CompanyId == cid && t.Load!.ShipperId == sid, ct);
            if (shipper is null || (!worked && customer is null)) return NotFound();
            trips = trips.Where(t => t.Load!.ShipperId == sid);
        }

        var sum = await trips.GroupBy(_ => 1).Select(g => new
        {
            Trips = g.Count(),
            Done = g.Count(t => t.Status == TripStatus.Delivered || t.Status == TripStatus.Settled),
            Cancelled = g.Count(t => t.Status == TripStatus.Cancelled),
            Fare = g.Sum(t => t.Status != TripStatus.Cancelled ? t.Fare : 0),
            Share = g.Sum(t => t.Status == TripStatus.Delivered || t.Status == TripStatus.Settled ? t.CarrierShare : 0),
            First = g.Min(t => t.CreatedAt),
            Last = g.Max(t => t.CreatedAt)
        }).FirstOrDefaultAsync(ct);

        ViewBag.Customer = customer;
        ViewBag.Shipper = shipper;
        ViewBag.CustomerId = customerId;
        ViewBag.ShipperId = shipperId;
        ViewBag.SumTrips = sum?.Trips ?? 0;
        ViewBag.SumDone = sum?.Done ?? 0;
        ViewBag.SumCancelled = sum?.Cancelled ?? 0;
        ViewBag.SumFare = sum?.Fare ?? 0L;
        ViewBag.SumShare = sum?.Share ?? 0L;
        ViewBag.First = sum?.First;
        ViewBag.Last = sum?.Last;

        var vm = await PageVm<CompanyTripRow>.FromAsync(trips.OrderByDescending(t => t.TripId).AsRows(partyIsCarrier: false),
            page, PageLink.For(Request), ct: ct);
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  قراردادها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Contracts(string? status, int? customerId, string? q, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "قراردادها";
        var cid = me.CompanyId;
        await CountsAsync(cid, ct);
        status = CompanyLabels.ContractStatuses.Contains(status) ? status : null;
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        var all = db.Contracts.AsNoTracking().Where(k => k.CompanyId == cid);
        var now = DateTime.UtcNow;
        var soon = now.AddDays(30);
        ViewBag.ActiveCount = await all.CountAsync(k => k.Status == "active", ct);
        ViewBag.ActiveAmount = await all.Where(k => k.Status == "active").SumAsync(k => (long?)k.Amount, ct) ?? 0;
        ViewBag.ExpiringCount = await all.CountAsync(k => k.Status == "active" && k.EndsAt != null && k.EndsAt >= now && k.EndsAt < soon, ct);
        ViewBag.OverdueCount = await all.CountAsync(k => k.Status == "active" && k.EndsAt != null && k.EndsAt < now, ct);
        ViewBag.StatusCounts = (await all.GroupBy(k => k.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.N);

        var list = all;
        if (status is not null) list = list.Where(k => k.Status == status);
        if (customerId is int cc) list = list.Where(k => k.CompanyCustomerId == cc);
        if (q is not null) list = list.Where(k => k.Title.Contains(q) || (k.Customer != null && k.Customer.Name.Contains(q)));

        var rows = list.OrderByDescending(k => k.StartsAt).ThenByDescending(k => k.ContractId).Select(k => new ContractRow
        {
            ContractId = k.ContractId, Title = k.Title, CustomerId = k.CompanyCustomerId,
            CustomerName = k.Customer != null ? k.Customer.Name : null,
            StartsAt = k.StartsAt, EndsAt = k.EndsAt, Amount = k.Amount, FilePath = k.FilePath,
            Status = k.Status, Note = k.Note, CreatedAt = k.CreatedAt
        });

        ViewBag.Status = status;
        ViewBag.CustomerId = customerId;
        ViewBag.Q = q;
        ViewBag.Customers = await db.CompanyCustomers.AsNoTracking().Where(c => c.CompanyId == cid)
            .OrderBy(c => c.Name).Select(c => new SelectItem { Id = c.CompanyCustomerId, Text = c.Name }).ToListAsync(ct);
        return View(await PageVm<ContractRow>.FromAsync(rows, page, PageLink.For(Request), ct: ct));
    }

    [HttpPost]
    public async Task<IActionResult> AddContract(int? customerId, string? title, DateTime? startsAt, DateTime? endsAt, string? amountToman,
        string? status, string? note, IFormFile? file, CancellationToken ct)
    {
        var cid = me.CompanyId;
        try
        {
            if (!ModelState.IsValid) throw new UserError("تاریخ معتبر نیست. قالب درست: ۱۴۰۵/۰۶/۲۱");
            if (customerId is not int cc || !await db.CompanyCustomers.AnyAsync(c => c.CompanyCustomerId == cc && c.CompanyId == cid, ct))
                throw new UserError("مشتری قرارداد را انتخاب کنید.");
            title = (title ?? "").Trim();
            if (title.Length is < 3 or > 150) throw new UserError("عنوان قرارداد را (۳ تا ۱۵۰ نویسه) بنویسید.");
            if (startsAt is null) throw new UserError("تاریخ شروع قرارداد را وارد کنید.");
            if (endsAt is DateTime e && e < startsAt) throw new UserError("تاریخ پایان نمی‌تواند پیش از تاریخ شروع باشد.");

            long? amount = null;
            if (!string.IsNullOrWhiteSpace(amountToman))
            {
                amount = Fa.ParseToman(amountToman);
                if (amount is null or <= 0) throw new UserError("مبلغ قرارداد معتبر نیست.");
            }

            var st = status == "draft" ? "draft" : "active";
            if (st == "active" && endsAt is DateTime end && end < DateTime.UtcNow) st = "expired";

            var path = await storage.SaveAsync(file, "contracts", ct);
            var k = new Contract
            {
                CompanyId = cid, CompanyCustomerId = cc, Title = title, StartsAt = startsAt.Value, EndsAt = endsAt,
                Amount = amount, FilePath = path, Status = st, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
            };
            db.Contracts.Add(k);
            await db.SaveChangesAsync(ct);
            audit.Add("Contract", k.ContractId, "create", $"ثبت قرارداد «{k.Title}»", new { k.CompanyCustomerId, k.Amount, k.Status });
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"قرارداد «{k.Title}» ثبت شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Contracts));
    }

    [HttpPost]
    public async Task<IActionResult> SetContractStatus(int id, string status, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var k = await db.Contracts.FirstOrDefaultAsync(x => x.ContractId == id && x.CompanyId == cid, ct);
        if (k is null) return NotFound();
        if (!CompanyLabels.ContractStatuses.Contains(status))
        {
            TempData["err"] = "وضعیت قرارداد نامعتبر است.";
            return RedirectToAction(nameof(Contracts));
        }
        if (k.Status == status) return RedirectToAction(nameof(Contracts));
        if (k.Status is "expired" or "terminated" && status is "draft")
        {
            TempData["err"] = "قرارداد پایان‌یافته به پیش‌نویس برنمی‌گردد.";
            return RedirectToAction(nameof(Contracts));
        }

        audit.Add("Contract", k.ContractId, "status", $"وضعیت قرارداد «{k.Title}»: {CompanyLabels.ContractStatus(k.Status)} ← {CompanyLabels.ContractStatus(status)}",
            new { from = k.Status, to = status });
        k.Status = status;
        await db.SaveChangesAsync(ct);
        TempData["ok"] = $"وضعیت قرارداد «{k.Title}» به «{CompanyLabels.ContractStatus(status)}» تغییر کرد.";
        return RedirectToAction(nameof(Contracts));
    }
}
