// ============================================================================
// ARCHIVO: CreditoService.cs
// PROPOSITO: Servicio para consultar saldos de credito pendientes de los
//            clientes, basado en SALDOS_CC de Microsip.
//
// TABLAS CONSULTADAS:
//   CLIENTES            -> Datos del cliente y limite de credito
//   SALDOS_CC           -> Saldos de CxC por cliente/mes/ano
//   DOCTOS_CC           -> Documentos de CxC (cargos y abonos)
//   DOCTOS_ENTRE_SIS    -> Vinculo entre PV y CC
//   DOCTOS_PV           -> Documentos de venta originales
//
// LOGICA DEL SALDO PENDIENTE:
//   SALDOS_CC almacena saldos mensuales:
//     CARGOS_CXC   = Suma de cargos (ventas a credito) en el mes
//     CREDITOS_CXC = Suma de abonos (pagos recibidos) en el mes
//
//   El saldo pendiente TOTAL del cliente es la suma historica:
//     Saldo = SUM(CARGOS_CXC) - SUM(CREDITOS_CXC)
//     para todos los meses con saldo > 0
//
//   Si no hay registros en SALDOS_CC, se consulta DOCTOS_CC directamente
//   como fallback.
//
// DIAS DE ATRASO:
//   Se calcula comparando la fecha actual contra VENCIMIENTOS_CARGOS_CC
//   para determinar si hay pagos vencidos. Si no hay vencimientos,
//   se usa la fecha del documento mas antiguo como referencia.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Servicio para consultar creditos pendientes de clientes.
/// Obtiene saldos desde SALDOS_CC y datos del cliente desde CLIENTES.
/// </summary>
public class CreditoService : ICreditoService
{
    private readonly IConfiguration _configuration;
    private readonly string _connectionString;

    public CreditoService(IConfiguration configuration)
    {
        _configuration = configuration;
        _connectionString = configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException(
                "No se encontro la cadena de conexion 'FirebirdConnection' en appsettings.");
    }

    /// <summary>
    /// Obtiene la lista de creditos pendientes de todos los clientes.
    /// Excluye al cliente eventual (ID=2424) ya que no tiene CxC.
    ///
    /// FLUJO DE CONSULTA:
    ///   1. Calcula saldo pendiente desde SALDOS_CC (suma historica)
    ///   2. Cruza con CLIENTES para obtener nombre y limite de credito
    ///   3. Calcula documentos pendientes desde DOCTOS_PV <-> DOCTOS_CC
    ///   4. Calcula dias de atraso desde VENCIMIENTOS_CARGOS_CC
    /// </summary>
    public async Task<PedidosCreditoResponseDto> SelectPedidosCreditoAsync(
        int? clienteId = null, int vendedorId = 0)
    {
        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();

        // ================================================================
        // CONSULTA PRINCIPAL: Saldos pendientes por cliente
        // ================================================================
        // Resume SALDOS_CC para obtener cargos y abonos acumulados
        // por cliente, y cruza con CLIENTES para datos del cliente.
        //
        // NOTA: El LIMITE_CREDITO puede ser 0 si no aplica credito.
        // En ese caso, el cliente no tiene credito (solo contado).
        //
        // Se excluye:
        //   - Cliente eventual (ID=2424) no tiene CxC
        //   - Clientes sin saldo pendiente (no deben aparecer)
        // ================================================================
        string sql = @"
            SELECT
                c.CLIENTE_ID,
                c.NOMBRE,
                COALESCE(c.LIMITE_CREDITO, 0) AS LIMITE_CREDITO,
                COALESCE(SUM(sc.CARGOS_CXC), 0) - COALESCE(SUM(sc.CREDITOS_CXC), 0) AS SALDO_PENDIENTE,
                COALESCE(docs.docs_pendientes, 0) AS DOCS_PENDIENTES,
                COALESCE(venc.fecha_min, CURRENT_DATE) AS FECHA_VENCIMIENTO_MAS_ANTIGUO
            FROM CLIENTES c
            LEFT JOIN SALDOS_CC sc ON sc.CLIENTE_ID = c.CLIENTE_ID
            LEFT JOIN (
                SELECT dc.CLIENTE_ID, COUNT(DISTINCT des.DOCTO_FTE_ID) AS docs_pendientes
                FROM DOCTOS_ENTRE_SIS des
                JOIN DOCTOS_CC dc ON dc.DOCTO_CC_ID = des.DOCTO_DEST_ID
                WHERE des.CLAVE_SIS_FTE = 'PV'
                  AND des.CLAVE_SIS_DEST = 'CC'
                  AND des.TIPO_DOCTO = 'C'
                  AND dc.APLICADO = 'S'
                  AND dc.CANCELADO = 'N'
                  AND dc.ESTATUS = 'N'
                GROUP BY dc.CLIENTE_ID
            ) docs ON docs.CLIENTE_ID = c.CLIENTE_ID
            LEFT JOIN (
                SELECT dc.CLIENTE_ID, MIN(v.FECHA_VENCIMIENTO) AS fecha_min
                FROM VENCIMIENTOS_CARGOS_CC v
                JOIN DOCTOS_CC dc ON dc.DOCTO_CC_ID = v.DOCTO_CC_ID
                WHERE dc.CANCELADO = 'N'
                  AND v.FECHA_VENCIMIENTO < CURRENT_DATE
                GROUP BY dc.CLIENTE_ID
            ) venc ON venc.CLIENTE_ID = c.CLIENTE_ID
            WHERE c.CLIENTE_ID != 2424
              AND c.ESTATUS = 'A'
              AND (sc.CARGOS_CXC > 0 OR sc.CREDITOS_CXC > 0)";

        // Filtros opcionales (independientes: ambos pueden aplicarse simultaneamente)
        if (clienteId.HasValue && clienteId.Value > 0)
            sql += " AND c.CLIENTE_ID = @ClienteId";
        if (vendedorId > 0)
            sql += " AND c.VENDEDOR_ID = @VendedorId";

        sql += @"
            GROUP BY c.CLIENTE_ID, c.NOMBRE, c.LIMITE_CREDITO, docs.docs_pendientes, venc.fecha_min
            HAVING COALESCE(SUM(sc.CARGOS_CXC), 0) - COALESCE(SUM(sc.CREDITOS_CXC), 0) > 0
            ORDER BY c.NOMBRE";

        var resultados = await connection.QueryAsync(sql,
            new { ClienteId = clienteId, VendedorId = vendedorId });

        var creditos = new List<CreditoClienteDto>();

        foreach (var row in resultados)
        {
            int clienteIdVal = Convert.ToInt32(row.CLIENTE_ID);
            string nombre = row.NOMBRE?.ToString()?.Trim() ?? "";
            decimal limiteCredito = Convert.ToDecimal(row.LIMITE_CREDITO);
            decimal saldoPendiente = Convert.ToDecimal(row.SALDO_PENDIENTE);
            int docsPendientes = Convert.ToInt32(row.DOCS_PENDIENTES);

            // Calcular porcentaje de credito usado
            decimal porcentajeUsado = 0;
            if (limiteCredito > 0)
                porcentajeUsado = Math.Round((saldoPendiente / limiteCredito) * 100, 1);

            // Calcular dias de atraso
            int diasAtraso = 0;
            if (row.FECHA_VENCIMIENTO_MAS_ANTIGUO != null)
            {
                DateTime fechaVenc = Convert.ToDateTime(row.FECHA_VENCIMIENTO_MAS_ANTIGUO);
                if (fechaVenc < DateTime.Today)
                {
                    diasAtraso = (DateTime.Today - fechaVenc).Days;
                }
            }

            creditos.Add(new CreditoClienteDto
            {
                ClienteId = clienteIdVal,
                Nombre = nombre,
                LimiteCredito = limiteCredito,
                SaldoPendiente = saldoPendiente,
                PorcentajeUsado = porcentajeUsado,
                DocumentosPendientes = docsPendientes,
                DiasAtraso = diasAtraso
            });
        }

        return new PedidosCreditoResponseDto
        {
            Message = $"Se encontraron {creditos.Count} clientes con credito pendiente",
            TotalClientes = creditos.Count,
            Creditos = creditos
        };
    }

    /// <summary>
    /// Obtiene los documentos de venta 'V' pendientes de pago para un cliente.
    /// Cada documento incluye su saldo pendiente calculado como:
    ///   cargo_original_en_CxC - SUM(abonos_aplicados)
    ///
    /// RELACION CON TRIGGERS Y SPs:
    ///   DOCTOS_PV_AFTUPD_0 (trigger) -> APLICA_DOCTO_PV -> APLICA_VTA_PV
    ///     -> GENERA_DOCTO_CC_PV crea:
    ///       - DOCTOS_CC con NATURALEZA='C' (cargo)
    ///       - DOCTOS_ENTRE_SIS (PV->CC con TIPO_DOCTO='C')
    ///       - SALDOS_CC (incrementa CARGOS_CXC)
    ///       - VENCIMIENTOS_CARGOS_CC
    ///
    ///   Los abonos (NATURALEZA='R') son creados manualmente por CobranzaService
    ///   cuando se registra un pago. Se vinculan via DOCTOS_ENTRE_SIS con
    ///   TIPO_DOCTO='P'.
    ///
    /// FLUJO DE LA CONSULTA:
    ///   1. Busca DOCTOS_PV tipo 'V', aplicados, no cancelados del cliente
    ///   2. Cruza con DOCTOS_ENTRE_SIS (TIPO_DOCTO='C') para hallar el DOCTOS_CC cargo
    ///   3. LEFT JOIN con abonos (DOCTOS_CC NATURALEZA='R') vinculados via
    ///      DOCTOS_ENTRE_SIS (TIPO_DOCTO='P')
    ///   4. Calcula saldo pendiente = cargo - SUM(abonos)
    ///   5. Filtra solo documentos con saldo > 0
    /// </summary>
    public async Task<DocumentosPendientesResponseDto> SelectDocumentosClienteAsync(int clienteId)
    {
        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();

        // =========================================================================
        // CONSULTA: Documentos de venta pendientes por cliente
        // =========================================================================
        // Busca DOCTOS_PV tipo 'V' (ventas a credito) que esten aplicados.
        // Cada venta a credito tiene un DOCTOS_CC cargo creado automaticamente
        // por GENERA_DOCTO_CC_PV (ejecutado via trigger DOCTOS_PV_AFTUPD_0).
        //
        // El vinculo PV->CC se almacena en DOCTOS_ENTRE_SIS con:
        //   CLAVE_SIS_FTE = 'PV', DOCTO_FTE_ID = DOCTOS_PV.DOCTO_PV_ID
        //   CLAVE_SIS_DEST = 'CC', DOCTO_DEST_ID = DOCTOS_CC.DOCTO_CC_ID
        //   TIPO_DOCTO = 'C' (Cargo, creado por GENERA_DOCTO_CC_PV)
        //
        // Los abonos (pagos) se vinculan al cargo via DOCTOS_ENTRE_SIS con:
        //   CLAVE_SIS_DEST = 'CC', DOCTO_DEST_ID = DOCTOS_CC.DOCTO_CC_ID
        //   TIPO_DOCTO = 'P' (Pago, creado manualmente por CobranzaService)
        // =========================================================================
        string sql = @"
            SELECT
                dpv.DOCTO_PV_ID,
                dpv.FOLIO,
                dpv.FECHA,
                dpv.IMPORTE_NETO AS IMPORTE_ORIGINAL,
                dc_cargo.DOCTO_CC_ID,
                dc_cargo.IMPORTE_COBRO AS TOTAL_CARGO,
                COALESCE(SUM(abono.IMPORTE_COBRO), 0) AS TOTAL_ABONADO,
                (dc_cargo.IMPORTE_COBRO - COALESCE(SUM(abono.IMPORTE_COBRO), 0)) AS SALDO_PENDIENTE
            FROM DOCTOS_PV dpv
            -- Vinculo PV -> CxC: el cargo generado por GENERA_DOCTO_CC_PV
            -- via trigger DOCTOS_PV_AFTUPD_0 -> APLICA_DOCTO_PV -> APLICA_VTA_PV
            INNER JOIN DOCTOS_ENTRE_SIS des_cargo
                ON des_cargo.DOCTO_FTE_ID = dpv.DOCTO_PV_ID
                AND des_cargo.CLAVE_SIS_FTE = 'PV'
                AND des_cargo.CLAVE_SIS_DEST = 'CC'
                AND des_cargo.TIPO_DOCTO = 'C'
            -- DOCTOS_CC del cargo (creado por GENERA_DOCTO_CC_PV)
            INNER JOIN DOCTOS_CC dc_cargo
                ON dc_cargo.DOCTO_CC_ID = des_cargo.DOCTO_DEST_ID
                AND dc_cargo.CANCELADO = 'N'
                AND dc_cargo.NATURALEZA_CONCEPTO = 'C'
            -- Abonos aplicados a este cargo (creados manualmente por CobranzaService)
            -- Se vinculan via DOCTOS_ENTRE_SIS con TIPO_DOCTO='P'
            LEFT JOIN DOCTOS_ENTRE_SIS des_abono
                ON des_abono.CLAVE_SIS_DEST = 'CC'
                AND des_abono.DOCTO_DEST_ID = dc_cargo.DOCTO_CC_ID
                AND des_abono.TIPO_DOCTO = 'P'
            LEFT JOIN DOCTOS_CC abono
                ON abono.DOCTO_CC_ID = des_abono.DOCTO_DEST_ID
                AND abono.NATURALEZA_CONCEPTO = 'R'
                AND abono.CANCELADO = 'N'
            WHERE dpv.CLIENTE_ID = @ClienteId
              AND dpv.TIPO_DOCTO = 'V'
              AND dpv.APLICADO = 'S'
              AND dpv.ESTATUS = 'N'
            GROUP BY
                dpv.DOCTO_PV_ID,
                dpv.FOLIO,
                dpv.FECHA,
                dpv.IMPORTE_NETO,
                dc_cargo.DOCTO_CC_ID,
                dc_cargo.IMPORTE_COBRO
            HAVING (dc_cargo.IMPORTE_COBRO - COALESCE(SUM(abono.IMPORTE_COBRO), 0)) > 0
            ORDER BY dpv.FECHA ASC";

        var documentos = await connection.QueryAsync<DocumentoPendienteDto>(sql,
            new { ClienteId = clienteId });

        // =========================================================================
        // CONSULTA AUXILIAR: Nombre del cliente
        // =========================================================================
        string sqlCliente = @"
            SELECT NOMBRE FROM CLIENTES WHERE CLIENTE_ID = @ClienteId";

        string? clienteNombre = await connection.QueryFirstOrDefaultAsync<string>(
            sqlCliente, new { ClienteId = clienteId });

        return new DocumentosPendientesResponseDto
        {
            ClienteId = clienteId,
            ClienteNombre = (clienteNombre ?? "").Trim(),
            Documentos = documentos.ToList()
        };
    }
}

// ============================================================================
// INTERFAZ: ICreditoService
// ============================================================================
public interface ICreditoService
{
    /// <summary>
    /// Obtiene la lista de creditos pendientes por cliente.
    /// </summary>
    /// <param name="clienteId">Opcional: filtrar por cliente especifico</param>
    /// <param name="vendedorId">Opcional: filtrar por vendedor</param>
    Task<PedidosCreditoResponseDto> SelectPedidosCreditoAsync(
        int? clienteId = null, int vendedorId = 0);

    /// <summary>
    /// Obtiene los documentos de venta 'V' pendientes de pago para un cliente.
    /// Cada documento incluye su saldo pendiente calculado contra DOCTOS_CC.
    /// </summary>
    /// <param name="clienteId">ID del cliente a consultar</param>
    Task<DocumentosPendientesResponseDto> SelectDocumentosClienteAsync(int clienteId);
}
