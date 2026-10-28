using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MrSabueso.Api.Data;

namespace MrSabueso.Api.Infrastructure;

/// <summary>Error de negocio: se devuelve como { code, message } con el estado indicado.</summary>
public class ApiError(int status, string message, string? code = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
    public static ApiError BadRequest(string m) => new(400, m);
    public static ApiError NotFound(string m) => new(404, m);
    public static ApiError Forbidden(string m = "No tienes permiso para esta acción.") => new(403, m);
}

public static class Ids
{
    public static string New(string prefix) => $"{prefix}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";
    public static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

/// <summary>Hora del negocio (Lima, UTC-5) para validar horarios de cita.</summary>
public static class Clock
{
    private static readonly TimeZoneInfo Tz = FindTz();
    private static TimeZoneInfo FindTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Lima"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("Lima", TimeSpan.FromHours(-5), "Lima", "Lima"); }
    }
    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz);
    public static string Today => Now.ToString("yyyy-MM-dd");
    public static string Hhmm => Now.ToString("HH:mm");
}

public static class Scheduling
{
    public static readonly List<string> Inactive = ["no_show", "cancelled"]; // List: EF 9 no traduce Contains sobre arrays con C# 14

    public static bool IsOpenDay(BusinessSettings s, string iso)
    {
        if (!DateOnly.TryParseExact(iso, "yyyy-MM-dd", out var d) || s.ClosedDates.Contains(iso)) return false;
        return s.Hours.FirstOrDefault(h => h.Day == (int)d.DayOfWeek)?.Open ?? false;
    }

    public static int Minutes(string hhmm) => int.Parse(hhmm[..2]) * 60 + int.Parse(hhmm[3..5]);
    public static string FromMinutes(int m) { m = Math.Clamp(m, 0, 23 * 60 + 59); return $"{m / 60:00}:{m % 60:00}"; }
    public static bool IsHhmm(string? s) => s is { Length: 5 } && TimeOnly.TryParseExact(s, "HH:mm", out _);

    /// <summary>Duración por defecto de una cita: la del servicio, o 30 min si no la tiene.</summary>
    public static int DurationOf(Service? s) => s is { DurationMin: > 0 } ? s.DurationMin : 30;
    public static string EndOf(string time, int minutes) => FromMinutes(Minutes(time) + minutes);

    public static List<string> SlotsForDate(BusinessSettings s, string iso)
    {
        if (!IsOpenDay(s, iso)) return [];
        var h = s.Hours.First(x => x.Day == (int)DateOnly.ParseExact(iso, "yyyy-MM-dd").DayOfWeek);
        return s.Slots.Where(x => string.CompareOrdinal(x, h.From) >= 0 && string.CompareOrdinal(x, h.To) < 0).Order(StringComparer.Ordinal).ToList();
    }
}

public static class SettingsRepo
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<BusinessSettings> GetAsync(AppDb db)
    {
        var row = await db.Settings.FindAsync(1);
        return row is null ? Seeder.DefaultSettings : JsonSerializer.Deserialize<BusinessSettings>(row.Json, Json) ?? Seeder.DefaultSettings;
    }

    public static async Task SaveAsync(AppDb db, BusinessSettings s)
    {
        var row = await db.Settings.FindAsync(1);
        if (row is null) db.Settings.Add(row = new SettingsRow());
        row.Json = JsonSerializer.Serialize(s, Json);
        await db.SaveChangesAsync();
    }
}

public class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "mrsabueso";
    public int ExpiresDays { get; set; } = 7;
}

public class TokenService(JwtOptions opt)
{
    public string Create(User u)
    {
        var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, u.Id), new Claim(ClaimTypes.Role, u.Role), new Claim(JwtRegisteredClaimNames.Email, u.Email) };
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opt.Key)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(opt.Issuer, opt.Issuer, claims, expires: DateTime.UtcNow.AddDays(opt.ExpiresDays), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}

public static class UserExt
{
    public static string? Id(this ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue(JwtRegisteredClaimNames.Sub);
    public static bool IsAdmin(this ClaimsPrincipal p) => p.IsInRole("admin");
    public static string RequireId(this ClaimsPrincipal p) => p.Id() ?? throw new ApiError(401, "Sesión requerida");

    /// <summary>Usuario sin datos sensibles (como `User` del frontend).</summary>
    public static object Public(this User u) => new { u.Id, u.FullName, u.Email, u.Phone, u.Role, u.CreatedAt, u.Active, u.ConsentAt, u.ConsentSource };

    public static async Task<Pet> PetFor(this AppDb db, ClaimsPrincipal p, string petId)
    {
        var pet = await db.Pets.FindAsync(petId) ?? throw ApiError.NotFound("Mascota no encontrada.");
        if (!p.IsAdmin() && pet.OwnerId != p.Id()) throw ApiError.Forbidden();
        return pet;
    }
}

public static class Text
{
    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    public static string Email(string e) => e.Trim().ToLowerInvariant();
}
