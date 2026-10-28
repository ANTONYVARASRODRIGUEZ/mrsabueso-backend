using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MrSabueso.Api.Data;
using MrSabueso.Api.Infrastructure;

namespace MrSabueso.Api.Endpoints;

public record NewAppointment(string PetId, string ServiceId, string Date, string Time, string? Notes);
public record AdminAppointment(string UserId, string PetId, string ServiceId, string Date, string Time, string? Notes, string? Status);
public record RescheduleInput(string Date, string Time);
public record StatusInput(string Status);
public record PaymentInput(string Payment);
public record NotesInput(string? Notes);
public record EndTimeInput(string EndTime);

/// <summary>Reemplaza a core/state/appointments.store.ts.</summary>
public static class AppointmentEndpoints
{
    private static readonly string[] Statuses = ["pending", "confirmed", "attended", "no_show", "cancelled"];
    // Evita que dos reservas simultáneas tomen el mismo horario (una sola instancia de la API).
    private static readonly SemaphoreSlim BookingLock = new(1, 1);

    public static void MapAppointments(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/appointments").RequireAuthorization();

        g.MapGet("/", async (ClaimsPrincipal me, AppDb db) =>
        {
            var q = db.Appointments.AsQueryable();
            if (!me.IsAdmin()) { var id = me.Id(); q = q.Where(a => a.UserId == id); }
            return await q.OrderByDescending(a => a.Date).ThenByDescending(a => a.Time).ToListAsync();
        });

        // Horarios ocupados y libres de una fecha (para el selector de horario).
        g.MapGet("/slots", async (string date, string? excludeId, AppDb db) =>
        {
            var settings = await SettingsRepo.GetAsync(db);
            var busy = await BusyTimes(db, date, excludeId);
            var free = Scheduling.SlotsForDate(settings, date).Where(t => !busy.Contains(t) && !IsPast(date, t)).ToList();
            return new { date, busy, free };
        }).AllowAnonymous();

        g.MapPost("/", async (NewAppointment input, ClaimsPrincipal me, AppDb db) =>
            await Create(db, me.RequireId(), input.PetId, input.ServiceId, input.Date, input.Time, input.Notes, "pending", me.IsAdmin()));

        g.MapPost("/admin", async (AdminAppointment input, AppDb db) =>
        {
            var status = input.Status ?? "confirmed";
            if (!Statuses.Contains(status)) throw ApiError.BadRequest("Estado inválido.");
            return await Create(db, input.UserId, input.PetId, input.ServiceId, input.Date, input.Time, input.Notes, status, true);
        }).RequireAuthorization("admin");

        g.MapPost("/{id}/cancel", (string id, ClaimsPrincipal me, AppDb db) =>
            Patch(db, me, id, a => a.Status = "cancelled", ownerAllowed: true));

        g.MapPost("/{id}/reschedule", async (string id, RescheduleInput input, ClaimsPrincipal me, AppDb db) =>
        {
            await BookingLock.WaitAsync();
            try
            {
                await AssertSlot(db, input.Date, input.Time, id);
                return await Patch(db, me, id, a =>
                {
                    // Se conserva la duración que tenía la cita.
                    var dur = a.EndTime is { } end && Scheduling.IsHhmm(end) ? Scheduling.Minutes(end) - Scheduling.Minutes(a.Time) : 30;
                    a.Date = input.Date; a.Time = input.Time; a.RescheduleCount++;
                    a.EndTime = Scheduling.EndOf(input.Time, Math.Max(dur, 5));
                    if (!me.IsAdmin()) a.Status = "pending";
                }, ownerAllowed: true);
            }
            finally { BookingLock.Release(); }
        });

        g.MapPatch("/{id}/status", (string id, StatusInput input, ClaimsPrincipal me, AppDb db) =>
            Statuses.Contains(input.Status) ? Patch(db, me, id, a => a.Status = input.Status) : throw ApiError.BadRequest("Estado inválido."))
            .RequireAuthorization("admin");

        g.MapPatch("/{id}/payment", (string id, PaymentInput input, ClaimsPrincipal me, AppDb db) =>
            input.Payment is "pending" or "paid" ? Patch(db, me, id, a => a.Payment = input.Payment) : throw ApiError.BadRequest("Pago inválido."))
            .RequireAuthorization("admin");

        g.MapPatch("/{id}/notes", (string id, NotesInput input, ClaimsPrincipal me, AppDb db) =>
            Patch(db, me, id, a => a.Notes = Text.Clean(input.Notes))).RequireAuthorization("admin");

        g.MapPatch("/{id}/end-time", (string id, EndTimeInput input, ClaimsPrincipal me, AppDb db) =>
            Patch(db, me, id, a =>
            {
                if (!Scheduling.IsHhmm(input.EndTime) || Scheduling.Minutes(input.EndTime) <= Scheduling.Minutes(a.Time))
                    throw ApiError.BadRequest("La hora de fin debe ser posterior a la hora de inicio.");
                a.EndTime = input.EndTime;
            })).RequireAuthorization("admin");

        g.MapPost("/{id}/reminder", (string id, ClaimsPrincipal me, AppDb db) =>
            Patch(db, me, id, a => a.ReminderSentAt = DateTime.UtcNow)).RequireAuthorization("admin");
    }

    private static bool IsPast(string date, string time) =>
        string.CompareOrdinal(date, Clock.Today) < 0 || (date == Clock.Today && string.CompareOrdinal(time, Clock.Hhmm) <= 0);

    private static async Task<List<string>> BusyTimes(AppDb db, string date, string? excludeId) =>
        await db.Appointments.Where(a => a.Date == date && a.Id != excludeId && !Scheduling.Inactive.Contains(a.Status))
            .Select(a => a.Time).ToListAsync();

    private static async Task AssertSlot(AppDb db, string date, string time, string? excludeId = null)
    {
        var settings = await SettingsRepo.GetAsync(db);
        if (!Scheduling.IsOpenDay(settings, date)) throw ApiError.BadRequest("Ese día no atendemos. Elige otra fecha.");
        if (IsPast(date, time)) throw ApiError.BadRequest("Ese horario ya pasó.");
        if (!Scheduling.SlotsForDate(settings, date).Contains(time) || (await BusyTimes(db, date, excludeId)).Contains(time))
            throw new ApiError(409, "Ese horario acaba de ser reservado. Elige otro.");
    }

    private static async Task<Appointment> Create(AppDb db, string userId, string petId, string serviceId, string date, string time,
        string? notes, string status, bool byAdmin)
    {
        await BookingLock.WaitAsync();
        try
        {
            await AssertSlot(db, date, time);
            var user = await db.Users.FindAsync(userId);
            var pet = await db.Pets.FindAsync(petId);
            var service = await db.Services.FindAsync(serviceId);
            if (user is null || pet is null || service is null) throw ApiError.BadRequest("Datos de la reserva incompletos.");
            if (pet.OwnerId != userId) throw ApiError.BadRequest("La mascota no pertenece a este cliente.");
            if (!byAdmin && (!service.Active || !service.Bookable)) throw ApiError.BadRequest("Este servicio no se reserva en línea.");

            var codes = await db.Appointments.Select(a => a.Code).ToListAsync();
            var seq = codes.Select(c => int.TryParse(c.Split('-').ElementAtOrDefault(1), out var n) ? n : 0).Append(100).Max() + 1;
            var created = new Appointment
            {
                Id = Ids.New("apt"), Code = $"#75-{seq}", UserId = userId, CustomerName = user.FullName, CustomerPhone = user.Phone,
                PetId = pet.Id, PetName = $"{pet.Name} ({pet.Breed})", ServiceId = service.Id, ServiceName = service.Name, Price = service.PriceFrom,
                Date = date, Time = time, EndTime = Scheduling.EndOf(time, Scheduling.DurationOf(service)), Status = status, Payment = "pending", Notes = Text.Clean(notes), CreatedAt = DateTime.UtcNow,
            };
            db.Appointments.Add(created);
            await db.SaveChangesAsync();
            return created;
        }
        finally { BookingLock.Release(); }
    }

    private static async Task<Appointment> Patch(AppDb db, ClaimsPrincipal me, string id, Action<Appointment> change, bool ownerAllowed = false)
    {
        var a = await db.Appointments.FindAsync(id) ?? throw ApiError.NotFound("Cita no encontrada.");
        if (!me.IsAdmin() && !(ownerAllowed && a.UserId == me.Id())) throw ApiError.Forbidden();
        change(a);
        await db.SaveChangesAsync();
        return a;
    }
}
