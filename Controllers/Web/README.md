# Area Web (pendiente)

Carpeta reservada para los controllers de la pagina web futura.

- Usar namespace `Rutx.Sincronizador.Controllers.Web`.
- Rutas sugeridas: `/api/v2/*` o `/api/web/*` (NO reutilizar `/api/v1/*` que esta congelado para movil).
- DTOs propios en `Models/Web/` con namespace `Rutx.Sincronizador.Models.Web`.
- Reutilizar los `Services/` existentes (estan desacoplados por interfaces).
- Agregar CORS en `Program.cs` antes de exponer la web.
