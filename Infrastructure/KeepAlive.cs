namespace MrSabueso.Api.Infrastructure;

/// <summary>
/// Evita que Render (plan gratis) duerma el servicio: lo duerme tras 15 min sin peticiones entrantes,
/// así que cada 10 min se llama a sí mismo por su URL pública (cuenta como tráfico entrante).
/// Solo se activa si existe RENDER_EXTERNAL_URL (Render la define) o KeepAlive:Url.
/// </summary>
public class KeepAlive(IHttpClientFactory http, IConfiguration cfg, ILogger<KeepAlive> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var baseUrl = cfg["KeepAlive:Url"] ?? Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");
        if (string.IsNullOrWhiteSpace(baseUrl)) return;
        var url = $"{baseUrl.TrimEnd('/')}/api/health";
        log.LogInformation("KeepAlive activo: {Url} cada {Min} min", url, Interval.TotalMinutes);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stop))
        {
            try
            {
                using var res = await http.CreateClient().GetAsync(url, stop);
                if (!res.IsSuccessStatusCode) log.LogWarning("KeepAlive: {Url} respondió {Status}", url, (int)res.StatusCode);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("KeepAlive falló: {Error}", e.Message);
            }
        }
    }
}
