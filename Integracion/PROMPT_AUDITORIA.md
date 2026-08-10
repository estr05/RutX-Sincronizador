# AUDITORÍA DE COMPATIBILIDAD: BD MICROSIP ↔ SINCRONIZADOR RUTX

> **Cómo usar** (una sola vez por PC):
> 1. Clona el repo del sincronizador (incluye este prompt y `auditar_compatibilidad.py`).
> 2. Edita **SOLO la sección ⚙️ CONFIGURACIÓN** con los datos de tu BD asignada.
> 3. Pega TODO este texto como primer mensaje en Freebuff (Buffy).
>
> La ruta de la BD se escribe **una sola vez**, en la sección de configuración.

---

## ⚙️ CONFIGURACIÓN (ÚNICO LUGAR PARA EDITAR)

```
BD asignada  : [RUTA\BD\NOMBRE.FDB]      (ej: C:\Microsip datos\BDEMPRESA.FDB)
Usuario FB   : [USUARIO]                 (default: SYSDBA)
Password FB  : [CLAVE]                   (default: masterkey)
Script       : auditar_compatibilidad.py  (junto a este prompt, en el repo del sincronizador)
appsettings  : (opcional) appsettings.json del entorno que usará el sincronizador.
               Si se omite, el script usa los IDs por defecto del código.
```

---

## 🚨 REGLA #1 — EVALUACIÓN 100% SOLO LECTURA (NO NEGOCIABLE)

Durante la evaluación **NO modificas absolutamente NADA**:

- ❌ **NO** insertes, actualices, borres ni alteres ningún dato de la BD.
- ❌ **NO** muevas, copies, renombres ni borres archivos (BD, script, configs, logs).
- ❌ **NO** edites `appsettings.json` ni ningún archivo de configuración.
- ❌ **NO** ejecutes scripts que escriban, ni `gbak` de restauración, ni nada destructivo.
- ✅ **SÍ** puedes: conectar a la BD (solo lectura), ejecutar SELECTs, correr el script de
  auditoría (es 100% solo-lectura), leer archivos y producir el reporte.

**Los cambios (FASE 2) solo se ejecutan si el usuario los aprueba explícitamente**
("aplícalo", "procede","analiza"). Hasta entonces, el entregable es **diagnóstico + plan propuesto**,
y debes declarar: *"NO he modificado nada. La evaluación fue 100% solo-lectura."*

---

## 🎯 TU MISIÓN (FASE 1 — solo lectura)

1. **Verifica el entorno**: que exista (a) la BD, (b) el script, (c) Python con `fdb`
   (`python -c "import fdb"`). Si falta `fdb`, pide autorización para `pip install fdb`
   (es la única excepción a la Regla #1 y no toca la BD ni el proyecto).
2. **Ejecuta la auditoría** (100% solo-lectura):
   ```bash
   python "RUTA\auditar_compatibilidad.py" "RUTA\BD\NOMBRE.FDB"
   # si la BD pide otro usuario/clave:
   python "...auditar_compatibilidad.py" "...NOMBRE.fdb" --user [USUARIO] --password [CLAVE]
   # si quieres verificar los IDs del appsettings del entorno:
   python "...auditar_compatibilidad.py" "...NOMBRE.fdb" --appsettings appsettings.json
   ```
   El script devuelve `[ OK ]` / `[AVISO]` / `[FALLO]` y un veredicto (APTA / APTA CON
   AVISOS / NO APTA) + código de salida. **No modifica nada** (conexión read-only).
3. **Interpreta**:
   - `[FALLO]` → requisito que falta. Es lo que impide producción. Atender todos.
   - `[AVISO]` → no rompe el flujo básico, pero revisar.
   - `[ OK ]` → requisito cumplido.
   - ⚠️ Las tablas **AGENTES / RUTAS / RUTAS_DET ya no se usan** en el sincronizador
     (login nativo Firebird): sus avisos **no bloquean**.
4. **Diagnostica causa raíz** de cada FALLO/AVISO con SELECTs de SOLO LECTURA:
   - ¿El ID esperado no existe pero hay otro equivalente? (el script ya lista los IDs
     disponibles del catálogo cuando falta uno).
   - ¿Falta el vendedor/cajero/caja? → verificar la cadena `VENDEDORES.NOMBRE = usuario`
     → `CAJEROS.USUARIO = usuario` → `CAJAS_CAJEROS` (acceso A/O).
   - ¿Es trigger/FK/generador faltante? → verificar si existe con otra nomenclatura.
5. **Clasifica la solución** (solo proponer, NO ejecutar):

   | Tipo | Qué es | ¿Lo resuelves tú? |
   |------|--------|-------------------|
   | **A** | Cambio de configuración del sincronizador (`appsettings.json`, entorno, IDs por defecto, ruta de BD) | ✅ Sí (FASE 2, con aprobación) |
   | **B** | Alta de DATOS en la BD (catálogos, vendedores, cajeros, accesos a cajas, formas de cobro, RFCS_LCO) | ✅ Sí (FASE 2, con respaldo y aprobación) |
   | **C** | Esquema/triggers de Microsip faltantes (tabla sin columna, trigger ausente, generador ausente) | ❌ No (requiere versión/esquema correcto de Microsip) → reportar |

6. **PUNTO DE CONTROL**: presenta el veredicto + hallazgos + plan propuesto y **ESPERA
   aprobación explícita** antes de cualquier cambio (Regla #1).

---

## 📦 ENTREGABLE FINAL — TABLA DE LO QUE FALTA CONFIGURAR/AGREGAR

Al finalizar la evaluación, devuelve el análisis en este formato (la IA interpreta los
resultados del script y traduce cada hallazgo a la acción concreta). Despues la IA corrobora estos datos conectandose a la BD y afirma/evalua los hallazgos:

```
📋 REPORTE DE COMPATIBILIDAD — BD [NOMBRE]
VEREDICTO: [APTA / APTA CON AVISOS / NO APTA] (OK: X | AVISO: Y | FALLO: Z)

| # | Hallazgo | Tipo | Qué falta (config/agregar) | Dónde exacto | Valor propuesto | Estado |
|---|----------|------|----------------------------|--------------|-----------------|--------|
| 1 | appsettings apunta a otra BD | A | Cambiar ruta de BD | appsettings.json → ConnectionStrings:FirebirdConnection | Database=...\NOMBRE.fdb | 🕓 Propuesta |
| 2 | DefaultCondPagoId 1 no existe | A | Reasignar ID de condición de pago | appsettings.json → MicrosipSettings:DefaultCondPagoId | [ID real del catálogo] | 🕓 Propuesta |
| 3 | Sin formas de cobro de crédito | B | Dar de alta FORMAS_COBRO | BD → FORMAS_COBRO (+ alinear CreditFormaCobroIds) | [INSERT propuesto] | 🕓 Propuesta |
| 4 | ... | C | Requiere intervención de Microsip | ... | ... | ⛔ Pendiente |

🔧 CAMBIOS REALIZADOS: [NINGUNO — solo evaluación] (o detalle si hubo FASE 2 aprobada)
⚠️ PENDIENTES (no resueltos): cada uno con su causa y qué se necesita.
✅ PRÓXIMOS PASOS SUGERIDOS: login → sync matutino → venta de prueba → ticket en PVenta.
```

---

## 🟡 FASE 2 — ADECUACIÓN (SOLO CON APROBACIÓN EXPLÍCITA DEL USUARIO)

Si el usuario aprueba el plan, aplica los fixes **uno por uno, verificando cada uno**, con
transacción y ROLLBACK como respaldo:

1. **Solo aplica lo aprobado**: ningún cambio fuera del plan aprobado.
2. **Respaldar primero**: transacción con ROLLBACK por lote. Para respaldo de archivo usar
   `gbak -b`/`nbackup`; **nunca** copiar el `.fdb` con `copy` mientras Firebird lo tiene abierto.
3. **Nunca modificar estructura (DDL), triggers ni stored procedures** de Microsip.
4. **Antes de INSERT**: verificar que el ID no colisiona; si colisiona, proponer ID libre o
   reasignación de config (Tipo A).
5. **Respetar FKs**: `CAJAS_CAJEROS` requiere caja y cajero existentes; Esto es importante ya que influye en la logica de la App Movil y el Sincronizador
6. **Nada destructivo sin confirmación explícita** (DELETE/UPDATE masivos, cancelaciones).
7. **Caché de Microsip**: si Microsip (PVenta/principal) está abierto, sus catálogos están
   cacheados en memoria y **no verá los cambios hasta reiniciar**.
8. **Cadena de datos del flujo** (orden): `VENDEDORES (vendedor) → CAJEROS (cajero por
   USUARIO) → CAJAS_CAJEROS (acceso A+O a la caja)`. Si falta un eslabón, ese usuario no
   puede operar. *(El login actual NO usa AGENTES: valida credenciales nativas de Firebird.)*
9. **Re-ejecuta la auditoría** tras cada fix: objetivo **0 FALLOS corregibles**. Los FALLOS
   Tipo C se documentan como pendientes; los AVISOS remanentes deben estar justificados.

### Checklist de validación punta a punta (después de FASE 2)
1. **Login**: `POST /api/auth/login` (usuario Firebird nativo cuyo nombre exista en
   `VENDEDORES.NOMBRE`) → devuelve `vendedor_id` + `caja_id` + token.
2. **Sync matutino**: `GET /api/ruta/sync-matutino` → clientes, productos, formas de cobro.
3. **Venta de prueba**: `POST /api/v1/pv/ventas` → folio consecutivo, `APLICADO='S'`.
4. **Visible en Microsip**: el ticket aparece en PVenta **con la caja abierta** (si no hay
   apertura en `MOVTOS_CAJAS`, PVenta no muestra los tickets del día).
5. **Al concluir**: Pedir al usuario que proceda a crear sus usuarios/vendedores relacionados. Con base al manual adjunto en el Repo; 'MANUAL_CONFIGURACION_RUTX_MICROSIP.pdf'

---

## 📌 CONTEXTO RÁPIDO DEL SINCRONIZADOR (para interpretar mejor)

- **Login**: credenciales nativas Firebird, **sin AGENTES**. Resuelve:
  vendedor = `VENDEDORES.NOMBRE` = usuario · cajero = `CAJEROS.USUARIO` = usuario ·
  caja = `CAJAS_CAJEROS` (A→O, con evidencia en `MOVTOS_CAJAS`/`DOCTOS_PV`) ·
  almacén = `CAJAS.ALMACEN_ID`.
- **Endpoints**: `POST /api/auth/login` · `GET /api/ruta/sync-matutino` · `POST /api/v1/pv/ventas`
  · `POST /api/v1/pv/noventa` · `POST /api/v1/cobranza/insert` · `GET /api/v1/credito/pedidos`
  · `GET /health` · (otros en `api/v1/...`). Puerto por defecto: 5047.
- **IDs de `MicrosipSettings`** (defaults): Moneda 1 · CondPago 1 (Dev: 2422) · Impuesto 622
  · Sucursal 4274 · Almacén 19 · Cajero fallback 2419 · Forma de cobro 67 ·
  Crédito [71, 703, 2205] · Concepto CxC 11 · Lista de precios 42.
- **Triggers críticos**: `DOCTOS_PV_BEFINS`, `DOCTOS_PV_DET_BEFINS`, `DOCTOS_PV_COBROS_BEFINS`,
  `DOCTOS_PV_COBROS_AFTINS_0`, `DOCTOS_PV_AFTUPD_0`, `DOCTOS_PV_BEFUPD_0`,
  `DOCTOS_PV_LIGAS_BEFINS`, `DOCTOS_CC_BEFINS` + generador `ID_DOCTOS`.
- **FK**: `CAJEROS_A_DOCTOS_PV` (FkResolverService).
- **Regla de oro**: NUNCA modificar estructura (DDL) ni triggers de Microsip. Solo datos de
  catálogos/configuración, con respaldo y aprobación.

---

*Fin del SUPER PROMPT. Pégame completo y empieza por la FASE 1 (solo lectura).* 🚀
