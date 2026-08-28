-- ============================================================================
-- FASE B / PASO 1: usuario dedicado RUTX_SYNC + grants minimos
-- ----------------------------------------------------------------------------
-- STOP: NO ejecutar sin confirmacion EXPLICITA del responsable.
-- Ejecutar SOLO como SYSDBA y SOLO contra el alias aislado de la copia
-- consistente (gbak -> restore), NUNCA contra la BD real de Microsip.
-- SYSDBA y sus credenciales actuales NO se tocan. Ver Docs/FIREBIRD_GRANTS_RUTX_SYNC.md
--
-- Uso (cuando haya aprobacion):
--   isql -user SYSDBA "rutx_e2e_copia" -i Deploy\Firebird\FaseB_01_crear_usuario_y_grants.sql
--   (la password se pedira por ISC_PASSWORD o prompt; nunca en este archivo)
-- ============================================================================

CREATE USER RUTX_SYNC PASSWORD '<aleatoria>=20+'; -- SIN admin role: no lo requiere

-- Lectura de catalogos y consultas
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
GRANT SELECT ON RFCS_LCO               TO RUTX_SYNC;
GRANT SELECT ON CLAVES_ARTICULOS       TO RUTX_SYNC;

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

-- Integracion / auditoria cruzada
GRANT SELECT, INSERT ON DOCTOS_ENTRE_SIS TO RUTX_SYNC;

-- Clientes (alta desde la app; SELECT ya otorgado arriba)
GRANT INSERT, UPDATE ON CLIENTES      TO RUTX_SYNC;
GRANT INSERT         ON DIRS_CLIENTES TO RUTX_SYNC;

-- NOTA DE ESCALADO: si algun trigger/generador rechaza una escritura
-- legitima durante la matriz, DETENERSE y reportar el permiso exacto
-- faltante. No otorgar privilegios amplios automaticamente.
