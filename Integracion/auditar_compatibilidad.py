# -*- coding: utf-8 -*-
"""
================================================================================
 AUDITOR DE COMPATIBILIDAD: sincronizador_rutx <-> BD Microsip (Firebird)
================================================================================
Valida si una base Firebird de Microsip contiene lo que el sincronizador
Rutx necesita para operar (esquema + datos minimos + IDs de configuracion).

ALINEADO AL ESTADO ACTUAL DEL SINCRONIZADOR (login nativo, sin AGENTES):
  - Login nativo Firebird: VENDEDORES.NOMBRE = usuario y CAJEROS.USUARIO = usuario.
    La tabla AGENTES YA NO se usa ni en el login ni en la sincronizacion.
  - RUTAS / RUTAS_DET ya NO se usan para el mapeo de clientes (RouteService).
  - IDs criticos: se leen de MicrosipSettings del appsettings del entorno
    (por defecto: appsettings.json junto al script; --appsettings para otro).

USO:
  python auditar_compatibilidad.py "C:\\ruta\\CHOCOLATES.fdb" --user SYSDBA --password <tu_password>
  python auditar_compatibilidad.py "C:\\ruta\\CHOCOLATES.fdb" --appsettings appsettings.json

  NOTA DE SEGURIDAD: --password es obligatorio. No existe un valor por defecto para
  evitar el uso accidental de la contrasena por defecto de Firebird ('masterkey').
  Si tu instalacion usa la contrasena por defecto de Firebird, cambiala antes de
  conectar esta BD a produccion (incumple SEC-01).

SALIDA: [ OK ] / [AVISO] / [FALLO] por verificacion + veredicto final
  APTA            -> lista para produccion
  APTA CON AVISOS -> funciona, pero revisar los avisos
  NO APTA         -> faltan requisitos (IDs de config o datos) -> ver FALLOS
  Exit code: 0 = apta (con o sin avisos), 1 = no apta (con fallos).

SOLO LECTURA: la conexion se abre con aislamiento read-only; solo ejecuta SELECTs
sobre metadatos y catalogos. No modifica nada (Regla #1).
================================================================================
"""

import argparse
import json
import os
import sys

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

try:
    import fdb
except ImportError:
    print("ERROR: No esta instalado el driver 'fdb'. Instalar con: pip install fdb")
    sys.exit(2)

# ==============================================================================
# 1. TABLAS REQUERIDAS con las columnas que el sincronizador usa en sus SQL
#    (extraidas de FirebirdAuthService, VentaServicePv, RouteService,
#     ClienteService, CreditoService, CobranzaService, FolioService)
# ==============================================================================
REQUIRED_TABLES = {
    # Catalogos base (objetivo de las FKs de los inserts: si faltan, el INSERT rompe)
    "MONEDAS": ["MONEDA_ID"],
    "CONDICIONES_PAGO": ["COND_PAGO_ID"],
    "ALMACENES": ["ALMACEN_ID"],
    "CONCEPTOS_CC": ["CONCEPTO_CC_ID"],
    "VENDEDORES": ["VENDEDOR_ID", "NOMBRE", "POLITICA_COMIS_VEN_ID", "ES_PREDET", "OCULTO"],
    "CLIENTES": ["CLIENTE_ID", "NOMBRE", "LIMITE_CREDITO", "MONEDA_ID", "COND_PAGO_ID",
                 "VENDEDOR_ID", "ESTATUS", "SUJETO_IEPS", "DIFERIR_CFDI_COBROS"],
    "DIRS_CLIENTES": ["DIR_CLI_ID", "CLIENTE_ID", "CALLE", "COLONIA", "POBLACION", "CODIGO_POSTAL"],
    "CAJAS": ["CAJA_ID", "NOMBRE", "ALMACEN_ID", "OCULTO", "FORMA_COBRO_PREDET_ID",
              "MANEJAR_VENDEDORES"],
    "CAJEROS": ["CAJERO_ID", "NOMBRE", "USUARIO", "OPERAR_CAJAS", "ABRIR_CAJAS", "OCULTO"],
    "CAJAS_CAJEROS": ["CAJA_ID", "CAJERO_ID", "TIPO_ACCESO"],
    "FOLIOS_CAJAS": ["CAJA_ID", "TIPO_DOCTO", "SERIE", "CONSECUTIVO"],
    "ARTICULOS": ["ARTICULO_ID", "NOMBRE", "ESTATUS", "ES_ALMACENABLE"],
    "CLAVES_ARTICULOS": ["ARTICULO_ID", "CLAVE_ARTICULO"],
    "PRECIOS_ARTICULOS": ["ARTICULO_ID", "PRECIO", "PRECIO_EMPRESA_ID", "PRECIO_ARTICULO_ID"],
    "PRECIOS_EMPRESA": ["PRECIO_EMPRESA_ID", "NOMBRE"],
    "IMPUESTOS": ["IMPUESTO_ID", "PCTJE_IMPUESTO"],
    "IMPUESTOS_ARTICULOS": ["ARTICULO_ID", "IMPUESTO_ID"],
    "FORMAS_COBRO": ["FORMA_COBRO_ID", "NOMBRE", "TIPO"],
    "SUCURSALES": ["SUCURSAL_ID", "NOMBRE", "CALLE", "NOMBRE_CALLE", "NUM_EXTERIOR",
                    "NUM_INTERIOR", "COLONIA", "POBLACION", "CODIGO_POSTAL", "TELEFONO1"],
    "RFCS_LCO": ["RFC_LCO_ID", "RFC", "NOMBRE_FISCAL", "DOMICILIO_FISCAL", "ESTATUS_VERIFICACION"],
    "SALDOS_IN": ["ALMACEN_ID", "ARTICULO_ID", "ANO", "MES", "ENTRADAS_UNIDADES", "SALIDAS_UNIDADES"],
    "DOCTOS_PV": ["DOCTO_PV_ID", "CAJA_ID", "TIPO_DOCTO", "SUCURSAL_ID", "FOLIO",
                   "FECHA", "HORA", "CAJERO_ID", "CLIENTE_ID", "DIR_CLI_ID",
                   "ALMACEN_ID", "MONEDA_ID", "IMPUESTO_INCLUIDO", "TIPO_CAMBIO",
                   "ESTATUS", "APLICADO", "PROCESO_ORIGEN", "SISTEMA_ORIGEN", "VENDEDOR_ID",
                   "IMPORTE_NETO", "TOTAL_IMPUESTOS", "TOTAL_RETENCIONES",
                   "PESO_EMBARQUE", "DESCRIPCION", "ES_CFD", "ENVIADO",
                   "CFDI_CERTIFICADO", "CARGAR_SUN", "USUARIO_CREADOR",
                   "FECHA_HORA_CREACION", "PARTIDA_AJUSTE_ID", "PRECIO_ORIG_PARTIDA_AJUSTE",
                   "FECHA_VIGENCIA"],
    "DOCTOS_PV_DET": ["DOCTO_PV_DET_ID", "DOCTO_PV_ID", "ARTICULO_ID", "UNIDADES",
                       "PRECIO_UNITARIO", "PRECIO_UNITARIO_IMPTO", "IMPUESTO_POR_UNIDAD",
                       "PRECIO_TOTAL_NETO", "PRECIO_MODIFICADO", "ROL", "POSICION"],
    "IMPUESTOS_DOCTOS_PV_DET": ["DOCTO_PV_DET_ID", "IMPUESTO_ID", "DOCTO_PV_ID",
                                 "ID_INTERNO_TIPO_IMPTO", "TIPO_CALC",
                                 "IMPORTE_IMPUESTO_BRUTO", "VENTA_NETA", "VENTA_BRUTA",
                                 "OTROS_IMPUESTOS", "PCTJE_IMPUESTO", "IMPORTE_IMPUESTO",
                                 "UNIDADES_IMPUESTO", "IMPORTE_UNITARIO_IMPUESTO"],
    "IMPUESTOS_DOCTOS_PV": ["DOCTO_PV_ID", "IMPUESTO_ID", "VENTA_NETA", "VENTA_BRUTA",
                             "OTROS_IMPUESTOS", "PCTJE_IMPUESTO", "IMPORTE_IMPUESTO",
                             "UNIDADES_IMPUESTO", "IMPORTE_UNITARIO_IMPUESTO"],
    "DOCTOS_PV_COBROS": ["DOCTO_PV_COBRO_ID", "DOCTO_PV_ID", "TIPO", "FORMA_COBRO_ID",
                          "IMPORTE", "TIPO_CAMBIO", "IMPORTE_MON_DOC"],
    "DOCTOS_PV_LIGAS": ["DOCTO_PV_LIGA_ID", "DOCTO_PV_FTE_ID", "DOCTO_PV_DEST_ID"],
    "DOCTOS_CC": ["DOCTO_CC_ID", "CONCEPTO_CC_ID", "NATURALEZA_CONCEPTO",
                   "FOLIO", "SUCURSAL_ID", "FECHA", "HORA",
                   "CLIENTE_ID", "IMPORTE_COBRO", "TIPO_CAMBIO",
                   "CANCELADO", "APLICADO", "SISTEMA_ORIGEN",
                   "ESTATUS", "ESTATUS_ANT", "ES_CFD", "TIENE_ANTICIPO",
                   "ENVIADO", "CFDI_CERTIFICADO", "MODALIDAD_FACTURACION",
                   "CONTABILIZADO_GYP", "INTEG_BA", "CONTABILIZADO_BA"],
    "DOCTOS_ENTRE_SIS": ["CLAVE_SIS_FTE", "DOCTO_FTE_ID", "CLAVE_SIS_DEST",
                          "DOCTO_DEST_ID", "TIPO_DOCTO"],
    "SALDOS_CC": ["CLIENTE_ID", "CARGOS_CXC", "CREDITOS_CXC"],
    "VENCIMIENTOS_CARGOS_CC": ["DOCTO_CC_ID", "FECHA_VENCIMIENTO"],
    "MOVTOS_EFVO_CAJA": [],  # Solo se verifica existencia (la escribe el trigger)
    "MOVTOS_CAJAS": ["MOVTO_CAJA_ID", "FECHA", "HORA", "TIPO_MOVTO", "CAJA_ID",
                      "CAJEROS_HABILITADOS"],
}

# Tablas de flujos RETIRADOS: existen en la BD de Microsip, pero el sincronizador
# actual ya no las usa. Si faltan o estan vacias, NO bloquean (solo se reportan).
OPTIONAL_TABLES = {
    "AGENTES": ["AGENTE_ID", "NOMBRE", "USUARIO", "CLAVE", "VENDEDOR_ID", "SUCURSAL_ID", "ALMACEN_ID"],
    "RUTAS": ["RUTA_ID", "NOMBRE", "ESTATUS"],
    "RUTAS_DET": ["RUTA_ID", "CLIENTE_ID", "DIA", "DIA_POSICION"],
}

# ==============================================================================
# 2. TRIGGERS CRITICOS (sin ellos el sincronizador NO puede operar)
# ==============================================================================
REQUIRED_TRIGGERS = [
    "DOCTOS_PV_BEFINS",          # Genera DOCTO_PV_ID cuando llega -1
    "DOCTOS_PV_DET_BEFINS",      # Genera DOCTO_PV_DET_ID y POSICION
    "DOCTOS_PV_COBROS_BEFINS",   # Genera DOCTO_PV_COBRO_ID
    "DOCTOS_PV_COBROS_AFTINS_0", # Registra MOVTOS_EFVO_CAJA
    "DOCTOS_PV_AFTUPD_0",        # Dispara APLICA_DOCTO_PV (inventario + CxC)
    "DOCTOS_PV_BEFUPD_0",        # Dispara DESAPLICA_DOCTO_PV (cancelaciones)
    "DOCTOS_PV_LIGAS_BEFINS",    # Genera DOCTO_PV_LIGA_ID (cobranza)
    "DOCTOS_CC_BEFINS",          # Genera DOCTO_CC_ID (cobranza/CxC)
]

# ==============================================================================
# 3. CONSTRAINT FK usada por FkResolverService
# ==============================================================================
REQUIRED_FK_CONSTRAINT = "CAJEROS_A_DOCTOS_PV"

# ==============================================================================
# 4. IDS CRITICOS por defecto (usados si no hay --appsettings):
#    (tabla, columna_id, {id: descripcion}, nivel_si_falta, tipo_check)
#    - nivel_si_falta: 'FALLO' (imprescindible) o 'AVISO' (fallback en codigo)
#    - tipo_check: 'TODOS' (cada id debe existir) o 'AL_MENOS_UNO' (basta con 1)
# ==============================================================================
DEFAULT_CRITICAL_IDS = [
    ("MONEDAS", "MONEDA_ID", {1: "Moneda base (Peso)"}, "FALLO", "TODOS"),
    ("CONDICIONES_PAGO", "COND_PAGO_ID", {1: "Contado (appsettings)", 2422: "Contado (Development)"},
     "FALLO", "AL_MENOS_UNO"),
    ("IMPUESTOS", "IMPUESTO_ID", {622: "IVA 16%"}, "FALLO", "TODOS"),
    ("FORMAS_COBRO", "FORMA_COBRO_ID", {67: "EFECTIVO"}, "FALLO", "TODOS"),
    ("FORMAS_COBRO", "FORMA_COBRO_ID", {71: "CREDITO", 703: "CREDITO 15D", 2205: "CREDITO 30D"},
     "AVISO", "TODOS"),  # Solo afectan ventas a credito
    ("SUCURSALES", "SUCURSAL_ID", {4274: "Sucursal por defecto"}, "FALLO", "TODOS"),
    ("ALMACENES", "ALMACEN_ID", {19: "Almacen por defecto"}, "FALLO", "TODOS"),
    ("PRECIOS_EMPRESA", "PRECIO_EMPRESA_ID", {42: "Lista de precios (DefaultPrecioEmpresaId)"},
     "FALLO", "TODOS"),
    # Cajero 2419 es SOLO fallback (en produccion se resuelve por USUARIO del login nativo)
    ("CAJEROS", "CAJERO_ID", {2419: "Cajero fallback por defecto"}, "AVISO", "TODOS"),
    ("CONCEPTOS_CC", "CONCEPTO_CC_ID", {11: "Concepto Cobro (abono CxC)"}, "FALLO", "TODOS"),
]

# ==============================================================================
# 5. DATOS MINIMOS PARA OPERAR (alineados al flujo actual, sin AGENTES)
# ==============================================================================
MIN_DATA_CHECKS = [
    ("Al menos 1 caja activa (OCULTO='N')",
     "SELECT COUNT(*) FROM CAJAS WHERE OCULTO = 'N'", 1, "CRITICO"),
    ("Al menos 1 vendedor activo (OCULTO='N')",
     "SELECT COUNT(*) FROM VENDEDORES WHERE OCULTO = 'N'", 1, "CRITICO"),
    ("Al menos 1 cajero activo (OCULTO='N')",
     "SELECT COUNT(*) FROM CAJEROS WHERE OCULTO = 'N'", 1, "CRITICO"),
    ("Al menos 1 vendedor con CAJERO por NOMBRE/USUARIO (login nativo)",
     "SELECT COUNT(*) FROM VENDEDORES v INNER JOIN CAJEROS c ON TRIM(c.USUARIO) = TRIM(v.NOMBRE) "
     "WHERE v.OCULTO = 'N' AND c.OCULTO = 'N'", 1, "CRITICO"),
    ("Al menos 1 cajero con acceso OPERAR a caja (CAJAS_CAJEROS)",
     "SELECT COUNT(DISTINCT cc.CAJERO_ID) FROM CAJAS_CAJEROS cc INNER JOIN CAJAS c "
     "ON c.CAJA_ID = cc.CAJA_ID WHERE cc.TIPO_ACCESO = 'O' AND c.OCULTO = 'N'", 1, "CRITICO"),
    ("Al menos 1 cliente activo (ESTATUS='A')",
     "SELECT COUNT(*) FROM CLIENTES WHERE ESTATUS = 'A'", 1, "CRITICO"),
    ("Al menos 1 cliente con direccion en DIRS_CLIENTES",
     "SELECT COUNT(DISTINCT CLIENTE_ID) FROM DIRS_CLIENTES", 1, "CRITICO"),
    ("Al menos 1 articulo activo (ESTATUS='A')",
     "SELECT COUNT(*) FROM ARTICULOS WHERE ESTATUS = 'A'", 1, "CRITICO"),
    ("Al menos 1 articulo con precio (PRECIOS_ARTICULOS)",
     "SELECT COUNT(DISTINCT ARTICULO_ID) FROM PRECIOS_ARTICULOS", 1, "CRITICO"),
    ("Al menos 1 articulo con existencias > 0 (SALDOS_IN)",
     "SELECT COUNT(DISTINCT ARTICULO_ID) FROM SALDOS_IN "
     "WHERE (ENTRADAS_UNIDADES - SALIDAS_UNIDADES) > 0", 1, "AVISO"),
    ("Al menos 1 forma de cobro en catalogo",
     "SELECT COUNT(*) FROM FORMAS_COBRO", 1, "CRITICO"),
    ("Al menos 1 condicion de pago",
     "SELECT COUNT(*) FROM CONDICIONES_PAGO", 1, "CRITICO"),
    ("Folio 'V' en FOLIOS_CAJAS (auto-creable por FolioService)",
     "SELECT COUNT(*) FROM FOLIOS_CAJAS WHERE TIPO_DOCTO = 'V'", 1, "AVISO"),
    ("Folio 'P' en FOLIOS_CAJAS (auto-creable por FolioService)",
     "SELECT COUNT(*) FROM FOLIOS_CAJAS WHERE TIPO_DOCTO = 'P'", 1, "AVISO"),
    ("Al menos 1 emisor fiscal (RFCS_LCO activo)",
     "SELECT COUNT(*) FROM RFCS_LCO WHERE ESTATUS_VERIFICACION = 'A'", 1, "AVISO"),
    ("Al menos 1 impuesto en catalogo",
     "SELECT COUNT(*) FROM IMPUESTOS", 1, "AVISO"),
]

# ==============================================================================

def get_tables(cur):
    cur.execute("SELECT TRIM(RDB$RELATION_NAME) FROM RDB$RELATIONS "
                "WHERE RDB$SYSTEM_FLAG = 0 AND RDB$VIEW_BLR IS NULL")
    return {r[0].upper() for r in cur.fetchall()}

def get_columns(cur, table):
    cur.execute("SELECT TRIM(RDB$FIELD_NAME) FROM RDB$RELATION_FIELDS "
                "WHERE RDB$RELATION_NAME = ?", (table,))
    return {r[0].upper() for r in cur.fetchall()}

def get_triggers(cur):
    cur.execute("SELECT TRIM(RDB$TRIGGER_NAME) FROM RDB$TRIGGERS")
    return {r[0].upper() for r in cur.fetchall()}

def get_fk_constraints(cur):
    cur.execute("SELECT TRIM(RDB$CONSTRAINT_NAME) FROM RDB$RELATION_CONSTRAINTS "
                "WHERE RDB$CONSTRAINT_TYPE = 'FOREIGN KEY'")
    return {r[0].upper() for r in cur.fetchall()}

def ids_existentes(cur, tabla, columna_id, limite=15):
    """Lista los primeros IDs del catalogo (para sugerir el mapeo correcto)."""
    try:
        cur.execute(f"SELECT FIRST {limite} {columna_id} FROM {tabla} ORDER BY {columna_id}")
        return [r[0] for r in cur.fetchall()]
    except Exception:
        return None

def cargar_appsettings(ruta):
    """Lee MicrosipSettings desde un appsettings y construye la lista de IDs a verificar."""
    try:
        with open(ruta, encoding='utf-8-sig') as f:
            data = json.load(f)
        ms = (data.get("MicrosipSettings") or {}) if isinstance(data, dict) else {}
    except Exception:
        return None

    ids = []
    def add(tabla, col, vals, nivel, tipo):
        if vals:
            ids.append((tabla, col, vals, nivel, tipo))

    add("MONEDAS", "MONEDA_ID", {int(ms["DefaultMonedaId"]): "Moneda base (appsettings)"},
        "FALLO", "TODOS")
    add("CONDICIONES_PAGO", "COND_PAGO_ID",
        {int(ms["DefaultCondPagoId"]): "Contado (appsettings)"},
        "FALLO", "AL_MENOS_UNO")
    add("IMPUESTOS", "IMPUESTO_ID", {int(ms["DefaultImpuestoId"]): "IVA por defecto (appsettings)"},
        "FALLO", "TODOS")
    add("FORMAS_COBRO", "FORMA_COBRO_ID", {int(ms["DefaultFormaCobroId"]): "Forma de cobro por defecto (appsettings)"},
        "FALLO", "TODOS")
    credito = ms.get("CreditFormaCobroIds") or []
    add("FORMAS_COBRO", "FORMA_COBRO_ID",
        {int(c): "Credito (appsettings)" for c in credito},
        "AVISO", "TODOS")
    add("SUCURSALES", "SUCURSAL_ID", {int(ms["DefaultSucursalId"]): "Sucursal por defecto (appsettings)"},
        "FALLO", "TODOS")
    add("ALMACENES", "ALMACEN_ID", {int(ms["DefaultAlmacenId"]): "Almacen por defecto (appsettings)"},
        "FALLO", "TODOS")
    add("PRECIOS_EMPRESA", "PRECIO_EMPRESA_ID",
        {int(ms["DefaultPrecioEmpresaId"]): "Lista de precios (appsettings)"},
        "FALLO", "TODOS")
    add("CAJEROS", "CAJERO_ID", {int(ms["DefaultCajeroId"]): "Cajero fallback (appsettings)"},
        "AVISO", "TODOS")
    add("CONCEPTOS_CC", "CONCEPTO_CC_ID",
        {int(ms["DefaultConceptoCobroId"]): "Concepto Cobro (appsettings)"},
        "FALLO", "TODOS")
    return ids

def main():
    parser = argparse.ArgumentParser(
        description="Audita una BD Firebird de Microsip para compatibilidad con el sincronizador Rutx.")
    parser.add_argument("db_path", help="Ruta del archivo .fdb (ej: C:\\Microsip datos\\CHOCOLATES.fdb)")
    parser.add_argument("--user", default="SYSDBA", help="Usuario Firebird (default: SYSDBA)")
    parser.add_argument(
        "--password",
        required=True,
        help=(
            "Password de Firebird para el usuario especificado. "
            "OBLIGATORIO: no existe valor por defecto para prevenir el uso "
            "accidental de credenciales inseguras (SEC-01)."
        ),
    )
    parser.add_argument("--appsettings", default=None,
                        help="Ruta al appsettings del entorno (default: appsettings.json junto al script)")
    args = parser.parse_args()

    # IDs a verificar: desde --appsettings (o el del repo) si existe; si no, defaults.
    ruta_settings = args.appsettings
    origen_ids = "defaults del codigo"
    if not ruta_settings:
        candidata = os.path.join(os.path.dirname(os.path.abspath(__file__)), "appsettings.json")
        if os.path.exists(candidata):
            ruta_settings = candidata
    if ruta_settings and os.path.exists(ruta_settings):
        crit_ids = cargar_appsettings(ruta_settings)
        if crit_ids:
            origen_ids = f"appsettings: {ruta_settings}"
        else:
            crit_ids = DEFAULT_CRITICAL_IDS
            origen_ids = "defaults del codigo (no se pudo leer el appsettings)"
    else:
        crit_ids = DEFAULT_CRITICAL_IDS

    print("=" * 78)
    print(" AUDITOR DE COMPATIBILIDAD sincronizador_rutx <-> BD Microsip")
    print("=" * 78)
    print(f" BD : {args.db_path}")
    print(f" Usr: {args.user}")
    print(f" IDs: {origen_ids}")
    print()

    ok_count = 0
    warn_count = 0
    fail_count = 0

    def report(estado, seccion, detalle):
        nonlocal ok_count, warn_count, fail_count
        if estado == "OK":
            ok_count += 1
            simbolo = "[ OK ]"
        elif estado == "AVISO":
            warn_count += 1
            simbolo = "[AVISO]"
        else:
            fail_count += 1
            simbolo = "[FALLO]"
        print(f" {simbolo} {seccion}: {detalle}")

    # ------------------------------------------------------------------ 0. CONEXION
    print("--" * 39)
    print(" 0. CONEXION Y ACCESIBILIDAD")
    print("--" * 39)
    try:
        try:
            conn = fdb.connect(dsn=args.db_path, user=args.user,
                               password=args.password, charset='UTF8',
                               isolation_level=fdb.ISOLATION_LEVEL_READ_COMMITED_RO)
            print(" [ OK ] Conexion abierta en modo SOLO LECTURA (defensa Regla #1)")
        except (TypeError, AttributeError, KeyError, ValueError):
            conn = fdb.connect(dsn=args.db_path, user=args.user,
                               password=args.password, charset='UTF8')
            print(" [AVISO] Modo solo-lectura no soportado por este driver; "
                  "usando conexion normal (el script solo ejecuta SELECTs)")
    except Exception as e:
        print(f" [FALLO] No se pudo conectar a la BD: {e}")
        print()
        print(" VEREDICTO FINAL: NO APTA (BD inaccesible)")
        sys.exit(1)
    cur = conn.cursor()
    try:
        cur.execute("SELECT MON$DATABASE_NAME, MON$DATABASE_VERSION FROM MON$DATABASE")
        row = cur.fetchone()
        report("OK", "Conexion establecida",
               f"Firebird {row[1]} | Archivo: {row[0]}")
    except Exception:
        report("OK", "Conexion establecida", "(sin detalle de version)")
    print()

    # ------------------------------------------------------------------ 1. TABLAS
    print("--" * 39)
    print(" 1. TABLAS REQUERIDAS Y COLUMNAS")
    print("--" * 39)
    tables = get_tables(cur)
    column_cache = {}
    for tabla, columnas in REQUIRED_TABLES.items():
        t = tabla.upper()
        if t not in tables:
            report("FALLO", f"Tabla {tabla}", "NO EXISTE en la BD")
            continue
        column_cache[t] = get_columns(cur, t)
        if not columnas:
            report("OK", f"Tabla {tabla}", "existe")
            continue
        faltantes = [c for c in columnas if c.upper() not in column_cache[t]]
        if faltantes:
            report("FALLO", f"Tabla {tabla}",
                   f"existe pero FALTAN columnas: {', '.join(faltantes)}")
        else:
            report("OK", f"Tabla {tabla}", f"existe con sus {len(columnas)} columnas clave")

    # Tablas opcionales (flujos retirados: AGENTES, RUTAS, RUTAS_DET)
    for tabla, columnas in OPTIONAL_TABLES.items():
        t = tabla.upper()
        if t not in tables:
            report("AVISO", f"Tabla {tabla} (opcional)",
                   "no existe; el sincronizador actual NO la usa, no bloquea")
        else:
            column_cache[t] = get_columns(cur, t)
            faltantes = [c for c in columnas if c.upper() not in column_cache[t]]
            if faltantes:
                report("AVISO", f"Tabla {tabla} (opcional)",
                       f"existe sin algunas columnas ({', '.join(faltantes)}); no bloquea")
            else:
                report("AVISO", f"Tabla {tabla} (opcional)",
                       "existe; el sincronizador actual NO la usa")
    print()

    # ------------------------------------------------------------------ 2. TRIGGERS
    print("--" * 39)
    print(" 2. TRIGGERS CRITICOS")
    print("--" * 39)
    triggers = get_triggers(cur)
    for trig in REQUIRED_TRIGGERS:
        if trig.upper() in triggers:
            report("OK", f"Trigger {trig}", "existe")
        else:
            report("FALLO", f"Trigger {trig}",
                   "NO EXISTE. Revisar si la BD tiene el esquema completo de Microsip PV")
    print("   NOTA: Los nombres de triggers pueden variar levemente entre versiones de Microsip;")
    print("         si un [FALLO] aparece solo por nombre, verificar la funcion equivalente.")
    print()

    # ------------------------------------------------------------------ 3. FK CONSTRAINT
    print("--" * 39)
    print(" 3. CONSTRAINT DE FK (FkResolverService)")
    print("--" * 39)
    fks = get_fk_constraints(cur)
    if REQUIRED_FK_CONSTRAINT.upper() in fks:
        report("OK", f"FK {REQUIRED_FK_CONSTRAINT}", "existe en metadatos")
    else:
        report("AVISO", f"FK {REQUIRED_FK_CONSTRAINT}",
               "NO EXISTE -> FkResolver usara fallback (DefaultCajeroId). "
               "No rompe el flujo si el login nativo resuelve el cajero por USUARIO.")
    print()

    # ------------------------------------------------------------------ 4. IDS CRITICOS
    print("--" * 39)
    print(" 4. IDS CRITICOS (appsettings.json / codigo)")
    print("--" * 39)
    for tabla, columna_id, ids, nivel_falta, tipo_check in crit_ids:
        t = tabla.upper()
        if t not in tables:
            report("AVISO", f"Catalogo {tabla}", "tabla no existe (se omite verificacion de IDs)")
            continue
        try:
            cur.execute(f"SELECT {columna_id} FROM {tabla}")
            existentes = {r[0] for r in cur.fetchall()}
        except Exception as e:
            report("AVISO", f"Catalogo {tabla}", f"no verificable (columna {columna_id}?): {e}")
            continue

        if tipo_check == "AL_MENOS_UNO":
            encontrados = [idv for idv in ids if idv in existentes]
            if encontrados:
                for id_val, desc in ids.items():
                    if id_val in existentes:
                        report("OK", f"{tabla}.{columna_id}={id_val}", desc)
                    else:
                        report("AVISO", f"{tabla}.{columna_id}={id_val}",
                               f"{desc} -> no existe, pero hay otra condicion valida (id {encontrados[0]})")
            else:
                for id_val, desc in ids.items():
                    report("FALLO", f"{tabla}.{columna_id}={id_val}",
                           f"{desc} -> NINGUNA condicion valida en la BD")
                disp = ids_existentes(cur, t, columna_id)
                if disp:
                    report("AVISO", f"{tabla}.{columna_id}",
                           f"IDs disponibles en la BD: {disp} -> revisar que ID debe apuntar appsettings")
            continue

        for id_val, desc in ids.items():
            if id_val in existentes:
                report("OK", f"{tabla}.{columna_id}={id_val}", desc)
            else:
                report(nivel_falta, f"{tabla}.{columna_id}={id_val}",
                       f"{desc} -> NO ENCONTRADO en la BD")
                disp = ids_existentes(cur, t, columna_id)
                if disp:
                    report("AVISO", f"{tabla}.{columna_id}",
                           f"IDs disponibles en la BD: {disp} -> revisar que ID debe apuntar appsettings")

    # Generador usado por los triggers BEFINS para generar DOCTO_PV_ID / DOCTO_CC_ID
    try:
        cur.execute("SELECT COUNT(*) FROM RDB$GENERATORS WHERE RDB$SYSTEM_FLAG = 0 "
                    "AND TRIM(RDB$GENERATOR_NAME) = 'ID_DOCTOS'")
        if cur.fetchone()[0] > 0:
            report("OK", "Generador ID_DOCTOS", "existe (los triggers BEFINS podran generar IDs)")
        else:
            report("FALLO", "Generador ID_DOCTOS",
                   "NO EXISTE -> los triggers de generacion de IDs fallaran")
    except Exception as e:
        report("AVISO", "Generador ID_DOCTOS", f"no verificable: {e}")
    print()

    # ------------------------------------------------------------------ 5. DATOS MINIMOS
    print("--" * 39)
    print(" 5. DATOS MINIMOS PARA OPERAR")
    print("--" * 39)
    for desc, sql, minimo, nivel in MIN_DATA_CHECKS:
        try:
            cur.execute(sql)
            count = cur.fetchone()[0]
        except Exception as e:
            report("FALLO" if nivel == "CRITICO" else "AVISO", desc, f"error de consulta: {e}")
            continue
        if count >= minimo:
            report("OK", desc, f"{count} registro(s)")
        else:
            estado = "FALLO" if nivel == "CRITICO" else "AVISO"
            report(estado, desc, f"solo {count} (minimo {minimo})")
    print()

    # ------------------------------------------------------------------ 6. LOGIN NATIVO: VENDEDOR -> CAJERO -> CAJA
    print("--" * 39)
    print(" 6. LOGIN NATIVO (sin AGENTES): VENDEDOR -> CAJERO -> CAJA")
    print("--" * 39)
    print("   El login resuelve: VENDEDORES.NOMBRE = usuario, CAJEROS.USUARIO = usuario,")
    print("   CAJA via CAJAS_CAJEROS. Si falta un eslabon, ese usuario no puede operar.")
    try:
        cur.execute("""
            SELECT TRIM(v.NOMBRE), v.VENDEDOR_ID, c.CAJERO_ID,
                   (SELECT COUNT(*) FROM CAJAS_CAJEROS cc
                    WHERE cc.CAJERO_ID = c.CAJERO_ID) AS ACCESOS
            FROM VENDEDORES v
            LEFT JOIN CAJEROS c ON TRIM(c.USUARIO) = TRIM(v.NOMBRE) AND c.OCULTO = 'N'
            WHERE v.OCULTO = 'N'
            ORDER BY v.NOMBRE
        """)
        rows = cur.fetchall()
        if not rows:
            report("AVISO", "Vendedores", "no hay vendedores activos")
        for r in rows:
            nombre, vend_id, cajero_id, accesos = r[0], r[1], r[2], r[3]
            if cajero_id is None:
                report("AVISO", f"Vendedor '{nombre}'",
                       f"(id {vend_id}) SIN CAJERO por USUARIO -> el login fallara con 403")
            elif accesos is None or accesos == 0:
                report("AVISO", f"Vendedor '{nombre}'",
                       f"(id {vend_id}) -> cajero {cajero_id} SIN ACCESO a cajas en CAJAS_CAJEROS")
            else:
                report("OK", f"Vendedor '{nombre}'",
                       f"(id {vend_id}) -> cajero {cajero_id} con {accesos} acceso(s)")
    except Exception as e:
        report("AVISO", "Cadena login vendedor->cajero", f"no verificable: {e}")

    try:
        cur.execute("""
            SELECT TRIM(cj.NOMBRE), COUNT(*)
            FROM CAJAS_CAJEROS cc
            INNER JOIN CAJEROS cj ON cj.CAJERO_ID = cc.CAJERO_ID
            INNER JOIN CAJAS c ON c.CAJA_ID = cc.CAJA_ID
            WHERE cc.TIPO_ACCESO = 'O' AND c.OCULTO = 'N'
            GROUP BY cj.NOMBRE
            ORDER BY cj.NOMBRE
        """)
        rows = cur.fetchall()
        if not rows:
            report("AVISO", "Acceso OPERAR a cajas",
                   "ningun cajero tiene acceso OPERAR a una caja activa")
        else:
            report("OK", "Acceso OPERAR a cajas",
                   f"{len(rows)} cajero(s) pueden operar cajas activas")
    except Exception as e:
        report("AVISO", "Acceso OPERAR a cajas", f"no verificable: {e}")
    print()

    conn.close()

    # ------------------------------------------------------------------ VEREDICTO
    print("=" * 78)
    print(" RESUMEN FINAL")
    print("=" * 78)
    print(f"   OK    : {ok_count}")
    print(f"   AVISO : {warn_count}")
    print(f"   FALLO : {fail_count}")
    print()
    print(" INTERPRETACION PARA EL USUARIO:")
    print("   - [FALLO]  -> hay que atenderlo: reasignar ID en appsettings (Tipo A) o")
    print("                 dar de alta el dato/catalogo en la BD (Tipo B).")
    print("   - [AVISO]  -> no rompe el flujo basico; revisar y documentar.")
    print("   - Tablas AGENTES/RUTAS/RUTAS_DET ya NO se usan: ignorar sus avisos.")
    print()
    if fail_count == 0:
        if warn_count == 0:
            print(" VEREDICTO: APTA - La BD es 100% compatible con el sincronizador.")
        else:
            print(" VEREDICTO: APTA CON AVISOS - Funciona, pero revisa los avisos antes de produccion.")
    else:
        print(" VEREDICTO: NO APTA - La BD no cumple todos los requisitos del sincronizador.")
        print("            Corrige los [FALLO] (ver tabla de hallazgos del analisis) antes de migrar.")
    print("=" * 78)
    sys.exit(1 if fail_count > 0 else 0)

if __name__ == "__main__":
    main()
