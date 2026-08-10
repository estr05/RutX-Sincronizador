// ============================================================================
// ARCHIVO: FirebirdAuthService.cs
// PROPOSITO: Autenticacion nativa contra Microsip (Firebird) sin AGENTES.
//
// El login de RUTX usa las credenciales REALES del usuario de Firebird:
//   1. Abrir una conexion de prueba con (usuario, password) proporcionados.
//      - Si Firebird rechaza -> SQLSTATE 28000 ("Your user name and password
//        are not defined") -> credenciales incorrectas (401).
//      - No se escribe nada en la base: solo se validan credenciales.
//   2. Resolver la identidad de ruta del usuario:
//      - Vendedor : VENDEDORES.NOMBRE = usuario (convencion RUTX)
//      - Cajero   : CAJEROS.USUARIO = usuario
//      - Caja     : CAJAS_CAJEROS ('A' -> 'O'); si varias, la que tenga
//                   existencias > 0 en SALDOS_IN; si no hay filas, se usa
//                   evidencia (MOVTOS_CAJAS.USUARIO_CREADOR, luego
//                   DOCTOS_PV.USUARIO_CREADOR); si nada -> RutaAuthException
//      - Almacen  : CAJAS.ALMACEN_ID de la caja resuelta
// ============================================================================

using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Excepcion de dominio: el usuario autentico pero no puede operar
/// (falta vendedor, cajero o caja). Se responde 403 con el mensaje.
/// </summary>
public class RutaAuthException : Exception
{
    public RutaAuthException(string message) : base(message) { }
}

public class FirebirdAuthService : IFirebirdAuthService
{
    private readonly IConfiguration _configuration;
    private readonly string _connectionString;
    private readonly ILogger<FirebirdAuthService> _logger;

    public FirebirdAuthService(IConfiguration configuration, ILogger<FirebirdAuthService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionString = configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException(
                "No se encontro la cadena de conexion 'FirebirdConnection' en appsettings.");
    }

    /// <summary>
    /// Autentica al usuario contra Firebird y resuelve su identidad de ruta.
    /// Retorna null si las credenciales son incorrectas (401).
    /// Lanza RutaAuthException si el usuario no tiene identidad de ruta (403).
    /// </summary>
    public async Task<UsuarioSesion?> AutenticarAsync(string usuario, string password)
    {
        // 1) Validar credenciales Firebird reales (conexion de prueba).
        if (!await ValidarCredencialesAsync(usuario, password))
            return null;

        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();

        // 2) Vendedor por NOMBRE = usuario (convencion RUTX).
        var vendedor = await connection.QueryFirstOrDefaultAsync(@"
            SELECT FIRST 1 VENDEDOR_ID, NOMBRE
            FROM VENDEDORES
            WHERE TRIM(NOMBRE) = @Usuario",
            new { Usuario = usuario });

        int vendedorId = vendedor?.VENDEDOR_ID != null ? Convert.ToInt32(vendedor.VENDEDOR_ID) : 0;
        string vendedorNombre = vendedor?.NOMBRE?.ToString()?.Trim() ?? usuario;

        if (vendedorId <= 0)
        {
            _logger.LogWarning("Login sin vendedor asociado para usuario '{Usuario}'", usuario);
            throw new RutaAuthException(
                "El usuario no tiene un vendedor asociado. Contacta a soporte para configurar tu cuenta.");
        }

        // 3) Cajero por USUARIO.
        int cajeroId = await connection.QueryFirstOrDefaultAsync<int>(
            "SELECT FIRST 1 CAJERO_ID FROM CAJEROS WHERE TRIM(USUARIO) = @Usuario AND OCULTO = 'N'",
            new { Usuario = usuario });

        // 4) Resolver caja (prioridad definida por el equipo).
        int cajaId = await ResolverCajaAsync(connection, usuario, cajeroId);

        // 5) Almacen desde la caja.
        int almacenId = await connection.QueryFirstOrDefaultAsync<int>(
            "SELECT FIRST 1 ALMACEN_ID FROM CAJAS WHERE CAJA_ID = @CajaId",
            new { CajaId = cajaId });

        int sucursalId = _configuration.GetValue<int?>("MicrosipSettings:DefaultSucursalId") ?? 4274;

        _logger.LogInformation(
            "Login OK usuario='{Usuario}' vendedor={VendedorId} cajero={CajeroId} caja={CajaId} almacen={AlmacenId}",
            usuario, vendedorId, cajeroId, cajaId, almacenId);

        return new UsuarioSesion
        {
            VendedorId = vendedorId,
            VendedorNombre = vendedorNombre,
            CajeroId = cajeroId,
            CajaId = cajaId,
            AlmacenId = almacenId,
            SucursalId = sucursalId,
            Usuario = usuario
        };
    }

    /// <summary>
    /// Abre una conexion Firebird con las credenciales del usuario.
    /// Retorna true si son validas; false si Firebird las rechaza (401).
    /// Cualquier otro error de conexion (BD caida, red, etc.) se propaga
    /// para que el controlador responda 500.
    /// </summary>
    private async Task<bool> ValidarCredencialesAsync(string usuario, string password)
    {
        var cadena = ConstruirCadenaConCredenciales(usuario, password);
        using var connection = new FbConnection(cadena);
        try
        {
            await connection.OpenAsync();
            return true;
        }
        catch (FbException ex)
        {
            if (EsErrorDeCredenciales(ex))
                return false;
            throw;
        }
    }

    private static bool EsErrorDeCredenciales(FbException ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("28000", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Your user name and password are not defined", StringComparison.OrdinalIgnoreCase);
    }

    private string ConstruirCadenaConCredenciales(string usuario, string password)
    {
        var cs = Regex.Replace(_connectionString, @"(?i)\b(?:User ID|User)\s*=\s*[^;]*", $"User={usuario}");
        cs = Regex.Replace(cs, @"(?i)\bPassword\s*=\s*[^;]*", $"Password={password}");
        return cs;
    }

    /// <summary>
    /// Resuelve la caja del usuario con la prioridad acordada:
    ///   1) CAJAS_CAJEROS del cajero (TIPO_ACCESO 'A' primero, luego 'O').
    ///      Si hay varias cajas, se elige la que tenga existencias > 0 en
    ///      SALDOS_IN del periodo vigente.
    ///   2) Evidencia: MOVTOS_CAJAS.USUARIO_CREADOR = usuario (mas reciente).
    ///   3) Evidencia: DOCTOS_PV.USUARIO_CREADOR = usuario (mas reciente).
    ///   4) Nada -> RutaAuthException (403).
    /// </summary>
    private async Task<int> ResolverCajaAsync(FbConnection connection, string usuario, int cajeroId)
    {
        // 1) Acceso configurado en CAJAS_CAJEROS.
        var cajasConfiguradas = new List<dynamic>();
        if (cajeroId > 0)
        {
            cajasConfiguradas = (await connection.QueryAsync(@"
                SELECT c.CAJA_ID, c.ALMACEN_ID
                FROM CAJAS_CAJEROS cc
                INNER JOIN CAJAS c ON c.CAJA_ID = cc.CAJA_ID
                WHERE cc.CAJERO_ID = @CajeroId AND c.OCULTO = 'N'
                ORDER BY CASE cc.TIPO_ACCESO WHEN 'A' THEN 0 ELSE 1 END, c.CAJA_ID",
                new { CajeroId = cajeroId })).ToList();
        }

        if (cajasConfiguradas.Count == 1)
        {
            return Convert.ToInt32(cajasConfiguradas[0].CAJA_ID);
        }

        if (cajasConfiguradas.Count > 1)
        {
            foreach (var caja in cajasConfiguradas)
            {
                int almacen = Convert.ToInt32(caja.ALMACEN_ID);
                decimal existencias = await ObtenerExistenciasAsync(connection, almacen);
                if (existencias > 0)
                    return Convert.ToInt32(caja.CAJA_ID);
            }
            // Ninguna caja con existencias: usar la primera configurada.
            return Convert.ToInt32(cajasConfiguradas[0].CAJA_ID);
        }

        // 2) Evidencia: apertura de caja mas reciente.
        int? cajaEvidencia = await connection.QueryFirstOrDefaultAsync<int?>(
            @"SELECT FIRST 1 CAJA_ID
              FROM MOVTOS_CAJAS
              WHERE USUARIO_CREADOR = @Usuario
              ORDER BY MOVTO_CAJA_ID DESC",
            new { Usuario = usuario });
        if (cajaEvidencia.HasValue)
        {
            _logger.LogInformation("[Auth] Caja por evidencia MOVTOS_CAJAS: {CajaId} (usuario {Usuario})",
                cajaEvidencia.Value, usuario);
            return cajaEvidencia.Value;
        }

        // 3) Evidencia: ventas previas.
        cajaEvidencia = await connection.QueryFirstOrDefaultAsync<int?>(
            @"SELECT FIRST 1 CAJA_ID
              FROM DOCTOS_PV
              WHERE USUARIO_CREADOR = @Usuario
              ORDER BY DOCTO_PV_ID DESC",
            new { Usuario = usuario });
        if (cajaEvidencia.HasValue)
        {
            _logger.LogInformation("[Auth] Caja por evidencia DOCTOS_PV: {CajaId} (usuario {Usuario})",
                cajaEvidencia.Value, usuario);
            return cajaEvidencia.Value;
        }

        _logger.LogWarning("Usuario '{Usuario}' sin caja asignada (ni config ni evidencia)", usuario);
        throw new RutaAuthException(
            "Tu usuario no tiene caja asignada. Contacta a soporte para configurar tu caja.");
    }

    /// <summary>
    /// Existencias del almacen en el periodo vigente de SALDOS_IN
    /// (ultimo ANO/MES registrado en la tabla).
    /// </summary>
    private async Task<decimal> ObtenerExistenciasAsync(FbConnection connection, int almacenId)
    {
        return await connection.ExecuteScalarAsync<decimal>(@"
            SELECT COALESCE(SUM(s.ENTRADAS_UNIDADES - s.SALIDAS_UNIDADES), 0)
            FROM SALDOS_IN s
            WHERE s.ALMACEN_ID = @AlmacenId
              AND s.ANO = (SELECT MAX(s2.ANO) FROM SALDOS_IN s2 WHERE s2.ALMACEN_ID = @AlmacenId)
              AND s.MES = (SELECT MAX(s3.MES) FROM SALDOS_IN s3 WHERE s3.ALMACEN_ID = @AlmacenId AND s3.ANO = s.ANO)",
            new { AlmacenId = almacenId });
    }
}

public interface IFirebirdAuthService
{
    Task<UsuarioSesion?> AutenticarAsync(string usuario, string password);
}
