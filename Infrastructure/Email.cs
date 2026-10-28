using System.Net;
using System.Net.Http.Json;

namespace MrSabueso.Api.Infrastructure;

public class BrevoOptions
{
    public string ApiKey { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string SenderName { get; set; } = "MR. SABUESO";
}

/// <summary>Envío de correos transaccionales con la API HTTP de Brevo (no SMTP: Render gratis bloquea esos puertos).</summary>
public class EmailService(IHttpClientFactory http, BrevoOptions opt, ILogger<EmailService> log)
{
    public bool Configured => opt.ApiKey.Length > 0 && opt.SenderEmail.Length > 0;

    public async Task SendAsync(string toEmail, string toName, string subject, string html)
    {
        if (!Configured) throw new ApiError(503, "El envío de correos no está configurado.");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email")
        {
            Content = JsonContent.Create(new
            {
                sender = new { email = opt.SenderEmail, name = opt.SenderName },
                to = new[] { new { email = toEmail, name = toName } },
                subject,
                htmlContent = html,
            }),
        };
        req.Headers.Add("api-key", opt.ApiKey);
        using var res = await http.CreateClient().SendAsync(req);
        if (res.IsSuccessStatusCode) return;
        log.LogError("Brevo respondió {Status}: {Body}", (int)res.StatusCode, await res.Content.ReadAsStringAsync());
        throw new ApiError(502, "No pudimos enviar el correo. Inténtalo de nuevo en unos minutos.");
    }

    /// <summary>Correo con el enlace para crear una nueva contraseña.</summary>
    public Task SendPasswordResetAsync(string toEmail, string toName, string link, int minutes)
    {
        var name = WebUtility.HtmlEncode(toName.Split(' ')[0]);
        var url = WebUtility.HtmlEncode(link);
        var html = $"""
            <div style="font-family:Arial,Helvetica,sans-serif;max-width:520px;margin:0 auto;padding:24px;color:#1f2937">
              <div style="font-size:20px;font-weight:700;color:#f26a21;margin-bottom:16px">MR. SABUESO</div>
              <p style="font-size:16px">Hola {name},</p>
              <p style="font-size:15px;line-height:1.5">Recibimos una solicitud para restablecer la contraseña de tu cuenta. Haz clic en el botón para crear una nueva:</p>
              <p style="text-align:center;margin:28px 0">
                <a href="{url}" style="background:#f26a21;color:#fff;text-decoration:none;padding:14px 28px;border-radius:999px;font-weight:700;display:inline-block">Crear nueva contraseña</a>
              </p>
              <p style="font-size:13px;color:#6b7280;line-height:1.5">El enlace vence en {minutes} minutos y solo se puede usar una vez. Si no pediste este cambio, ignora este correo: tu contraseña seguirá igual.</p>
              <p style="font-size:12px;color:#9ca3af;word-break:break-all">Si el botón no funciona, copia este enlace en tu navegador:<br>{url}</p>
            </div>
            """;
        return SendAsync(toEmail, toName, "Restablece tu contraseña · MR. SABUESO", html);
    }
}
