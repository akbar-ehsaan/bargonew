using System.Text.Json;
using Bargo.Web.Data;
using Bargo.Web.Models.Entities;

namespace Bargo.Web.Services;

/// <summary>
/// ردّ حسابرسی برای اقدامات حساس: تأیید/رد مدرک، تعلیق حساب، تسویه، تعویض راننده،
/// تغییر تعرفه. در اختلاف باید بشود گفت چه کسی، کی، چه چیزی را عوض کرد.
/// </summary>
public class AuditService(BargoDbContext db, CurrentUser me, IHttpContextAccessor http)
{
    /// <summary>رویداد را در صف ذخیره می‌گذارد؛ SaveChanges بر عهدهٔ فراخواننده است.</summary>
    public void Add(string entity, int entityId, string action, string? summary = null, object? detail = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Entity = entity,
            EntityId = entityId,
            Action = action,
            ActorRole = me.IsAuthenticated ? me.Role : "system",
            ActorId = me.IsAuthenticated ? me.Id : 0,
            ActorName = me.IsAuthenticated ? me.Name : null,
            Summary = summary,
            DetailJson = detail == null ? null : JsonSerializer.Serialize(detail),
            Ip = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow
        });
    }

    public async Task LogAsync(string entity, int entityId, string action, string? summary = null, object? detail = null)
    {
        Add(entity, entityId, action, summary, detail);
        await db.SaveChangesAsync();
    }
}
