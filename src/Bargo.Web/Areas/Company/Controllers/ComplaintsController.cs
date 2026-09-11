using Bargo.Web.Controllers.Shared;
using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Bargo.Web.Areas.CompanyPanel.Controllers;

[Area("Company")]
[Authorize(Roles = Roles.Company)]
public class ComplaintsController(BargoDbContext db, CurrentUser me)
    : ComplaintsControllerBase(db, me);
