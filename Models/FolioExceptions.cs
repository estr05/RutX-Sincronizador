namespace Rutx.Sincronizador.Models;

/// <summary>
/// La caja no tiene filas de folio en FOLIOS_CAJAS y la auto-creación
/// está deshabilitada (o falló). Se mapea a HTTP 400 con el mensaje.
/// </summary>
public class FolioSinSeriesException : InvalidOperationException
{
    public FolioSinSeriesException(string message) : base(message) { }
}

/// <summary>
/// La caja no tiene folios y AutoCrearFolios está deshabilitado.
/// El mensaje orienta al usuario a configurar la caja en Microsip.
/// </summary>
public class FolioAutoCreacionDeshabilitadaException : InvalidOperationException
{
    public FolioAutoCreacionDeshabilitadaException(string message) : base(message) { }
}

/// <summary>
/// La caja está siendo procesada por otra operación y se agotó el tiempo
/// de espera del semáforo. Se mapea a HTTP 409.
/// </summary>
public class FolioOcupadoException : InvalidOperationException
{
    public FolioOcupadoException(string message) : base(message) { }
}

/// <summary>
/// Conflicto transitorio de Firebird (deadlock / update conflict) que se
/// agotó tras los reintentos. Se mapea a HTTP 503 (reintentable).
/// </summary>
public class DeadlockTransientException : Exception
{
    public DeadlockTransientException(string message, Exception innerException)
        : base(message, innerException) { }
}
