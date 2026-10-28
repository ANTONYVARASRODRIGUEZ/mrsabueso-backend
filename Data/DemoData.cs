using Microsoft.EntityFrameworkCore;
using MrSabueso.Api.Infrastructure;

namespace MrSabueso.Api.Data;

/// <summary>
/// Datos de ejemplo realistas para ver el sitio y el panel con contenido (Seed:DemoData = true).
/// Se cargan una sola vez: si ya hay más de 2 clientes no hace nada.
/// </summary>
public static class DemoData
{
    private static readonly (string Name, string Email, string Phone)[] Clients =
    [
        ("Valeria Torres", "valeria.torres@example.com", "+51 987 654 321"),
        ("Carlos Mendoza", "carlos.mendoza@example.com", "+51 912 345 678"),
        ("Lucía Paredes", "lucia.paredes@example.com", "+51 955 123 456"),
        ("Jorge Salazar", "jorge.salazar@example.com", "+51 944 888 222"),
        ("María Fernanda Ríos", "mafe.rios@example.com", "+51 933 111 000"),
        ("Diego Quispe", "diego.quispe@example.com", "+51 922 456 789"),
        ("Sofía Castañeda", "sofia.castaneda@example.com", "+51 966 777 444"),
    ];

    private static readonly (string Name, string Species, string Breed, string Sex)[] PetList =
    [
        ("Rocky", "dog", "Labrador", "male"), ("Luna", "cat", "Mestizo", "female"), ("Max", "dog", "Golden Retriever", "male"),
        ("Nala", "dog", "Schnauzer", "female"), ("Simba", "cat", "Persa", "male"), ("Toby", "dog", "Poodle", "male"),
        ("Kira", "dog", "Pastor Alemán", "female"), ("Misu", "cat", "Angora", "female"), ("Bruno", "dog", "Bulldog", "male"),
        ("Coco", "other", "Conejo enano", "female"),
    ];

    private static readonly string[] Comments =
    [
        "Excelente atención, mi perrito salió feliz y muy bien arreglado.",
        "Muy profesionales y cariñosos. Totalmente recomendados.",
        "Reservé en línea y todo fue muy rápido. Volveremos.",
        "La doctora fue muy paciente y me explicó todo con claridad.",
        "Buen servicio y puntualidad. Mi gata quedó tranquila.",
        "Me encantó el trato. Los recordatorios por WhatsApp ayudan mucho.",
    ];

    private static readonly string[] Vets = ["Dra. Camila Vargas", "Dr. Andrés Rojas"];

    public static async Task RunAsync(AppDb db)
    {
        if (await db.Users.CountAsync(u => u.Role == "client") > 2) return;

        var rand = new Random(20261016);
        T Pick<T>(IReadOnlyList<T> l) => l[rand.Next(l.Count)];
        var today = DateOnly.FromDateTime(Clock.Now);
        string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");
        DateTime Ago(int days) => DateTime.UtcNow.AddDays(-days);
        var settings = await SettingsRepo.GetAsync(db);
        var services = await db.Services.Where(s => s.Bookable && s.Active).ToListAsync();
        var hash = BCrypt.Net.BCrypt.HashPassword("Cliente1234");

        // Clientes
        var users = Clients.Select((c, i) => new User
        {
            Id = $"u-c{i + 1}", FullName = c.Name, Email = c.Email, Phone = c.Phone, Role = "client",
            CreatedAt = Ago(50 - i * 5), ConsentAt = Ago(50 - i * 5), ConsentSource = i % 3 == 2 ? "admin" : "web", PasswordHash = hash,
        }).ToList();
        db.Users.AddRange(users);
        var demo = await db.Users.FindAsync("u-demo");
        if (demo is not null) demo.Phone ??= "+51 923 456 781";

        // Mascotas
        var pets = PetList.Select((p, i) => new Pet
        {
            Id = $"pet-{i + 3}", OwnerId = users[i % users.Count].Id, Name = p.Name, Species = p.Species, Breed = p.Breed, Sex = p.Sex,
            BirthDate = Iso(today.AddDays(-(400 + i * 170))),
        }).ToList();
        db.Pets.AddRange(pets);
        var demoPets = await db.Pets.Where(p => p.OwnerId == "u-demo").ToListAsync();
        foreach (var (p, i) in demoPets.Select((p, i) => (p, i))) p.BirthDate ??= Iso(today.AddDays(-(1500 - i * 600)));
        var allPets = pets.Concat(demoPets).ToList();
        var owners = users.Concat(demo is null ? [] : [demo]).ToDictionary(u => u.Id);

        // Citas: 5 semanas atrás y 10 días adelante
        var appts = new List<Appointment>();
        var used = (await db.Appointments.Where(a => a.Status != "cancelled").Select(a => a.Date + "|" + a.Time).ToListAsync()).ToHashSet();
        var seq = 200;
        void Add(Pet pet, Service svc, string date, string time, string status, bool paid)
        {
            var u = owners[pet.OwnerId];
            used.Add($"{date}|{time}");
            appts.Add(new Appointment
            {
                Id = $"apt-d{++seq}", Code = $"#75-{seq}", UserId = u.Id, CustomerName = u.FullName, CustomerPhone = u.Phone, PetId = pet.Id,
                PetName = $"{pet.Name} ({pet.Breed})", ServiceId = svc.Id, ServiceName = svc.Name, Price = svc.PriceFrom, Date = date, Time = time, EndTime = Scheduling.EndOf(time, Scheduling.DurationOf(svc)),
                Status = status, Payment = paid ? "paid" : "pending",
                CreatedAt = DateTime.SpecifyKind(DateTime.Parse(date).AddDays(-3).AddHours(17), DateTimeKind.Utc),
            });
        }

        // Historial de la cuenta demo
        if (demoPets.Count >= 2)
        {
            var svc = services.ToDictionary(s => s.Id);
            Add(demoPets[1], svc["vacunacion"], Iso(today.AddDays(-70)), "11:00", "attended", true);
            Add(demoPets[0], svc["grooming"], Iso(today.AddDays(-38)), "14:30", "attended", true);
            Add(demoPets[0], svc["grooming"], Iso(today.AddDays(-30)), "16:00", "no_show", false);
        }

        for (var off = -35; off <= 10; off++)
        {
            var date = Iso(today.AddDays(off));
            var slots = Scheduling.SlotsForDate(settings, date);
            if (slots.Count == 0) continue;
            var count = 1 + rand.Next(3);
            for (var k = 0; k < count; k++)
            {
                var free = slots.Where(s => !used.Contains($"{date}|{s}")).ToList();
                if (free.Count == 0 || (off == 0 && k > 0)) break;
                string status; var paid = false;
                if (off < 0)
                {
                    var r = rand.NextDouble();
                    status = r < 0.78 ? "attended" : r < 0.88 ? "no_show" : "cancelled";
                    paid = status == "attended";
                }
                else status = rand.NextDouble() < 0.55 ? "confirmed" : "pending";
                Add(Pick(pets), Pick(services), date, Pick(free), status, paid);
            }
        }
        db.Appointments.AddRange(appts);

        // Reseñas de citas atendidas
        var attended = appts.Where(a => a.Status == "attended").ToList();
        var reviews = attended.Where(a => a.UserId != "u-demo").Take(9).Select((a, i) => new Review
        {
            Id = $"rev-d{i + 1}", AppointmentId = a.Id, UserId = a.UserId, CustomerName = a.CustomerName, ServiceId = a.ServiceId,
            ServiceName = a.ServiceName, PetName = a.PetName, Rating = i % 4 == 3 ? 4 : 5,
            Comment = i == 0
                ? "Estamos muy agradecidos con todo el equipo de MR. SABUESO. Siempre tratan a mi perrito con mucho cariño y profesionalismo. ¡Totalmente recomendados!"
                : Comments[i % Comments.Length],
            CreatedAt = DateTime.SpecifyKind(DateTime.Parse(a.Date).AddDays(1).AddHours(23), DateTimeKind.Utc),
            Reply = i == 1 ? "¡Gracias por confiar en nosotros! Los esperamos pronto." : null,
        }).ToList();
        var demoRated = attended.FirstOrDefault(a => a.UserId == "u-demo" && a.ServiceId == "grooming");
        if (demoRated is not null)
            reviews.Add(new Review
            {
                Id = "rev-demo", AppointmentId = demoRated.Id, UserId = "u-demo", CustomerName = demoRated.CustomerName, ServiceId = demoRated.ServiceId,
                ServiceName = demoRated.ServiceName, PetName = demoRated.PetName, Rating = 5, Comment = "Boby quedó hermoso y súper tranquilo. Gracias.",
                CreatedAt = DateTime.SpecifyKind(DateTime.Parse(demoRated.Date).AddDays(1).AddHours(23), DateTimeKind.Utc),
                Reply = "¡Gracias, Anali! Nos encantó atender a Boby.",
            });
        db.Reviews.AddRange(reviews);

        // Historial clínico y vacunas
        foreach (var (p, i) in allPets.Select((p, i) => (p, i)))
        {
            var w = p.Species == "cat" ? 4m : p.Species == "other" ? 1.5m : 12 + i % 5 * 4;
            db.Medical.Add(new MedicalEntry
            {
                Id = $"med-d{i}a", PetId = p.Id, Date = Iso(today.AddDays(-(120 + i * 9))), Kind = "consulta", Title = "Control general",
                Description = "Examen físico completo. Buen estado de salud, peso adecuado.", Vet = Vets[i % 2], WeightKg = w,
            });
            db.Medical.Add(new MedicalEntry
            {
                Id = $"med-d{i}b", PetId = p.Id, Date = Iso(today.AddDays(-(50 + i * 4))), Kind = "tratamiento", Title = "Desparasitación",
                Description = "Desparasitación interna y externa. Repetir en 3 meses.", Vet = Vets[(i + 1) % 2], WeightKg = w + 0.3m,
            });
            string[] core = p.Species switch { "cat" => ["Triple felina", "Antirrábica"], "dog" => ["Quíntuple (DHPP)", "Antirrábica"], _ => ["Mixomatosis"] };
            foreach (var (vaccine, j) in core.Select((v, j) => (v, j)))
            {
                var givenAgo = 200 + (i * 37 + j * 53) % 190;
                db.Vaccines.Add(new Vaccination
                {
                    Id = $"vac-d{i}-{j}", PetId = p.Id, Vaccine = vaccine, Date = Iso(today.AddDays(-givenAgo)), NextDue = Iso(today.AddDays(365 - givenAgo)),
                    Lot = $"L{2400 + i * 7 + j}", Vet = Vets[(i + j) % 2],
                });
            }
        }

        // Mensajes de contacto
        db.Messages.AddRange(
            new ContactMessage { Id = "msg-d1", FullName = "Rosa Medina", Phone = "+51 955 010 203", ServiceId = "grooming", Message = "Hola, ¿atienden razas grandes para baño y corte? Tengo un Husky.", CreatedAt = Ago(1) },
            new ContactMessage { Id = "msg-d2", FullName = "Pedro Lozano", Phone = "+51 944 202 101", ServiceId = "vacunacion", Message = "Quisiera saber el esquema de vacunas para un cachorro de 2 meses.", CreatedAt = Ago(2) },
            new ContactMessage { Id = "msg-d3", FullName = "Andrea Gil", Phone = "+51 933 909 808", ServiceId = "petshop", Message = "¿Tienen alimento para gatos esterilizados? ¿Hacen delivery?", CreatedAt = Ago(4), Status = "read" },
            new ContactMessage { Id = "msg-d4", FullName = "Hugo Palacios", Phone = "+51 922 303 404", ServiceId = "dental", Message = "¿Cuánto cuesta la limpieza dental para un perro mediano?", CreatedAt = Ago(7), Status = "resolved" });

        await db.SaveChangesAsync();
    }
}
