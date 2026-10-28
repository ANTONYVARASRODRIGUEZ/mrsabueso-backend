using Microsoft.EntityFrameworkCore;
using MrSabueso.Api.Infrastructure;

namespace MrSabueso.Api.Data;

/// <summary>Crea las tablas y carga los datos iniciales (mismos valores que frontend/src/app/core/config).</summary>
public static class Seeder
{
    public static readonly BusinessSettings DefaultSettings = new(
        "MR. SABUESO", "Veterinaria y Spa", "+51 923 399 129", "51923399129", "", "", "", "@mrsabuesoveterinaria",
        Enumerable.Range(0, 7).Select(d => new DaySchedule(d, d != 0, "09:00", "19:00")).ToList(),
        ["09:30", "11:00", "14:30", "16:00", "17:30", "18:30"], [],
        "Hola {cliente}, te recordamos la cita de {mascota} en {negocio}: {servicio}, {fecha} a las {hora}. Si no puedes asistir, avísanos para reprogramar. ¡Te esperamos!",
        "Hola {cliente}, la vacuna {vacuna} de {mascota} {estado} {fecha}. Puedes reservar su cita en línea o escribirnos por aquí. — {negocio}");

    private static List<Service> DefaultServices() =>
    [
        new() { Id = "grooming", Name = "Baño y Corte", Summary = "Estilo y limpieza para cada raza.",
            Description = "Un spa completo para que tu mascota se vea y se sienta increíble, con productos suaves y secado cuidadoso.",
            Includes = ["Baño medicado", "Corte según la raza", "Corte de uñas", "Limpieza de oídos"],
            Icon = "scissors", PriceFrom = 50, DurationMin = 90, Bookable = true, Image = "images/services/grooming.jpg" },
        new() { Id = "consulta", Name = "Consulta y Tratamientos", Summary = "Desparasitación y controles.",
            Description = "Evaluación clínica completa a cargo de médicos veterinarios, con seguimiento y tratamiento personalizado.",
            Includes = ["Examen clínico general", "Desparasitación", "Controles periódicos", "Plan de tratamiento"],
            Icon = "stethoscope", PriceFrom = 55, DurationMin = 30, Bookable = true, Image = "images/services/consulta.jpg" },
        new() { Id = "vacunacion", Name = "Vacunación", Summary = "Protección contra enfermedades.",
            Description = "Esquemas de vacunación para cachorros y adultos, con refuerzos y control de parásitos.",
            Includes = ["Esquema de vacunas", "Refuerzos anuales", "Control de parásitos", "Carnet de vacunación"],
            Icon = "syringe", PriceFrom = 70, DurationMin = 30, Bookable = true, Image = "images/services/vacunacion.jpg" },
        new() { Id = "dental", Name = "Profilaxis Dental", Summary = "Limpieza bucal ultrasónica.",
            Description = "Limpieza dental profunda con ultrasonido para prevenir sarro, mal aliento y enfermedad de encías.",
            Includes = ["Evaluación bucal", "Limpieza ultrasónica", "Pulido dental", "Recomendaciones de cuidado"],
            Icon = "tooth", PriceFrom = 110, DurationMin = 60, Bookable = true, Image = "images/services/dental.jpg" },
        new() { Id = "petshop", Name = "Pet Shop", Summary = "Alimentación y accesorios.",
            Description = "Alimento, snacks, juguetes y accesorios seleccionados para la felicidad diaria de tu mascota.",
            Includes = ["Alimento balanceado", "Snacks y premios", "Juguetes", "Collares y accesorios"],
            Icon = "bag", DurationMin = 0, Bookable = false, Image = "images/services/petshop.jpg" },
    ];

    /// <summary>Cambios de esquema sobre tablas ya creadas (EnsureCreated no altera tablas existentes).</summary>
    private static async Task MigrateAsync(AppDb db)
    {
        var hasEndTime = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Appointments' AND COLUMN_NAME = 'EndTime'")
            .SingleAsync() > 0;
        if (!hasEndTime) await db.Database.ExecuteSqlRawAsync("ALTER TABLE Appointments ADD COLUMN EndTime longtext NULL");

        // Citas antiguas sin hora de fin: inicio + duración del servicio.
        var missing = await db.Appointments.Where(a => a.EndTime == null).ToListAsync();
        if (missing.Count == 0) return;
        var services = await db.Services.ToDictionaryAsync(s => s.Id);
        foreach (var a in missing) a.EndTime = Scheduling.EndOf(a.Time, Scheduling.DurationOf(services.GetValueOrDefault(a.ServiceId)));
        await db.SaveChangesAsync();
    }

    public static async Task RunAsync(AppDb db, IConfiguration cfg)
    {
        await db.Database.EnsureCreatedAsync();
        await MigrateAsync(db);

        if (!await db.Services.AnyAsync()) db.Services.AddRange(DefaultServices());
        if (await db.Settings.FindAsync(1) is null) await SettingsRepo.SaveAsync(db, DefaultSettings);

        // Administrador inicial (cámbialo en appsettings: Seed:AdminEmail / Seed:AdminPassword).
        var adminEmail = Text.Email(cfg["Seed:AdminEmail"] ?? "admin@mrsabueso.pe");
        if (!await db.Users.AnyAsync(u => u.Role == "admin"))
            db.Users.Add(new User
            {
                Id = "u-admin", FullName = "Administrador", Email = adminEmail, Role = "admin", CreatedAt = DateTime.UtcNow,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(cfg["Seed:AdminPassword"] ?? "Admin1234"),
            });

        // Cuenta demo de cliente con dos mascotas (solo si se pide en la configuración).
        if (cfg.GetValue<bool>("Seed:DemoData") && !await db.Users.AnyAsync(u => u.Id == "u-demo"))
        {
            db.Users.Add(new User
            {
                Id = "u-demo", FullName = "Anali Rojas", Email = "anali.rojas@gmail.com", Role = "client", CreatedAt = DateTime.UtcNow,
                ConsentAt = DateTime.UtcNow, ConsentSource = "web", PasswordHash = BCrypt.Net.BCrypt.HashPassword("Demo1234"),
            });
            db.Pets.AddRange(
                new Pet { Id = "pet-1", OwnerId = "u-demo", Name = "Boby", Species = "dog", Breed = "Beagle", Sex = "male", PhotoUrl = DemoData.PhotoOf("Boby") },
                new Pet { Id = "pet-2", OwnerId = "u-demo", Name = "Mimi", Species = "cat", Breed = "Siamés", Sex = "female", PhotoUrl = DemoData.PhotoOf("Mimi") });
        }

        await db.SaveChangesAsync();

        if (cfg.GetValue<bool>("Seed:DemoData"))
        {
            await DemoData.RunAsync(db);
            await DemoData.FillPhotosAsync(db);
            // Datos de contacto PROVISIONALES para la vista previa: reemplázalos en Panel → Configuración.
            var s = await SettingsRepo.GetAsync(db);
            if (s.Address == "" && s.Email == "")
                await SettingsRepo.SaveAsync(db, s with { Email = "hola@mrsabueso.pe", Address = "Av. Primavera 1250, Santiago de Surco, Lima" });
        }
    }
}
