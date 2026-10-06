using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using minerva.planningfkt.models;
using minerva.planningfkt.services;
using System.Reflection.Emit;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
  .AddCookie(options => {
    options.Cookie.Name = "PlanningFKTAuth";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => {
      context.Response.StatusCode = 401;
      return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context => {
      context.Response.StatusCode = 403;
      return Task.CompletedTask;
    };
  });

builder.Services.AddAuthorization();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options => {
  options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0)));
});

builder.Services.AddSingleton<SettingsCache>();

// Daily removal of specialistica PDFs older than 1 year (see the class comment).
builder.Services.AddHostedService<SpecialisticaCleanupService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope()) {
  var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
  db.Database.Migrate();

  if (!db.Therapists.Any(t => t.Id == 1)) {
    var admin = new Therapist {
      Id = 1,
      Name = "Admin",
      Phone = string.Empty,
      IsActive = 1,
      OperatingArea = TherapistOperatingArea.Accettazione,
      OvertimeAllowed = 0,
      ModDate = DateTime.Now,
      ModUser = 1
    };

    var hasher = new PasswordHasher<Therapist>();
    admin.PasswordHash = hasher.HashPassword(admin, "CentroMinerva!1");

    db.Therapists.Add(admin);
    db.SaveChanges();
  }

  // Settings and fixed holidays - see DbSeeding (Admin stays here).
  DbSeeding.SeedRuntime(db);

  // Pianificata therapies left with no sessions at all go back to Da pianificare
  // (same rule as AppController.RevertToBeScheduledIfEmpty, for data from before it).
  var emptyScheduled = db.Therapies
    .Where(t => t.Status == TherapyStatus.Scheduled
      && !db.TherapyParts.Any(p => p.TherapyId == t.Id
        && db.TherapySlots.Any(s => s.TherapyPartId == p.Id && s.Status != TherapySlotStatus.Rescheduled)))
    .ToList();

  foreach (var therapy in emptyScheduled) {
    therapy.Status = TherapyStatus.ToBeScheduled;
    therapy.ModDate = DateTime.Now;
    therapy.ModUser = 1;
  }

  db.SaveChanges();
}

if (!app.Environment.IsDevelopment()) {
  app.UseExceptionHandler("/error");
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
