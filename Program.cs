using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MrSabueso.Api.Data;
using MrSabueso.Api.Endpoints;
using MrSabueso.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

var conn = cfg.GetConnectionString("Default") ?? throw new InvalidOperationException("Falta ConnectionStrings:Default.");
builder.Services.AddDbContext<AppDb>(o => o.UseMySql(conn, new MySqlServerVersion(new Version(8, 0, 36)),
    my => my.EnableRetryOnFailure(3)));

var jwt = cfg.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (jwt.Key.Length < 32) throw new InvalidOperationException("Jwt:Key debe tener al menos 32 caracteres.");
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<TokenService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer, ValidAudience = jwt.Issuer, RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        NameClaimType = "sub", IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
    };
    // Una cuenta bloqueada por el administrador deja de funcionar aunque su token siga vigente.
    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = async ctx =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
            var id = ctx.Principal?.FindFirst("sub")?.Value;
            if (id is null || !await db.Users.AnyAsync(u => u.Id == id && u.Active)) ctx.Fail("Cuenta deshabilitada.");
        },
    };
});
builder.Services.AddAuthorizationBuilder().AddPolicy("admin", p => p.RequireRole("admin"));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var origins = cfg.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:4200"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Errores de negocio → { code, message } con su estado HTTP.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (ApiError e)
    {
        ctx.Response.StatusCode = e.Status;
        await ctx.Response.WriteAsJsonAsync(new { code = e.Code, message = e.Message });
    }
    catch (BadHttpRequestException)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { message = "Solicitud inválida." });
    }
});

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => new { ok = true, time = DateTime.UtcNow });
app.MapAuth();
app.MapAppointments();
app.MapResources();

using (var scope = app.Services.CreateScope())
    await Seeder.RunAsync(scope.ServiceProvider.GetRequiredService<AppDb>(), cfg);

app.Run();
