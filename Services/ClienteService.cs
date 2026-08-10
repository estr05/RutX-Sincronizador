using System.Data;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

public interface IClienteService
{
    Task<int> CrearClienteAsync(ClienteCreateDto cliente);
}

public class ClienteService : IClienteService
{
    private readonly IConfiguration _configuration;
    private readonly string _connectionString;

    public ClienteService(IConfiguration configuration)
    {
        _configuration = configuration;
        _connectionString = _configuration.GetConnectionString("FirebirdConnection") 
            ?? throw new InvalidOperationException("No se encontró la cadena de conexión FirebirdConnection.");
    }

    public async Task<int> CrearClienteAsync(ClienteCreateDto cliente)
    {
        // Estrategia 1: Hardcodeo Seguro leyendo valores de configuración (appsettings.json)
        int monedaId = cliente.MonedaId ?? _configuration.GetValue<int>("MicrosipSettings:DefaultMonedaId");
        int condPagoId = cliente.CondPagoId ?? _configuration.GetValue<int>("MicrosipSettings:DefaultCondPagoId");

        // El ID siempre se envía en -1 para que el trigger lo autogenere de forma segura
        int clienteId = -1;

        using IDbConnection db = new FbConnection(_connectionString);
        
        string sql = @"
            INSERT INTO CLIENTES (
                CLIENTE_ID, NOMBRE, SUJETO_IEPS, DIFERIR_CFDI_COBROS, 
                LIMITE_CREDITO, MONEDA_ID, COND_PAGO_ID
            ) 
            VALUES (
                @ClienteId, @Nombre, @SujetoIeps, @DiferirCfdiCobros, 
                @LimiteCredito, @MonedaId, @CondPagoId
            )
            RETURNING CLIENTE_ID;";
            
        var idGenerado = await db.ExecuteScalarAsync<int>(sql, new
        {
            ClienteId = clienteId,
            Nombre = cliente.Nombre,
            SujetoIeps = cliente.SujetoIeps,
            DiferirCfdiCobros = cliente.DiferirCfdiCobros, 
            LimiteCredito = cliente.LimiteCredito,
            MonedaId = monedaId,
            CondPagoId = condPagoId
        });

        return idGenerado;
    }
}
