# -*- coding: utf-8 -*-
"""
================================================================================
 AUDITORIA DE INSTALACION: sincronizador_rutx <-> BD Microsip (Firebird)
================================================================================
Variante compacta de auditar_compatibilidad.py pensada para el INSTALADOR
(wizard / .exe): mismas verificaciones, pero la salida se enfoca en lo que
una instalacion necesita saber:

  - Contadores: cuantos faltan por configurar (FALLO), avisos y OK.
  - Lista breve de lo que HAY QUE CONFIGURAR (solo los FALLO).
  - Verificacion de credenciales: intenta SYSDBA/masterkey; si falla, pide
    usuario y contrasena al usuario (y valida de nuevo).

100% SOLO LECTURA: solo ejecuta SELECTs y metadatos. Nada se modifica.

USO:
  python auditory_install.py "C:\\ruta\\CHOCOLATES.fdb"
  python auditory_install.py "C:\\ruta\\CHOCOLATES.fdb" --user SYSDBA --password masterkey
  python auditory_install.py "C:\\ruta\\CHOCOLATES.fdb" --auto   (no pregunta, usa defaults)

SALIDA (codigo de salida):
  0 = apta (sin FALLOs corregibles) | 1 = no apta (faltan requisitos)
  2 = no se pudo conectar / driver ausente
================================================================================
"""

import argparse
import getpass
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
# MISMAS VERIFICACIONES que auditar_compatibilidad.py (fuente unica de verdad:
# el sincronizador actual usa login nativo Firebird, sin AGENTES ni RUTAS).
# ==============================================================================

REQUIRED_TABLES = {
    "MONEDAS": ["MONEDA_ID"],
    "CONDICIONES_PAGO": ["COND_PAGO_ID"],
    "ALMACENES": ["ALMACEN_ID"],
    "CONCEPTOS_CC": ["CONCEPTO_CC_ID"],
    "VENDEDORES": ["VENDEDOR_ID", "NOMBRE"],
    "CAJEROS": ["CAJERO_ID", "NOMBRE", "USUARIO", "OPERAR_CAJAS", "ABRIR_CAJAS", "OCULTO"],
    "CAJAS": ["CAJA_ID", "NOMBRE", "ALMACEN_ID", "OCULTO", "FORMA_COBRO_PREDET_ID"],
    "CAJAS_CAJEROS": ["CAJA_ID", "CAJERO_ID", "TIPO_ACCESO"],
    "MOVTOS_CAJAS": ["MOVTO_CAJA_ID", "FECHA", "HORA", "TIPO_MOVTO", "CAJA_ID", "USUARIO_CREADOR"],
    "CLIENTES": ["CLIENTE_ID", "NOMBRE", "LIMITE_CREDITO", "COND_PAGO_ID", "VENDEDOR_ID", "ESTATUS"],
    "DIRS_CLIENTES": ["DIR_CLI_ID", "CLIENTE_ID", "CALLE", "COLONIA", "POBLACION", "CODIGO_POSTAL"],
    "ARTICULOS": ["ARTICULO_ID", "NOMBRE", "ESTATUS"],
    "CLAVES_ARTICULOS": ["ARTICULO_ID", "CLAVE_ARTICULO"],
    "PRECIOS_ARTICULOS": ["ARTICULO_ID", "PRECIO", "PRECIO_EMPRESA_ID", "PRECIO_ARTICULO_ID"],
    "IMPUESTOS": ["IMPUESTO_ID", "PCTJE_IMPUESTO"],
    "IMPUESTOS_ARTICULOS": ["ARTICULO_ID", "IMPUESTO_ID"],
    "SALDOS_IN": ["ARTICULO_ID", "ALMACEN_ID", "ENTRADAS_UNIDADES", "SALIDAS_UNIDADES", "ANO", "MES"],
    "FORMAS_COBRO": ["FORMA_COBRO_ID", "NOMBRE", "TIPO"],
    "SUCURSALES": ["SUCURSAL_ID", "NOMBRE", "CALLE", "NOMBRE_CALLE", "POBLACION", "CODIGO_POSTAL"],
    "RFCS_LCO": ["RFC_LCO_ID", "RFC", "NOMBRE_FISCAL", "DOMICILIO_FISCAL", "ESTATUS_VERIFICACION"],
    "FOLIOS_CAJAS": ["CAJA_ID", "TIPO_DOCTO", "SERIE", "CONSECUTIVO"],
    "DOCTOS_PV": ["DOCTO_PV_ID", "CAJA_ID", "TIPO_DOCTO", "SUCURSAL_ID", "FOLIO", "FECHA", "HORA",
                  "CAJERO_ID", "CLIENTE_ID", "ALMACEN_ID", "MONEDA_ID", "ESTATUS", "APLICADO",
                  "VENDEDOR_ID", "IMPORTE_NETO", "TOTAL_IMPUESTOS", "DESCRIPCION", "USUARIO_CREADOR"],
    "DOCTOS_PV_DET": ["DOCTO_PV_DET_ID", "DOCTO_PV_ID", "ARTICULO_ID", "UNIDADES",
                      "PRECIO_UNITARIO", "PRECIO_UNITARIO_IMPTO", "IMPUESTO_POR_UNIDAD",
                      "PRECIO_TOTAL_NETO", "POSICION"],
    "IMPUESTOS_DOCTOS_PV": ["DOCTO_PV_ID", "IMPUESTO_ID", "PCTJE_IMPUESTO", "IMPORTE_IMPUESTO"],
    "IMPUESTOS_DOCTOS_PV_DET": ["DOCTO_PV_DET_ID", "IMPUESTO_ID", "DOCTO_PV_ID", "PCTJE_IMPUESTO",
                                "IMPORTE_IMPUESTO", "TIPO_CALC"],
    "DOCTOS_PV_COBROS": ["DOCTO_PV_COBRO_ID", "DOCTO_PV_ID", "TIPO", "FORMA_COBRO_ID", "IMPORTE", "TIPO_CAMBIO"],
    "DOCTOS_PV_LIGAS": ["DOCTO_PV_LIGA_ID", "DOCTO_PV_FTE_ID", "DOCTO_PV_DEST_ID"],
    "DOCTOS_CC": ["DOCTO_CC_ID", "CONCEPTO_CC_ID", "CLIENTE_ID", "IMPORTE_COBRO", "FOLIO",
                  "FECHA", "CANCELADO", "APLICADO"],
    "SALDOS_CC": ["CLIENTE_ID", "CARGOS_CXC", "CREDITOS_CXC"],
    "VENCIMIENTOS_CARGOS_CC": ["DOCTO_CC_ID", "FECHA_VENCIMIENTO"],
    "DOCTOS_ENTRE_SIS": ["CLAVE_SIS_FTE", "DOCTO_FTE_ID", "CLAVE_SIS_DEST", "DOCTO_DEST_ID", "TIPO_DOCTO"],
    "MOVTOS_EFVO_CAJA": [],
}

TABLAS_RETIRADAS = ["AGENTES", "RUTAS", "RUTAS_DET"]  # informativas (ya no se usan)

REQUIRED_TRIGGERS = [
    "DOCTOS_PV_BEFINS",
    "DOCTOS_PV_DET_BEFINS",
    "DOCTOS_PV_COBROS_BEFINS",
    "DOCTOS_PV_COBROS_AFTINS_0",
    "DOCTOS_PV_AFTUPD_0",
    "DOCTOS_PV_BEFUPD_0",
    "DOCTOS_PV_LIGAS_BEFINS",
    "DOCTOS_CC_BEFINS",
]

REQUIRED_FK_CONSTRAINT = "CAJEROS_A_DOCTOS_PV"

# (tabla, columna_id, {id: descripcion}, nivel, tipo_check)
CRITICAL_IDS = [
    ("MONEDAS", "MONEDA_ID", {1: "Moneda base"}, "FALLO", "TODOS"),
    ("CONDICIONES_PAGO", "COND_PAGO_ID", {1: "Contado (appsettings)", 2422: "Contado (Development)"},
     "FALLO", "AL_MENOS_UNO"),
    ("IMPUESTOS", "IMPUESTO_ID", {622: "IVA 16%"}, "FALLO", "TODOS"),
    ("FORMAS_COBRO", "FORMA_COBRO_ID", {67: "EFECTIVO"}, "FALLO", "TODOS"),
    ("FORMAS_COBRO", "FORMA_COBRO_ID", {71: "CREDITO", 703: "CREDITO 15D", 2205: "CREDITO 30D"},
     "AVISO", "TODOS"),
    ("SUCURSALES", "SUCURSAL_ID", {4274: "Sucursal por defecto"}, "FALLO", "TODOS"),
    ("ALMACENES", "ALMACEN_ID", {19: "Almacen por defecto"}, "FALLO", "TODOS"),
    ("CAJEROS", "CAJERO_ID", {2419: "Cajero fallback por defecto"}, "AVISO", "TODOS"),
    ("CONCEPTOS_CC", "CONCEPTO_CC_ID", {11: "Concepto Cobro (abono CxC)"}, "FALLO", "TODOS"),
]

MIN_DATA_CHECKS = [
    ("Al menos 1 caja activa (OCULTO='N')",
     "SELECT COUNT(*) FROM CAJAS WHERE OCULTO = 'N'", 1, "CRITICO"),
    ("Al menos 1 vendedor",
     "SELECT COUNT(*) FROM VENDEDORES", 1, "CRITICO"),
    ("Al menos 1 cajero con USUARIO",
     "SELECT COUNT(*) FROM CAJEROS WHERE USUARIO IS NOT NULL AND TRIM(USUARIO) <> ''", 1, "CRITICO"),
    ("Al menos 1 acceso de cajero a caja (CAJAS_CAJEROS)",
     "SELECT COUNT(*) FROM CAJAS_CAJEROS WHERE TIPO_ACCESO IN ('A','O')", 1, "CRITICO"),
    ("Al menos 1 cliente activo (ESTATUS='A')",
     "SELECT COUNT(*) FROM CLIENTES WHERE ESTATUS = 'A'", 1, "CRITICO"),
    ("Al menos 1 cliente con direccion en DIRS_CLIENTES",
     "SELECT COUNT(DISTINCT CLIENTE_ID) FROM DIRS_CLIENTES", 1, "CRITICO"),
    ("Al menos 1 articulo activo (ESTATUS='A')",
     "SELECT COUNT(*) FROM ARTICULOS WHERE ESTATUS = 'A'", 1, "CRITICO"),
    ("Al menos 1 articulo con precio (PRECIOS_ARTICULOS)",
     "SELECT COUNT(DISTINCT ARTICULO_ID) FROM PRECIOS_ARTICULOS", 1, "CRITICO"),
    ("Al menos 1 forma de cobro en catalogo",
     "SELECT COUNT(*) FROM FORMAS_COBRO", 1, "CRITICO"),
    ("Al menos 1 emisor fiscal activo (RFCS_LCO)",
     "SELECT COUNT(*) FROM RFCS_LCO WHERE ESTATUS_VERIFICACION = 'A'", 1, "AVISO"),
    ("Al menos 1 impuesto en catalogo",
     "SELECT COUNT(*) FROM IMPUESTOS", 1, "AVISO"),
    ("Al menos 1 articulo con existencias > 0 (SALDOS_IN)",
     "SELECT COUNT(DISTINCT ARTICULO_ID) FROM SALDOS_IN WHERE (ENTRADAS_UNIDADES - SALIDAS_UNIDADES) > 0", 1, "AVISO"),
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


def resolver_credenciales(args):
    """Intenta SYSDBA/masterkey; si falla y no viene --auto, pide usuario/clave."""
    intentos = [("SYSDBA", "masterkey")]
    if args.user != "SYSDBA" or args.password != "masterkey":
        intentos.insert(0, (args.user, args.password))

    for usuario, clave in intentos:
        try:
            conn = fdb.connect(dsn=args.db_path, user=usuario, password=clave, charset='UTF8')
            conn.close()
            return usuario, clave
        except Exception:
            continue

    if args.auto:
        return args.user, args.password

    # Pedir credenciales al usuario (hasta 3 intentos)
    for _ in range(3):
        print()
        print(" No se pudo entrar con las credenciales por defecto.")
        print(" Configura el usuario y contrasena de Firebird:")
        usuario = input("   Usuario [SYSDBA]: ").strip() or "SYSDBA"
        clave = getpass.getpass("   Contrasena: ")
        try:
            conn = fdb.connect(dsn=args.db_path, user=usuario, password=clave, charset='UTF8')
            conn.close()
            return usuario, clave
        except Exception as e:
            print(f"   [X] Fallo: {str(e).strip()[:160]}")
    return None, None


def main():
    parser = argparse.ArgumentParser(
        description="Auditoria compacta de instalacion: BD Microsip <-> sincronizador Rutx.")
    parser.add_argument("db_path", help="Ruta del archivo .fdb (ej: C:\\Microsip datos\\CHOCOLATES.fdb)")
    parser.add_argument("--user", default="SYSDBA", help="Usuario Firebird (default: SYSDBA)")
    parser.add_argument("--password", default="masterkey", help="Password (default: masterkey)")
    parser.add_argument("--auto", action="store_true",
                        help="No preguntar credenciales: usa las dadas y reporta el fallo")
    args = parser.parse_args()

    print("=" * 66)
    print(" AUDITORIA DE INSTALACION · sincronizador <-> BD Microsip")
    print("=" * 66)
    print(f" BD : {args.db_path}")

    # ---- 0. Archivo existe y firma Firebird ----
    if not __import__('os').path.exists(args.db_path):
        print(" [FALLO] El archivo de BD NO existe en la ruta indicada.")
        print("\n VEREDICTO: NO APTA (BD inaccesible)")
        sys.exit(1)

    # ---- Credenciales ----
    usuario, clave = resolver_credenciales(args)
    if not usuario:
        print(" [FALLO] No se pudo autenticar contra la BD con las credenciales probadas.")
        print("\n VEREDICTO: NO APTA (credenciales incorrectas)")
        sys.exit(1)

    # ---- Conexion SOLO LECTURA ----
    try:
        try:
            conn = fdb.connect(dsn=args.db_path, user=usuario, password=clave, charset='UTF8',
                               isolation_level=fdb.ISOLATION_LEVEL_READ_COMMITED_RO)
        except (TypeError, AttributeError, KeyError, ValueError):
            conn = fdb.connect(dsn=args.db_path, user=usuario, password=clave, charset='UTF8')
    except Exception as e:
        print(f" [FALLO] No se pudo conectar a la BD: {str(e).strip()[:180]}")
        print("\n VEREDICTO: NO APTA (BD inaccesible)")
        sys.exit(1)

    cur = conn.cursor()
    fallos = []
    avisos = []
    ok_count = 0

    def report(estado, seccion, detalle):
        nonlocal ok_count
        if estado == "OK":
            ok_count += 1
        elif estado == "AVISO":
            avisos.append(f"{seccion}: {detalle}")
        else:
            fallos.append(f"{seccion}: {detalle}")

    # ---- 1. Tablas y columnas ----
    tables = get_tables(cur)
    column_cache = {}
    for tabla, columnas in REQUIRED_TABLES.items():
        t = tabla.upper()
        if t not in tables:
            report("FALLO", f"Tabla {tabla}", "NO EXISTE")
            continue
        column_cache[t] = get_columns(cur, t)
        if not columnas:
            report("OK", f"Tabla {tabla}", "existe")
            continue
        faltantes = [c for c in columnas if c.upper() not in column_cache[t]]
        if faltantes:
            report("FALLO", f"Tabla {tabla}", f"FALTAN columnas: {', '.join(faltantes)}")
        else:
            report("OK", f"Tabla {tabla}", "ok")

    # ---- 2. Triggers ----
    triggers = get_triggers(cur)
    for trig in REQUIRED_TRIGGERS:
        if trig.upper() in triggers:
            report("OK", f"Trigger {trig}", "existe")
        else:
            report("FALLO", f"Trigger {trig}", "NO EXISTE")

    # ---- 3. FK ----
    fks = get_fk_constraints(cur)
    if REQUIRED_FK_CONSTRAINT.upper() in fks:
        report("OK", f"FK {REQUIRED_FK_CONSTRAINT}", "existe")
    else:
        report("AVISO", f"FK {REQUIRED_FK_CONSTRAINT}", "NO EXISTE -> usara fallback DefaultCajeroId")

    # ---- 4. IDs criticos ----
    for tabla, columna_id, ids, nivel_falta, tipo_check in CRITICAL_IDS:
        t = tabla.upper()
        if t not in tables:
            report("AVISO", f"Catalogo {tabla}", "tabla no existe (se omite)")
            continue
        try:
            cur.execute(f"SELECT {columna_id} FROM {tabla}")
            existentes = {r[0] for r in cur.fetchall()}
        except Exception as e:
            report("AVISO", f"Catalogo {tabla}", f"no verificable: {str(e).strip()[:80]}")
            continue

        if tipo_check == "AL_MENOS_UNO":
            encontrados = [idv for idv in ids if idv in existentes]
            if encontrados:
                for id_val, desc in ids.items():
                    if id_val in existentes:
                        report("OK", f"{tabla}.{columna_id}={id_val}", desc)
                    else:
                        report("AVISO", f"{tabla}.{columna_id}={id_val}",
                               f"{desc} -> no existe, pero hay condicion valida (id {encontrados[0]})")
                if tabla == "CONDICIONES_PAGO" and 1 not in existentes:
                    report("AVISO", "CONDICIONES_PAGO.entorno",
                           "falta id 1 (Production usa DefaultCondPagoId=1): correr en Development o agregar el id")
            else:
                for id_val, desc in ids.items():
                    report("FALLO", f"{tabla}.{columna_id}={id_val}", f"{desc} -> NINGUNA condicion valida")
            continue

        for id_val, desc in ids.items():
            if id_val in existentes:
                report("OK", f"{tabla}.{columna_id}={id_val}", desc)
            else:
                report(nivel_falta, f"{tabla}.{columna_id}={id_val}", f"{desc} -> NO ENCONTRADO")

    try:
        cur.execute("SELECT COUNT(*) FROM RDB$GENERATORS WHERE RDB$SYSTEM_FLAG = 0 "
                    "AND TRIM(RDB$GENERATOR_NAME) = 'ID_DOCTOS'")
        if cur.fetchone()[0] > 0:
            report("OK", "Generador ID_DOCTOS", "existe")
        else:
            report("FALLO", "Generador ID_DOCTOS", "NO EXISTE -> los triggers BEFINS fallaran")
    except Exception as e:
        report("AVISO", "Generador ID_DOCTOS", f"no verificable: {str(e).strip()[:80]}")

    # ---- 5. Datos minimos ----
    for desc, sql, minimo, nivel in MIN_DATA_CHECKS:
        try:
            cur.execute(sql)
            count = cur.fetchone()[0]
        except Exception as e:
            report("FALLO" if nivel == "CRITICO" else "AVISO", desc, f"error: {str(e).strip()[:80]}")
            continue
        if count >= minimo:
            report("OK", desc, f"{count} registro(s)")
        else:
            report("FALLO" if nivel == "CRITICO" else "AVISO", desc, f"solo {count} (minimo {minimo})")

    # ---- 6. Coherencia (compacta) ----
    try:
        cur.execute("SELECT COUNT(*) FROM CAJEROS WHERE USUARIO IS NOT NULL AND TRIM(USUARIO) <> ''")
        con_usu = cur.fetchone()[0]
        cur.execute("SELECT COUNT(*) FROM CAJEROS c WHERE c.USUARIO IS NOT NULL AND TRIM(c.USUARIO) <> '' "
                    "AND NOT EXISTS (SELECT 1 FROM CAJAS_CAJEROS cc "
                    "WHERE cc.CAJERO_ID = c.CAJERO_ID AND cc.TIPO_ACCESO = 'O')")
        sin_acceso = cur.fetchone()[0]
        if con_usu > 0:
            report("OK", "Cajeros con USUARIO", f"{con_usu}")
        else:
            report("FALLO", "Cajeros con USUARIO", "no hay cajeros con USUARIO (login nativo)")
        if sin_acceso > 0:
            report("AVISO", "Cajeros sin acceso OPERAR", f"{sin_acceso} cajero(s) sin acceso 'O' a caja")
        else:
            report("OK", "Acceso OPERAR a cajas", "todos los cajeros con USUARIO tienen acceso")
    except Exception as e:
        report("AVISO", "Coherencia", f"no verificable: {str(e).strip()[:80]}")

    conn.close()

    # ---- Salida COMPACTA (contadores) ----
    print("-" * 66)
    print()
    print(f"  🔴 POR CONFIGURAR (FALLO): {len(fallos)}")
    print(f"  🟡 AVISOS              : {len(avisos)}")
    print(f"  🟢 OK                  : {ok_count}")
    print()
    if fallos:
        print("  Lo que falta configurar / agregar en la BD:")
        for f in fallos[:15]:
            print(f"    - {f}")
        if len(fallos) > 15:
            print(f"    ... y {len(fallos) - 15} mas (revisa el reporte completo).")
    if avisos:
        print("  Avisos:")
        for a in avisos[:8]:
            print(f"    · {a}")
    print()
    print("=" * 66)
    if fallos:
        print(" VEREDICTO: NO APTA - faltan requisitos (ver lista arriba).")
        sys.exit(1)
    if avisos:
        print(" VEREDICTO: APTA CON AVISOS - funciona, revisa los avisos.")
        sys.exit(0)
    print(" VEREDICTO: APTA - la BD esta lista.")
    print("=" * 66)
    sys.exit(0)


if __name__ == "__main__":
    main()
