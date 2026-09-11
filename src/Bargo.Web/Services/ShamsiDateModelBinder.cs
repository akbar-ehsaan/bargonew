using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Bargo.Web.Services;

/// <summary>
/// همهٔ ورودی‌های تاریخ در فرم‌ها شمسی‌اند. این بایندر «۱۴۰۵/۰۶/۲۱» را به UTC
/// (ظهر همان روز به وقت تهران) تبدیل می‌کند. در ویو از
/// <c>&lt;input type="text" data-jdate value="@Fa.DateBox(...)"&gt;</c> استفاده کنید، نه type="date".
/// </summary>
public class ShamsiDateModelBinder(bool nullable) : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext ctx)
    {
        var value = ctx.ValueProvider.GetValue(ctx.ModelName);
        if (value == ValueProviderResult.None) return Task.CompletedTask;

        ctx.ModelState.SetModelValue(ctx.ModelName, value);
        var raw = value.FirstValue;

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (nullable) ctx.Result = ModelBindingResult.Success(null);
            return Task.CompletedTask;
        }

        var parsed = Fa.ParseDateToUtc(raw);
        if (parsed is null)
        {
            ctx.ModelState.TryAddModelError(ctx.ModelName, "تاریخ معتبر نیست. قالب درست: ۱۴۰۵/۰۶/۲۱");
            return Task.CompletedTask;
        }

        ctx.Result = ModelBindingResult.Success(parsed.Value);
        return Task.CompletedTask;
    }
}

public class ShamsiDateModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        // بدنهٔ JSON (API) از این مسیر نمی‌گذرد؛ فقط فرم و کوئری‌استرینگ
        var t = context.Metadata.ModelType;
        if (t == typeof(DateTime)) return new ShamsiDateModelBinder(false);
        if (t == typeof(DateTime?)) return new ShamsiDateModelBinder(true);
        return null;
    }
}
