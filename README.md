# MR. SABUESO — API

API REST en .NET 10 (minimal APIs) + EF Core + MySQL para el sitio y panel de MR. SABUESO.

## Configuración

Las credenciales **no** están en el repositorio. Crea `appsettings.Development.json` (ignorado por git):

```json
{
  "ConnectionStrings": { "Default": "Server=...;Port=3306;Database=...;User=...;Password=...;SslMode=Preferred;" },
  "Jwt": { "Key": "una-clave-aleatoria-de-al-menos-32-caracteres" },
  "Seed": { "DemoData": true }
}
```

En producción usa variables de entorno: `ConnectionStrings__Default`, `Jwt__Key`, `Seed__AdminPassword`, `Cors__Origins__0`.

## Ejecutar

```
dotnet run --launch-profile http   # http://localhost:5072/api
```

Al iniciar crea las tablas y carga servicios, configuración y el administrador inicial (`Seed:AdminEmail` / `Seed:AdminPassword`).
Con `Seed:DemoData = true` agrega clientes, mascotas, citas y reseñas de ejemplo.
