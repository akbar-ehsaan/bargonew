using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Services;

/// <summary>
/// هشدار خودکار انقضای مدارک (بیمه، معاینه فنی، گواهینامه، کارت هوشمند، مجوز شرکت).
/// روزی یک بار اجرا می‌شود و برای هر مدرک در هر هفته حداکثر یک اعلان می‌سازد.
/// بستنِ پذیرش بار کارِ این سرویس نیست — آن را DriverReadiness در لحظه می‌سنجد.
/// </summary>
public class ExpiryAlertService(IServiceScopeFactory scopes, ILogger<ExpiryAlertService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // اجرای اول کمی دیرتر، تا مهاجرت پایگاه‌داده در راه‌اندازی تمام شده باشد
        try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (OperationCanceledException) { return; }

        while (!stop.IsCancellationRequested)
        {
            try { await RunOnceAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "هشدار انقضای مدارک اجرا نشد.");
            }
            try { await Task.Delay(TimeSpan.FromHours(24), stop); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BargoDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var notify = scope.ServiceProvider.GetRequiredService<NotificationService>();

        var warnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        var limit = DateTime.UtcNow.AddDays(warnDays);
        var weekAgo = DateTime.UtcNow.AddDays(-7);

        var docs = await db.Documents.AsNoTracking()
            .Where(d => d.Status == AccountStatus.Approved && d.ExpiresAt != null && d.ExpiresAt < limit)
            .ToListAsync(ct);

        foreach (var d in docs)
        {
            var link = $"doc:{d.DocumentId}";
            // اعلان با Link یکتا نشانه‌گذاری می‌شود تا هر روز تکرار نشود
            (string kind, int id, string panelLink)? owner = null;
            if (d.OwnerKind == OwnerKind.Driver) owner = (OwnerKind.Driver, d.OwnerId, "/Driver/Vehicle/Alerts");
            else if (d.OwnerKind == OwnerKind.Company) owner = (OwnerKind.Company, d.OwnerId, "/Company/Account/Documents");
            else if (d.OwnerKind == OwnerKind.Vehicle)
            {
                var v = await db.Vehicles.AsNoTracking().Where(x => x.VehicleId == d.OwnerId)
                    .Select(x => new { x.CompanyId, x.DriverId }).FirstOrDefaultAsync(ct);
                if (v?.CompanyId is int cid) owner = (OwnerKind.Company, cid, "/Company/Fleet/Alerts");
                else if (v?.DriverId is int did) owner = (OwnerKind.Driver, did, "/Driver/Vehicle/Alerts");
            }
            if (owner is null) continue;

            var already = await db.Notifications.AnyAsync(n => n.OwnerKind == owner.Value.kind && n.OwnerId == owner.Value.id &&
                                                                n.Kind == "document" && n.Body == link && n.CreatedAt > weekAgo, ct);
            if (already) continue;

            var expired = d.ExpiresAt < DateTime.UtcNow;
            notify.Add(owner.Value.kind, owner.Value.id,
                expired ? $"{DocumentKind.Label(d.Kind)} منقضی شده است" : $"{DocumentKind.Label(d.Kind)} تا {Fa.Date(d.ExpiresAt)} اعتبار دارد",
                link, owner.Value.panelLink, "document");
        }
        await db.SaveChangesAsync(ct);
    }
}
