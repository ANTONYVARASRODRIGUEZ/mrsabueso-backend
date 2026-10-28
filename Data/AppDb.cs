using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MrSabueso.Api.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Pet> Pets => Set<Pet>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<MedicalEntry> Medical => Set<MedicalEntry>();
    public DbSet<Vaccination> Vaccines => Set<Vaccination>();
    public DbSet<ContactMessage> Messages => Set<ContactMessage>();
    public DbSet<ResetToken> ResetTokens => Set<ResetToken>();
    public DbSet<SettingsRow> Settings => Set<SettingsRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Email);
        b.Entity<Pet>().HasIndex(p => p.OwnerId);
        b.Entity<Appointment>().HasIndex(a => a.Date);
        b.Entity<Appointment>().HasIndex(a => a.UserId);
        b.Entity<Review>().HasIndex(r => r.AppointmentId).IsUnique();
        b.Entity<MedicalEntry>().HasIndex(m => m.PetId);
        b.Entity<Vaccination>().HasIndex(v => v.PetId);
        b.Entity<ResetToken>().HasKey(t => t.Token);
        b.Entity<SettingsRow>().Property(s => s.Id).ValueGeneratedNever();
        b.Entity<SettingsRow>().Property(s => s.Json).HasColumnType("longtext");
        b.Entity<Pet>().Property(p => p.PhotoUrl).HasColumnType("longtext"); // puede ser data-URL

        b.Entity<Service>().Property(s => s.Includes).HasColumnType("text").HasConversion(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>(),
            new ValueComparer<List<string>>((a, c) => a!.SequenceEqual(c!), v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s)), v => v.ToList()));
    }

    // Las fechas se guardan en UTC; al leerlas se marcan como UTC para que el JSON salga con "Z".
    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<DateTime>().HaveConversion<UtcConverter>();
        c.Properties<DateTime?>().HaveConversion<UtcConverter>();
        c.Properties<decimal>().HavePrecision(10, 2);
    }

    private class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
