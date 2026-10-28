namespace MrSabueso.Api.Data;

// Mismas formas que frontend/src/app/core/models: ids string y estados en texto ('no_show', 'paid', …).

public class User
{
    public string Id { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public string Role { get; set; } = "client";
    public DateTime CreatedAt { get; set; }
    public bool Active { get; set; } = true;
    public DateTime? ConsentAt { get; set; }
    public string? ConsentSource { get; set; }
    public string? PasswordHash { get; set; }
}

public class Pet
{
    public string Id { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Species { get; set; } = "dog";
    public string Breed { get; set; } = "";
    public string? Sex { get; set; }
    public string? BirthDate { get; set; }
    public string? PhotoUrl { get; set; }
    public string? Notes { get; set; }
}

public class Service
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Includes { get; set; } = [];
    public string Icon { get; set; } = "paw";
    public decimal? PriceFrom { get; set; }
    public int DurationMin { get; set; }
    public bool Bookable { get; set; }
    public bool Active { get; set; } = true;
    public string Image { get; set; } = "";
}

public class Appointment
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string UserId { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string? CustomerPhone { get; set; }
    public string PetId { get; set; } = "";
    public string PetName { get; set; } = "";
    public string ServiceId { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public decimal? Price { get; set; }
    public string Date { get; set; } = "";
    public string Time { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string Payment { get; set; } = "pending";
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public int RescheduleCount { get; set; }
    public DateTime? ReminderSentAt { get; set; }
}

public class Review
{
    public string Id { get; set; } = "";
    public string AppointmentId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string ServiceId { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string PetName { get; set; } = "";
    public int Rating { get; set; }
    public string Comment { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool Hidden { get; set; }
    public string? Reply { get; set; }
}

public class MedicalEntry
{
    public string Id { get; set; } = "";
    public string PetId { get; set; } = "";
    public string Date { get; set; } = "";
    public string Kind { get; set; } = "consulta";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Vet { get; set; } = "";
    public decimal? WeightKg { get; set; }
}

public class Vaccination
{
    public string Id { get; set; } = "";
    public string PetId { get; set; } = "";
    public string Vaccine { get; set; } = "";
    public string Date { get; set; } = "";
    public string? NextDue { get; set; }
    public string? Lot { get; set; }
    public string Vet { get; set; } = "";
    public string? Notes { get; set; }
    public DateTime? ReminderSentAt { get; set; }
}

public class ContactMessage
{
    public string Id { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string ServiceId { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = "new";
}

public class ResetToken
{
    public string Token { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Fila única (Id = 1) con la configuración del negocio en JSON.</summary>
public class SettingsRow
{
    public int Id { get; set; } = 1;
    public string Json { get; set; } = "{}";
}

public record DaySchedule(int Day, bool Open, string From, string To);

public record BusinessSettings(
    string Name, string Tagline, string Phone, string Whatsapp, string Email, string Address, string MapsUrl, string Facebook,
    List<DaySchedule> Hours, List<string> Slots, List<string> ClosedDates, string ApptReminderTemplate, string VaccineReminderTemplate);
