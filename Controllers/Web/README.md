# Area Web (frontera v2)

Controllers de la pagina web del portal (oficina/administracion).

- Namespace: `Rutx.Sincronizador.Controllers.Web`.
- Rutas: `/api/v2/web/*` — NO reutilizar `/api/v1/*` (congelado para movil).
- DTOs propios en `Models/Web/` (namespace `Rutx.Sincronizador.Models.Web`).
- Servicios en `Services/Web/` (namespace `Rutx.Sincronizador.Services.Web`),
  desacoplados por interfaces. Flujo obligatorio:
  `Controller Web → Interface Web → Service Web → consulta parametrizada`.
- Envelope v2 (contrato §9.1): exito `data, [meta], [filters], trace_id`;
  error `code, message, [errors], trace_id`. Nunca stack traces ni SQL.
- Contrato canónico: `Docs/CONTRATOS_WEB_V2.md` (copia del contrato v2).

## Estado del esqueleto (Sprint 3 · Etapa paralela)

| Endpoint | Controller | Interface | Estado |
|---|---|---|---|
| `GET /dashboard` | `DashboardController` | `IDashboardWebService` | Esqueleto (TODO consulta) |
| `GET /dashboard/sales-series` | `DashboardController` | `IDashboardWebService` | Esqueleto (TODO consulta) |
| `GET /reports/sales` | `ReportsController` | `IReportsWebService` | Esqueleto (TODO consulta) |
| `GET /reports/sales-comparison` | `ReportsController` | `IReportsWebService` | Esqueleto (TODO consulta) |
| `GET /reports/route-profitability` | `ReportsController` | `IReportsWebService` | Posterior (§6.3); no publicar hasta activacion |
| `GET /route-monitor` | `RouteMonitorController` | `IRouteMonitoringWebService` | Esqueleto (TODO consulta) |
| `GET /route-monitor/{route_id}` | `RouteMonitorController` | `IRouteMonitoringWebService` | Esqueleto (TODO consulta) |
| `GET /routes` | `RouteMonitorController` | `IRouteMonitoringWebService` | Esqueleto (TODO consulta) |

Pendientes para publicar la frontera real:
- `WebAuthController` + `IWebAuthService` con `scope=web`, rol y zonas (contrato §10, paso 2).
- Middleware `RequireWebScope` y política CORS `RutxWeb` (contrato §9.1).
- Consultas parametrizadas Firebird en cada servicio (reemplazan los TODO).
