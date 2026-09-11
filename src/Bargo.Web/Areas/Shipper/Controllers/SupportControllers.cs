using Bargo.Web.Controllers.Shared;
using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Bargo.Web.Areas.ShipperPanel.Controllers;

// زیرکلاس‌های چندخطیِ ماژول‌های مشترک — همهٔ رفتار و ویوها در Controllers/Shared و Views/Shared/Panel.

[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
public class TicketsController(BargoDbContext db, CurrentUser me, SettingsService settings)
    : TicketsControllerBase(db, me, settings)
{
}

[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
public class NotificationsController(BargoDbContext db, CurrentUser me)
    : NotificationsControllerBase(db, me)
{
}

[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
public class MessagesController(BargoDbContext db, CurrentUser me, NotificationService notify)
    : MessagesControllerBase(db, me, notify)
{
}

[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
public class ComplaintsController(BargoDbContext db, CurrentUser me)
    : ComplaintsControllerBase(db, me)
{
}
