using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MrSabueso.Api.Data;
using MrSabueso.Api.Infrastructure;

namespace MrSabueso.Api.Endpoints;

public record PetInput(string Name, string Species, string Breed, string? Sex, string? BirthDate, string? PhotoUrl, string? Notes, string? OwnerId);
public record ReviewInput(string AppointmentId, int Rating, string Comment);
public record ReplyInput(string? Reply);
public record ContactInput(string FullName, string Phone, string ServiceId, string Message);
public record MessageStatusInput(string Status);
public record CustomerInput(string FullName, string Phone, string? Email, bool Consent);
public record ActiveInput(bool Active);
public record AdminInput(string FullName, string Email, string Password);

/// <summary>Reemplaza a los stores de pets, services, settings, reviews, medical, messages y customers.</summary>
public static class ResourceEndpoints
{
    public static void MapResources(this IEndpointRouteBuilder app)
    {
        MapPets(app.MapGroup("/api/pets").RequireAuthorization());
        MapServices(app.MapGroup("/api/services"));
        MapSettings(app.MapGroup("/api/settings"));
        MapReviews(app.MapGroup("/api/reviews"));
        MapMedical(app.MapGroup("/api").RequireAuthorization());
        MapMessages(app);
        MapCustomers(app.MapGroup("/api/customers").RequireAuthorization("admin"));
        MapAdmins(app.MapGroup("/api/admins").RequireAuthorization("admin"));

        // Copia de seguridad en JSON (sin contraseñas).
        app.MapGet("/api/admin/export", async (AppDb db) => new
        {
            exportedAt = DateTime.UtcNow,
            users = (await db.Users.ToListAsync()).Select(u => u.Public()),
            pets = await db.Pets.ToListAsync(), appointments = await db.Appointments.ToListAsync(), reviews = await db.Reviews.ToListAsync(),
            medical = await db.Medical.ToListAsync(), vaccines = await db.Vaccines.ToListAsync(), messages = await db.Messages.ToListAsync(),
            services = await db.Services.ToListAsync(), settings = await SettingsRepo.GetAsync(db),
        }).RequireAuthorization("admin");
    }

    private static void MapPets(RouteGroupBuilder g)
    {
        // Admin: todas (o las de ?ownerId=). Cliente: solo las suyas.
        g.MapGet("/", async (string? ownerId, ClaimsPrincipal me, AppDb db) =>
        {
            var owner = me.IsAdmin() ? ownerId : me.Id();
            return await db.Pets.Where(p => owner == null || p.OwnerId == owner).OrderBy(p => p.Name).ToListAsync();
        });

        g.MapGet("/{id}", (string id, ClaimsPrincipal me, AppDb db) => db.PetFor(me, id));

        g.MapPost("/", async (PetInput input, ClaimsPrincipal me, AppDb db) =>
        {
            var owner = me.IsAdmin() && input.OwnerId is not null ? input.OwnerId : me.RequireId();
            if (!await db.Users.AnyAsync(u => u.Id == owner)) throw ApiError.BadRequest("Cliente no encontrado.");
            var pet = Apply(new Pet { Id = Ids.New("pet"), OwnerId = owner }, input);
            db.Pets.Add(pet);
            await db.SaveChangesAsync();
            return pet;
        });

        g.MapPut("/{id}", async (string id, PetInput input, ClaimsPrincipal me, AppDb db) =>
        {
            var pet = Apply(await db.PetFor(me, id), input);
            await db.SaveChangesAsync();
            return pet;
        });

        g.MapDelete("/{id}", async (string id, ClaimsPrincipal me, AppDb db) =>
        {
            db.Pets.Remove(await db.PetFor(me, id));
            await db.Medical.Where(m => m.PetId == id).ExecuteDeleteAsync();
            await db.Vaccines.Where(v => v.PetId == id).ExecuteDeleteAsync();
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static Pet Apply(Pet p, PetInput i)
    {
        if (string.IsNullOrWhiteSpace(i.Name) || i.Species is not ("dog" or "cat" or "other")) throw ApiError.BadRequest("Datos de la mascota inválidos.");
        p.Name = i.Name.Trim(); p.Species = i.Species; p.Breed = i.Breed?.Trim() ?? ""; p.Sex = i.Sex is "male" or "female" ? i.Sex : null;
        p.BirthDate = Text.Clean(i.BirthDate); p.PhotoUrl = Text.Clean(i.PhotoUrl); p.Notes = Text.Clean(i.Notes);
        return p;
    }

    private static void MapServices(RouteGroupBuilder g)
    {
        g.MapGet("/", async (AppDb db) => await db.Services.ToListAsync());

        g.MapPut("/{id}", async (string id, Service input, AppDb db) =>
        {
            input.Id = id;
            var cur = await db.Services.FindAsync(id);
            if (cur is null) db.Services.Add(input); else db.Entry(cur).CurrentValues.SetValues(input);
            if (cur is not null) cur.Includes = input.Includes;
            await db.SaveChangesAsync();
            return cur ?? input;
        }).RequireAuthorization("admin");

        g.MapPost("/{id}/toggle-active", async (string id, AppDb db) =>
        {
            var s = await db.Services.FindAsync(id) ?? throw ApiError.NotFound("Servicio no encontrado.");
            s.Active = !s.Active;
            await db.SaveChangesAsync();
            return s;
        }).RequireAuthorization("admin");

        g.MapDelete("/{id}", async (string id, AppDb db) =>
        {
            await db.Services.Where(s => s.Id == id).ExecuteDeleteAsync();
            return Results.NoContent();
        }).RequireAuthorization("admin");
    }

    private static void MapSettings(RouteGroupBuilder g)
    {
        g.MapGet("/", (AppDb db) => SettingsRepo.GetAsync(db));
        g.MapPut("/", async (BusinessSettings input, AppDb db) =>
        {
            await SettingsRepo.SaveAsync(db, input);
            return input;
        }).RequireAuthorization("admin");
    }

    private static void MapReviews(RouteGroupBuilder g)
    {
        // Público: solo visibles. Admin: todas.
        g.MapGet("/", async (ClaimsPrincipal me, AppDb db) =>
        {
            var (admin, uid) = (me.IsAdmin(), me.Id());
            return await db.Reviews.Where(r => admin || !r.Hidden || r.UserId == uid).OrderByDescending(r => r.CreatedAt).ToListAsync();
        });

        g.MapPost("/", async (ReviewInput input, ClaimsPrincipal me, AppDb db) =>
        {
            var uid = me.RequireId();
            var appt = await db.Appointments.FindAsync(input.AppointmentId);
            if (appt is null || appt.UserId != uid) throw ApiError.Forbidden("No puedes calificar esta cita.");
            if (appt.Status != "attended") throw ApiError.BadRequest("Solo puedes calificar citas atendidas.");
            if (input.Rating is < 1 or > 5) throw ApiError.BadRequest("La calificación debe ser de 1 a 5.");
            if (await db.Reviews.AnyAsync(r => r.AppointmentId == appt.Id)) throw new ApiError(409, "Ya calificaste esta cita.");
            var user = await db.Users.FindAsync(uid);
            var review = new Review
            {
                Id = Ids.New("rev"), AppointmentId = appt.Id, UserId = uid, CustomerName = user!.FullName, ServiceId = appt.ServiceId,
                ServiceName = appt.ServiceName, PetName = appt.PetName, Rating = input.Rating, Comment = input.Comment?.Trim() ?? "", CreatedAt = DateTime.UtcNow,
            };
            db.Reviews.Add(review);
            await db.SaveChangesAsync();
            return review;
        }).RequireAuthorization();

        g.MapPost("/{id}/toggle-hidden", async (string id, AppDb db) =>
        {
            var r = await db.Reviews.FindAsync(id) ?? throw ApiError.NotFound("Reseña no encontrada.");
            r.Hidden = !r.Hidden;
            await db.SaveChangesAsync();
            return r;
        }).RequireAuthorization("admin");

        g.MapPut("/{id}/reply", async (string id, ReplyInput input, AppDb db) =>
        {
            var r = await db.Reviews.FindAsync(id) ?? throw ApiError.NotFound("Reseña no encontrada.");
            r.Reply = Text.Clean(input.Reply);
            await db.SaveChangesAsync();
            return r;
        }).RequireAuthorization("admin");

        g.MapDelete("/{id}", async (string id, AppDb db) =>
        {
            await db.Reviews.Where(r => r.Id == id).ExecuteDeleteAsync();
            return Results.NoContent();
        }).RequireAuthorization("admin");
    }

    private static void MapMedical(RouteGroupBuilder g)
    {
        // Lectura: dueño o admin. Escritura: admin.
        g.MapGet("/pets/{petId}/medical", async (string petId, ClaimsPrincipal me, AppDb db) =>
        {
            await db.PetFor(me, petId);
            return await db.Medical.Where(m => m.PetId == petId).OrderByDescending(m => m.Date).ToListAsync();
        });
        g.MapGet("/pets/{petId}/vaccines", async (string petId, ClaimsPrincipal me, AppDb db) =>
        {
            await db.PetFor(me, petId);
            return await db.Vaccines.Where(v => v.PetId == petId).OrderByDescending(v => v.Date).ToListAsync();
        });

        // Admin: todo (recordatorios, latestVaccines). Cliente: lo de sus mascotas.
        g.MapGet("/medical", async (ClaimsPrincipal me, AppDb db) =>
            me.IsAdmin() ? await db.Medical.ToListAsync() : await db.Medical.Where(m => OwnPetIds(db, me).Contains(m.PetId)).ToListAsync());
        g.MapGet("/vaccines", async (ClaimsPrincipal me, AppDb db) =>
            me.IsAdmin() ? await db.Vaccines.ToListAsync() : await db.Vaccines.Where(v => OwnPetIds(db, me).Contains(v.PetId)).ToListAsync());

        g.MapPost("/medical", async (MedicalEntry e, AppDb db) =>
        {
            if (!await db.Pets.AnyAsync(p => p.Id == e.PetId)) throw ApiError.BadRequest("Mascota no encontrada.");
            if (string.IsNullOrEmpty(e.Id)) e.Id = Ids.New("med");
            var cur = await db.Medical.FindAsync(e.Id);
            if (cur is null) db.Medical.Add(e); else db.Entry(cur).CurrentValues.SetValues(e);
            await db.SaveChangesAsync();
            return e;
        }).RequireAuthorization("admin");

        g.MapDelete("/medical/{id}", async (string id, AppDb db) =>
        {
            await db.Medical.Where(m => m.Id == id).ExecuteDeleteAsync();
            return Results.NoContent();
        }).RequireAuthorization("admin");

        g.MapPost("/vaccines", async (Vaccination v, AppDb db) =>
        {
            if (!await db.Pets.AnyAsync(p => p.Id == v.PetId)) throw ApiError.BadRequest("Mascota no encontrada.");
            if (string.IsNullOrEmpty(v.Id)) v.Id = Ids.New("vac");
            var cur = await db.Vaccines.FindAsync(v.Id);
            if (cur is null) db.Vaccines.Add(v); else db.Entry(cur).CurrentValues.SetValues(v);
            await db.SaveChangesAsync();
            return v;
        }).RequireAuthorization("admin");

        g.MapPost("/vaccines/{id}/reminder", async (string id, AppDb db) =>
        {
            var v = await db.Vaccines.FindAsync(id) ?? throw ApiError.NotFound("Vacuna no encontrada.");
            v.ReminderSentAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return v;
        }).RequireAuthorization("admin");

        g.MapDelete("/vaccines/{id}", async (string id, AppDb db) =>
        {
            await db.Vaccines.Where(v => v.Id == id).ExecuteDeleteAsync();
            return Results.NoContent();
        }).RequireAuthorization("admin");
    }

    private static IQueryable<string> OwnPetIds(AppDb db, ClaimsPrincipal me)
    {
        var uid = me.Id();
        return db.Pets.Where(p => p.OwnerId == uid).Select(p => p.Id);
    }

    private static void MapMessages(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/contact", async (ContactInput input, AppDb db) =>
        {
            if (string.IsNullOrWhiteSpace(input.FullName) || string.IsNullOrWhiteSpace(input.Phone) || string.IsNullOrWhiteSpace(input.Message))
                throw ApiError.BadRequest("Completa nombre, teléfono y mensaje.");
            var m = new ContactMessage
            {
                Id = Ids.New("msg"), FullName = input.FullName.Trim(), Phone = input.Phone.Trim(), ServiceId = input.ServiceId ?? "",
                Message = input.Message.Trim(), CreatedAt = DateTime.UtcNow,
            };
            db.Messages.Add(m);
            await db.SaveChangesAsync();
            return m;
        });

        var g = app.MapGroup("/api/messages").RequireAuthorization("admin");
        g.MapGet("/", async (AppDb db) => await db.Messages.OrderByDescending(m => m.CreatedAt).ToListAsync());
        g.MapPatch("/{id}/status", async (string id, MessageStatusInput input, AppDb db) =>
        {
            if (input.Status is not ("new" or "read" or "resolved")) throw ApiError.BadRequest("Estado inválido.");
            var m = await db.Messages.FindAsync(id) ?? throw ApiError.NotFound("Mensaje no encontrado.");
            m.Status = input.Status;
            await db.SaveChangesAsync();
            return m;
        });
        g.MapDelete("/{id}", async (string id, AppDb db) =>
        {
            await db.Messages.Where(m => m.Id == id).ExecuteDeleteAsync();
            return Results.NoContent();
        });
    }

    private static void MapCustomers(RouteGroupBuilder g)
    {
        g.MapGet("/", async (AppDb db) =>
            (await db.Users.Where(u => u.Role == "client").OrderBy(u => u.FullName).ToListAsync()).Select(u => u.Public()));

        // Alta presencial sin contraseña: el cliente la activa con "Recuperar contraseña".
        g.MapPost("/", async (CustomerInput input, AppDb db) =>
        {
            if (!input.Consent) throw ApiError.BadRequest("Debes confirmar que el cliente aceptó el tratamiento de sus datos.");
            if (string.IsNullOrWhiteSpace(input.FullName) || string.IsNullOrWhiteSpace(input.Phone)) throw ApiError.BadRequest("Nombre y teléfono son obligatorios.");
            var email = Text.Email(input.Email ?? "");
            if (email != "" && await db.Users.AnyAsync(u => u.Email == email)) throw new ApiError(409, "Ya existe un cliente con ese correo.");
            var now = DateTime.UtcNow;
            var u = new User
            {
                Id = Ids.New("u"), FullName = input.FullName.Trim(), Email = email, Phone = input.Phone.Trim(), Role = "client",
                CreatedAt = now, ConsentAt = now, ConsentSource = "admin",
            };
            db.Users.Add(u);
            await db.SaveChangesAsync();
            return u.Public();
        });

        g.MapPatch("/{id}/active", async (string id, ActiveInput input, AppDb db) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id && x.Role == "client") ?? throw ApiError.NotFound("Cliente no encontrado.");
            u.Active = input.Active;
            await db.SaveChangesAsync();
            return u.Public();
        });
    }

    // Solo un administrador crea otros administradores; el registro público siempre crea clientes.
    private static void MapAdmins(RouteGroupBuilder g)
    {
        g.MapGet("/", async (AppDb db) =>
            (await db.Users.Where(u => u.Role == "admin").OrderBy(u => u.FullName).ToListAsync()).Select(u => u.Public()));

        g.MapPost("/", async (AdminInput input, AppDb db) =>
        {
            var email = Text.Email(input.Email ?? "");
            if (string.IsNullOrWhiteSpace(input.FullName) || !email.Contains('@')) throw ApiError.BadRequest("Ingresa nombre y correo válidos.");
            if ((input.Password?.Length ?? 0) < 8 || !input.Password!.Any(char.IsLetter) || !input.Password.Any(char.IsDigit))
                throw ApiError.BadRequest("La contraseña debe tener al menos 8 caracteres, con letras y números.");
            if (await db.Users.AnyAsync(u => u.Email == email)) throw new ApiError(409, "Ya existe una cuenta con ese correo.");
            var u = new User
            {
                Id = Ids.New("u"), FullName = input.FullName.Trim(), Email = email, Role = "admin", CreatedAt = DateTime.UtcNow,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(input.Password),
            };
            db.Users.Add(u);
            await db.SaveChangesAsync();
            return u.Public();
        });

        g.MapPatch("/{id}/active", async (string id, ActiveInput input, ClaimsPrincipal me, AppDb db) =>
        {
            if (id == me.Id()) throw ApiError.BadRequest("No puedes bloquear tu propia cuenta.");
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id && x.Role == "admin") ?? throw ApiError.NotFound("Administrador no encontrado.");
            u.Active = input.Active;
            await db.SaveChangesAsync();
            return u.Public();
        });
    }
}
