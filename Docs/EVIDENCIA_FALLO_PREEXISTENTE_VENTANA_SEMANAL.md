# Evidencia: fallo preexistente en `VentanaSerie_Semanal` (fuera del alcance Cloudflare)

## Síntoma

`DashboardWebServiceFiltrosTests.VentanaSerie_Semanal_SinFechas_Ultimos7DiasDesdeHoy`
fallaba de forma dependiente de la fecha/huso al ejecutar la suite completa:

```
Expected: 2026-08-18T00:00:00.0000000-06:00
Actual:   2026-08-24T00:00:00.0000000-06:00
```

## Reproducción contra la base limpia (preexistente)

Con el working tree de Cloudflare guardado temporalmente (`git stash`),
el test falla igual en la rama sin cambios:

```
> git stash -q
> dotnet test Rutx.Sincronizador.Tests --configuration Release `
    --filter "FullyQualifiedName~VentanaSerie_Semanal_SinFechas"
  Con error! - Con error: 1, Superado: 0, Omitido: 0, Total: 1
> git stash pop -q   # restaurado
```

Es decir: **NO fue introducido por la implementación Cloudflare Tunnel**
(ningún archivo de dashboard fue tocado por esa fase).

## Causa raíz

El commit `83e7df9` ("fix: Corrección de reportes web y gráficas del
dashboard") cambió a propósito la ventana de la serie semanal:

```csharp
"semanal" => (LunesDe(hoy), hoy),     // Igual que los KPIs: lunes->hoy
```

pero no actualizó el test, que seguía exigiendo el contrato antiguo
(`hoy-6 -> hoy`, ventana rodante de 7 días). El test quedó obsoleto y solo
falla cuando "hoy" no es lunes.

## Resolución elegida: OPCIÓN 1 (corrección del test)

* El test se renombró a `VentanaSerie_Semanal_SinFechas_DesdeElLunesComoLosKPIs`
  y ahora afirma el contrato vigente (`LunesDe(Hoy) -> Hoy`), citando este
  documento.
* Suite completa tras la corrección: **230/230 en verde**
  (`dotnet test Rutx.Sincronizador.Tests -c Release --filter Category!=Integration`).
* La corrección del test **debe ir en un COMMIT SEPARADO** del resto de la
  fase Cloudflare, para que quede claro en la historia que es un arreglo
  preexistente y no parte del feature.
* No se excluyó nada del CI ni se ocultó ningún resultado.

## Alternativa descartada

Documentar excepción temporal (issue + responsable + fecha límite). Se
descartó porque la corrección es trivial, determinística y verificada;
una excepción dejaría la suite roja sin necesidad.
