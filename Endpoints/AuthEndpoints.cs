using Google.Apis.Auth;
using Microsoft.EntityFrameworkCore;
using MrSabueso.Api.Data;
using MrSabueso.Api.Infrastructure;

namespace MrSabueso.Api.Endpoints;

public record LoginInput(string Email, string Password);
public record RegisterInput(string FullName, string Email, string? Phone, string Password);
public record ForgotInput(string Email);
public record ResetInput(string Token, string NewPassword);
public record GoogleInput(string Credential);

/// <summary>Reemplaza a core/services/auth.service.ts. Errores con los mismos códigos que AuthErrorCode.</summary>
public static class AuthEndpoints
{
    private static readonly TimeSpan ResetTtl = TimeSpan.FromMinutes(30);

    /// <summary>Dirección del frontend para los enlaces de los correos: el Origin de la petición si es uno permitido.</summary>
    private static string FrontendUrl(HttpContext ctx, IConfiguration cfg)
    {
        var allowed = cfg.GetSection("Cors:Origins").Get<string[]>() ?? [];
        var origin = ctx.Request.Headers.Origin.ToString().TrimEnd('/');
        if (allowed.Contains(origin, StringComparer.OrdinalIgnoreCase)) return origin;
        return (cfg["App:FrontendUrl"] ?? allowed.FirstOrDefault(o => !o.Contains("localhost")) ?? "http://localhost:4200").TrimEnd('/');
    }

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/login", async (LoginInput input, AppDb db, TokenService tokens) =>
        {
            var email = Text.Email(input.Email);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Email == email);
            if (u?.PasswordHash is null || !BCrypt.Net.BCrypt.Verify(input.Password, u.PasswordHash))
                throw new ApiError(401, "Correo o contraseña incorrectos.", "invalid-credentials");
            if (!u.Active) throw new ApiError(403, "Tu cuenta está deshabilitada.", "disabled");
            return Results.Ok(new { token = tokens.Create(u), user = u.Public() });
        });

        g.MapPost("/register", async (RegisterInput input, AppDb db, TokenService tokens) =>
        {
            var email = Text.Email(input.Email);
            if (string.IsNullOrWhiteSpace(input.FullName) || !email.Contains('@') || (input.Password?.Length ?? 0) < 8)
                throw ApiError.BadRequest("Datos de registro inválidos.");
            if (await db.Users.AnyAsync(x => x.Email == email)) throw new ApiError(409, "Ese correo ya está registrado.", "email-taken");
            var now = DateTime.UtcNow;
            var u = new User
            {
                Id = Ids.New("u"), FullName = input.FullName.Trim(), Email = email, Phone = Text.Clean(input.Phone), Role = "client",
                CreatedAt = now, ConsentAt = now, ConsentSource = "web", PasswordHash = BCrypt.Net.BCrypt.HashPassword(input.Password),
            };
            db.Users.Add(u);
            await db.SaveChangesAsync();
            return Results.Ok(new { token = tokens.Create(u), user = u.Public() });
        });

        // Inicio de sesión con Google: el botón de Google entrega un ID token firmado; aquí se valida
        // contra nuestro ID de cliente. Si el correo ya existe se entra a esa cuenta; si no, se crea un cliente.
        g.MapPost("/google", async (GoogleInput input, AppDb db, TokenService tokens, IConfiguration cfg) =>
        {
            var clientId = cfg["Google:ClientId"];
            if (string.IsNullOrEmpty(clientId)) throw new ApiError(501, "El ingreso con Google no está configurado.");
            GoogleJsonWebSignature.Payload payload;
            try { payload = await GoogleJsonWebSignature.ValidateAsync(input.Credential, new() { Audience = [clientId] }); }
            catch (InvalidJwtException) { throw new ApiError(401, "No pudimos verificar tu cuenta de Google.", "invalid-credentials"); }
            if (!payload.EmailVerified) throw new ApiError(401, "Tu correo de Google no está verificado.", "invalid-credentials");

            var email = Text.Email(payload.Email);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Email == email);
            if (u is null)
            {
                var now = DateTime.UtcNow;
                u = new User
                {
                    Id = Ids.New("u"), FullName = string.IsNullOrWhiteSpace(payload.Name) ? email.Split('@')[0] : payload.Name.Trim(), Email = email,
                    Role = "client", CreatedAt = now, ConsentAt = now, ConsentSource = "web",
                };
                db.Users.Add(u);
                await db.SaveChangesAsync();
            }
            if (!u.Active) throw new ApiError(403, "Tu cuenta está deshabilitada.", "disabled");
            return Results.Ok(new { token = tokens.Create(u), user = u.Public() });
        });

        g.MapGet("/me", async (HttpContext ctx, AppDb db) =>
        {
            var u = await db.Users.FindAsync(ctx.User.RequireId());
            return u is { Active: true } ? Results.Ok(u.Public()) : Results.Unauthorized();
        }).RequireAuthorization();

        // Envía por correo (Brevo) el enlace para crear una nueva contraseña. La respuesta es igual exista o no
        // la cuenta, para no revelar qué correos están registrados. Sin Brevo configurado, en Development se
        // devuelve el token para poder probar el flujo.
        g.MapPost("/forgot-password", async (ForgotInput input, HttpContext ctx, AppDb db, EmailService mail, IHostEnvironment env, IConfiguration cfg) =>
        {
            var expose = (env.IsDevelopment() && !mail.Configured) || cfg.GetValue<bool>("Auth:ExposeResetToken");
            var email = Text.Email(input.Email ?? "");
            var u = await db.Users.FirstOrDefaultAsync(x => x.Email == email && x.Active);
            if (u is null) return Results.Ok(new { token = (string?)null });

            // Máximo un correo por minuto por cuenta.
            var recent = DateTime.UtcNow + ResetTtl - TimeSpan.FromMinutes(1);
            if (await db.ResetTokens.AnyAsync(t => t.Email == email && t.ExpiresAt > recent)) return Results.Ok(new { token = (string?)null });

            await db.ResetTokens.Where(t => t.Email == email || t.ExpiresAt < DateTime.UtcNow).ExecuteDeleteAsync();
            var token = Ids.Token();
            db.ResetTokens.Add(new ResetToken { Token = token, Email = email, ExpiresAt = DateTime.UtcNow + ResetTtl });
            await db.SaveChangesAsync();

            if (mail.Configured)
            {
                var link = $"{FrontendUrl(ctx, cfg)}/restablecer?token={token}";
                try { await mail.SendPasswordResetAsync(u.Email, u.FullName, link, (int)ResetTtl.TotalMinutes); }
                catch
                {
                    await db.ResetTokens.Where(t => t.Token == token).ExecuteDeleteAsync(); // permite reintentar de inmediato
                    throw;
                }
            }
            return Results.Ok(new { token = expose ? token : null });
        });

        g.MapGet("/reset-token/{token}", async (string token, AppDb db) =>
            Results.Ok(new { valid = await db.ResetTokens.AnyAsync(t => t.Token == token && t.ExpiresAt > DateTime.UtcNow) }));

        g.MapPost("/reset-password", async (ResetInput input, AppDb db) =>
        {
            if ((input.NewPassword?.Length ?? 0) < 8) throw ApiError.BadRequest("La contraseña debe tener al menos 8 caracteres.");
            var entry = await db.ResetTokens.FirstOrDefaultAsync(t => t.Token == input.Token && t.ExpiresAt > DateTime.UtcNow)
                ?? throw new ApiError(400, "El enlace no es válido o expiró.", "invalid-token");
            var u = await db.Users.FirstAsync(x => x.Email == entry.Email);
            u.PasswordHash = BCrypt.Net.BCrypt.HashPassword(input.NewPassword);
            db.ResetTokens.Remove(entry);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
