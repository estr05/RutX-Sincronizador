# Controllers — Organizacion por areas (Opcion A)

El API se segmenta por cliente para que movil y web evolucionen sin mezclarse.
**Las rutas HTTP NO dependen de la carpeta ni del namespace** (son explicitas con
`[Route(...)]`), asi que mover archivos aqui nunca rompe endpoints existentes.

## Areas

| Carpeta            | Namespace                            | Contenido |
|--------------------|--------------------------------------|-----------|
| `Movil/`           | `Rutx.Sincronizador.Controllers.Movil` | Contrato movil CONGELADO: endpoints que consume la app RUTX. NO modificar rutas ni DTOs de respuesta. |
| `Compartidos/`     | `Rutx.Sincronizador.Controllers.Compartidos` | Logica util para movil y web (ej. alta de clientes). |
| `Admin/`           | `Rutx.Sincronizador.Controllers.Admin` | Mantenimiento y diagnostico (folios, cola, dbcompare). No forma parte del flujo de venta. |
| `Web/`             | `Rutx.Sincronizador.Controllers.Web` | (Vacia) Endpoints futuros de la pagina web. Usar rutas `/api/v2/*` o `/api/web/*` con DTOs propios en `Models/Web/`. |

## Reglas

1. **Contrato movil congelado**: los endpoints que la app llama (ver README raiz) no se modifican.
   Los nuevos endpoints de web NO tocan `Movil/`.
2. **Versionado**: movil = `/api/v1/*` (congelado). Web = `/api/v2/*` o `/api/web/*`.
3. **DTOs**: los de movil viven en `Models/` (no tocar). Los de web iran en `Models/Web/`
   con namespace `Rutx.Sincronizador.Models.Web`.
4. **Servicios compartidos**: la logica de negocio vive en `Services/` (interfaces);
   los controllers de cualquier area los reutilizan sin duplicar.

## Notas (limpieza futura)

- `AuthController` y `DiagnosticController` usan `[Route("api/[controller]")]` (sin
  versionar): hoy resuelven a `/api/auth` y `/api/diagnostic`. Si se quiere uniformar
  con `/api/v1/*`, es un cambio futuro de ruta (requiere actualizar la app si afecta
  el login) — NO hacerlo sin plan.
- Los DTOs embebidos (`LoginRequest` en Auth, `ColaRequestDto` en Cola) viven en los
  namespaces de sus controllers. Cuando la web necesite uno propio, crear el suyo en
  `Models/Web/` en lugar de reutilizar el de móvil.
