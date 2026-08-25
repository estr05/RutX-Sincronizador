# Grants propuestos para el usuario dedicado `RUTX_SYNC` (Firebird/Microsip)

> **ESTADO: PROPUESTA PARA REVISIÓN — NO EJECUTADA.**
> Este script NO ha corrido contra la base de producción. Secuencia obligatoria:
> 1) copia CONSISTENTE con `gbak -b` + restore a un **alias aislado** de
>    `databases.conf` (nunca file-copy del .FDB; nunca sobre la BD de Microsip);
> 2) ejecutar el script SOLO contra ese alias, tras confirmación explícita;
> 3) correr la matriz de permisos derivada del código real
>    (`Deploy/Firebird/FaseB_03_matriz_permisos.ps1`);
> 4) probar login + sync LAN desde la app;
> 5) documentar aquí los permisos efectivos resultantes;
> 6) solo entonces aplicar en el equipo demo.
>
> Derivado del análisis estático del SQL real en `Services/` y `Data/`
> (grep de FROM/JOIN/INSERT INTO/UPDATE sobre el código versionado).

## Contexto

- Se crea un usuario dedicado en lugar de usar las credenciales por defecto de SYSDBA (que NO se rotan: Microsip depende de ellas).
  NO se rota SYSDBA: Microsip puede depender de esa credencial.
- Las tablas `RUTX_*`, `WEB_*`, `SCHEMA_VERSION` son **SQLite** (BD
  complementaria); NO requieren grants Firebird.
- Los INSERT disparan triggers/generadores del esquema PV de Microsip:
  en Firebird 3+ puede requerirse `GRANT USAGE ON GENERATOR`. Verificar
  contra la copia; si algún generador falla, agregar su grant explícito.

## Script (para revisión)

```sql
-- Ejecutar conectado como SYSDBA SOBRE LA COPIA AISLADA (alias rutx_e2e_copia)
CREATE USER RUTX_SYNC PASSWORD '<aleatoria>=20+'; -- SIN admin role: no lo requiere
-- (Firebird 4+: CREATE USER RUTX_SYNC PASSWORD '...' USING PLUGIN Srp;)

-- Lectura de catálogos y consultas
GRANT SELECT ON CLIENTES            TO RUTX_SYNC;
GRANT SELECT ON DIRS_CLIENTES       TO RUTX_SYNC;
GRANT SELECT ON FORMAS_COBRO        TO RUTX_SYNC;
GRANT SELECT ON ARTICULOS           TO RUTX_SYNC;
GRANT SELECT ON PRECIOS_ARTICULOS   TO RUTX_SYNC;
GRANT SELECT ON IMPUESTOS           TO RUTX_SYNC;
GRANT SELECT ON IMPUESTOS_ARTICULOS TO RUTX_SYNC;
GRANT SELECT ON IMPUESTOS_DOCTOS_PV      TO RUTX_SYNC;
GRANT SELECT ON IMPUESTOS_DOCTOS_PV_DET  TO RUTX_SYNC;
GRANT SELECT ON VENDEDORES          TO RUTX_SYNC;
GRANT SELECT ON CAJEROS             TO RUTX_SYNC;
GRANT SELECT ON CAJAS               TO RUTX_SYNC;
GRANT SELECT ON CAJAS_CAJEROS       TO RUTX_SYNC;
GRANT SELECT ON SUCURSALES          TO RUTX_SYNC;
GRANT SELECT ON CONCEPTOS_IN        TO RUTX_SYNC;
GRANT SELECT ON SALDOS_IN           TO RUTX_SYNC;
GRANT SELECT ON SALDOS_CC           TO RUTX_SYNC;
GRANT SELECT ON VENCIMIENTOS_CARGOS_CC TO RUTX_SYNC;

-- Escritura Punto de Venta (ventas / no-ventas)
GRANT SELECT, INSERT, UPDATE ON DOCTOS_PV        TO RUTX_SYNC;
GRANT SELECT, INSERT         ON DOCTOS_PV_DET    TO RUTX_SYNC;
GRANT SELECT, INSERT         ON DOCTOS_PV_LIGAS  TO RUTX_SYNC;
GRANT SELECT, INSERT, UPDATE ON DOCTOS_PV_COBROS TO RUTX_SYNC;

-- Cobranza / CxC
GRANT SELECT, INSERT, UPDATE ON DOCTOS_CC TO RUTX_SYNC;

-- Folios y caja
GRANT SELECT, UPDATE ON FOLIOS_CAJAS TO RUTX_SYNC;
GRANT SELECT, INSERT ON MOVTOS_CAJAS TO RUTX_SYNC;

-- Integración / auditoría cruzada
GRANT SELECT, INSERT ON DOCTOS_ENTRE_SIS TO RUTX_SYNC;

-- Clientes (alta desde la app, si está habilitada)
GRANT SELECT, INSERT, UPDATE ON CLIENTES     TO RUTX_SYNC;
GRANT SELECT, INSERT         ON DIRS_CLIENTES TO RUTX_SYNC;
```

## Permisos que NO se otorgan

- Ningún derecho sobre `RDB$*`, tablas de sistema, ni objetos de otros
  esquemas.
- No DELETE en ninguna tabla de negocio (la reversión de ventas usa
  UPDATE de estatus según el código actual; re-evaluar si aparece un
  DELETE real en `Services/`).
- No se alteran usuarios ni roles existentes; SYSDBA queda intacto.

## Matriz de verificación de permisos (fase B, sobre la copia)

Ejecutada por `Deploy/Firebird/FaseB_03_matriz_permisos.ps1` conectando como
`RUTX_SYNC` al alias aislado. NUNCA se asume que todo INSERT deba fallar:

- **Positivos (deben funcionar):** SELECT literal de cada tabla otorgada y
  las escrituras reales de ventas, cobranza, folios y caja (INSERT/UPDATE
  según la tabla), ejecutadas dentro de transacción con ROLLBACK para no
  persistir datos.
- **Negativos (deben fallar):** DDL (`CREATE`/`DROP`), `DELETE` en tablas
  de negocio, SELECT sobre tablas fuera de alcance (p. ej. `DOCTOS_IN`),
  UPDATE de catálogos (p. ej. `MONEDAS`), y ejercicio de privilegios de
  administración/herencia (p. ej. que RUTX_SYNC intente otorgar permisos).
- **Regla de escalado:** si un trigger/generador rechaza una escritura
  legítima, se DETIENE y reporta el permiso exacto faltante; NO se otorgan
  privilegios amplios automáticamente. Cualquier grant adicional exige tu
  confirmación explícita.

## Checklist post-aplicación (en copia)

- [ ] Login móvil OK con credenciales del vendedor.
- [ ] `GET /api/v1/routes/sync` descarga catálogos completos.
- [ ] `POST /api/v1/pv/ventas` genera docto + folio + inventario
      (registro marcado `RUTX-E2E`, folio inicial/final documentados).
- [ ] `POST /api/v1/pv/noventa` (con foto) OK (idem).
- [ ] `POST /api/v1/cobranza/insert` afecta DOCTOS_CC correctamente (idem).
- [ ] `GET /api/v1/credito/clientes/{id}/documentos` devuelve saldos.
- [ ] Sin errores de permisos en `Logs/sincronizador.log`.
