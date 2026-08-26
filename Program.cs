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

  if (!db.Settings.Any(s => s.Key == "AvailabilityStart")) {
    db.Settings.Add(new Setting { Key = "AvailabilityStart", Value = 28, ModDate = DateTime.Now, ModUser = 1 }); // 07:00
  }

  if (!db.Settings.Any(s => s.Key == "AvailabilityEnd")) {
    db.Settings.Add(new Setting { Key = "AvailabilityEnd", Value = 76, ModDate = DateTime.Now, ModUser = 1 }); // 19:00
  }

  if (!db.Settings.Any(s => s.Key == "RepartoTherapyStartingTime")) {
    db.Settings.Add(new Setting { Key = "RepartoTherapyStartingTime", Value = 5, ModDate = DateTime.Now, ModUser = 1 });
  }

  if (!db.Settings.Any(s => s.Key == "PalestraCoveringRepartoStartingTime")) {
    db.Settings.Add(new Setting { Key = "PalestraCoveringRepartoStartingTime", Value = 5, ModDate = DateTime.Now, ModUser = 1 });
  }

  if (!db.Settings.Any(s => s.Key == "RepartoCapacityWarningThreshold")) {
    db.Settings.Add(new Setting { Key = "RepartoCapacityWarningThreshold", Value = 75, ModDate = DateTime.Now, ModUser = 1 });
  }

  if (!db.Settings.Any(s => s.Key == "PacchettoScontoMinimo")) {
    db.Settings.Add(new Setting { Key = "PacchettoScontoMinimo", Value = 5, ModDate = DateTime.Now, ModUser = 1 });
  }

  if (!db.Settings.Any(s => s.Key == "PacchettoScontoMassimo")) {
    db.Settings.Add(new Setting { Key = "PacchettoScontoMassimo", Value = 25, ModDate = DateTime.Now, ModUser = 1 });
  }



	db.SaveChanges();

  var fixedHolidays = new (string Name, int Month, int Day)[] {
    ("Capodanno", 1, 1),
    ("Epifania", 1, 6),
    ("Liberazione", 4, 25),
    ("Festa del Lavoro", 5, 1),
    ("Festa della Repubblica", 6, 2),
    ("Ferragosto", 8, 15),
    ("San Gennaro", 9, 19),
    ("Ognissanti", 11, 1),
    ("Immacolata", 12, 8),
    ("Natale", 12, 25),
    ("Santo Stefano", 12, 26)
  };

  foreach (var holiday in fixedHolidays) {
    var exists = db.Vacations.Any(v =>
      v.IsYearIndependent == 1 && v.Month == holiday.Month && v.Day == holiday.Day);

    if (!exists) {
      db.Vacations.Add(new Vacation {
        Name = holiday.Name,
        IsYearIndependent = 1,
        Month = holiday.Month,
        Day = holiday.Day,
        IsSeeded = 1
      });
    }
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
