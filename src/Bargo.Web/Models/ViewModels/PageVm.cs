using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Models.ViewModels;

/// <summary>فقط عددهای صفحه‌بندی — چیزی که پارشالِ _Pager لازم دارد (پارشال Razor نوع جنریک نمی‌گیرد).</summary>
public sealed class PagerVm
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 30;
    public int Total { get; init; }
    public int Pages => Total == 0 ? 1 : (int)Math.Ceiling(Total / (double)PageSize);
    public int FirstRow => Total == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int LastRow => Math.Min(Page * PageSize, Total);
    public bool HasPrev => Page > 1;
    public bool HasNext => Page < Pages;
    public Func<int, string> Link { get; init; } = _ => "#";
}

/// <summary>
/// یک صفحه از یک فهرست بلند — شمارش و برش هر دو روی سرور. فهرستی که با Take(300)
/// بریده شود یعنی ردیف ۳۰۱ به بعد اصلاً دیده نمی‌شود (درس رنگیو).
/// </summary>
public sealed class PageVm<T>
{
    public IReadOnlyList<T> Rows { get; init; } = [];
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 30;
    public int Total { get; init; }
    public Func<int, string> Link { get; init; } = _ => "#";

    public PagerVm Pager => new() { Page = Page, PageSize = PageSize, Total = Total, Link = Link };

    public static async Task<PageVm<T>> FromAsync(IQueryable<T> query, int page, Func<int, string> link,
        int pageSize = 30, CancellationToken ct = default)
    {
        var total = await query.CountAsync(ct);
        var pages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize);
        page = Math.Clamp(page, 1, pages);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PageVm<T> { Rows = rows, Page = page, PageSize = pageSize, Total = total, Link = link };
    }
}

/// <summary>ساخت نشانیِ صفحه با حفظ فیلترهای فعلی.</summary>
public static class PageLink
{
    public static Func<int, string> For(Microsoft.AspNetCore.Http.HttpRequest req) => page =>
    {
        var q = req.Query.Where(kv => !string.Equals(kv.Key, "page", StringComparison.OrdinalIgnoreCase))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}")
            .Append($"page={page}");
        return $"{req.Path}?{string.Join("&", q)}";
    };
}
