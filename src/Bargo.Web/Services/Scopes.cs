using Bargo.Web.Models.Entities;

namespace Bargo.Web.Services;

/// <summary>
/// فیلترهای مالکیت — شرط دسترسی همیشه داخل خودِ کوئری است، نه یک if پس از خواندن
/// ردیف (همان قاعدهٔ رنگیو). شناسهٔ حدس‌زده هیچ ردیفی برنمی‌گرداند و پاسخ ۴۰۴ می‌شود.
/// </summary>
public static class Scopes
{
    /// <summary>ردیف‌های یک صاحب: <c>db.WalletTransactions.Of(me.Owner)</c></summary>
    public static IQueryable<T> Of<T>(this IQueryable<T> q, (string Kind, int Id) owner) where T : class, IOwned =>
        q.Where(x => x.OwnerKind == owner.Kind && x.OwnerId == owner.Id);

    /// <summary>سفرهایی که این انجام‌دهنده حق دیدنشان را دارد.</summary>
    public static IQueryable<Trip> VisibleTo(this IQueryable<Trip> q, Actor a) => a.Kind switch
    {
        Roles.Driver => q.Where(t => t.DriverId == a.Id),
        Roles.Shipper => q.Where(t => t.Load!.ShipperId == a.Id),
        Roles.Company => q.Where(t => t.CompanyId == a.CompanyId || t.Load!.CompanyId == a.CompanyId),
        Roles.Admin => q,
        _ => q.Where(_ => false)
    };

    /// <summary>بارهایی که این انجام‌دهنده صاحبشان است (صاحب بار، یا شرکتی که بار را ثبت کرده).</summary>
    public static IQueryable<Load> OwnedBy(this IQueryable<Load> q, Actor a) => a.Kind switch
    {
        Roles.Shipper => q.Where(l => l.ShipperId == a.Id),
        Roles.Company => q.Where(l => l.CompanyId == a.CompanyId),
        Roles.Admin => q,
        _ => q.Where(_ => false)
    };

    /// <summary>
    /// بازار بار: منتشرشده، هنوز پیشنهاد می‌پذیرد، منقضی نشده؛ درخواستِ مستقیمِ یک
    /// شرکت فقط برای همان شرکت دیده می‌شود.
    /// </summary>
    public static IQueryable<Load> Market(this IQueryable<Load> q, int? forCompanyId = null) =>
        q.Where(l => LoadStatus.Market.Contains(l.Status)
                     && (l.ExpiresAt == null || l.ExpiresAt > DateTime.UtcNow)
                     && (l.TargetCompanyId == null || l.TargetCompanyId == forCompanyId));
}
