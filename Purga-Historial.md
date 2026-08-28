# Procedimiento de purga de secretos del historial (OBLIGATORIA)

**Ambos repositorios son PÚBLICOS en GitHub.** Aunque los valores estén
rotados (muertos), siguen visibles en el historial. Esta purga es parte
obligatoria del feature Cloudflare Tunnel, no una tarea opcional futura.

## Estado y evidencia del inventario (2026-08-24)

| Repo | Hallazgo en historial (`git log --all -S`) | Acción |
|---|---|---|
| Sincronizador | JWT vieja: ≥2 commits; `Password=masterkey`: ≥9 commits | **PURGAR** |
| App móvil | 0 hits para JWT vieja y `masterkey` en TODO el historial | **Sin reescritura** — no hay nada que purgar |

* El conteo del sincronizador es un PISO, no el total exacto: el pickaxe
  (`log -S`) aborta recorridos con errores de textconv por un filtro
  global de `.doc` (`Mapeo_Clientes_Microsip.doc`). Por eso la
  verificación post-purga usa `git grep` sobre blobs (ver abajo), inmune
  a ese ruido.
* Consecuencia práctica: **solo el sincronizador se reescribe.** Los
  commits locales `a79cf8c` y `1487b39` obtendrán HASHES NUEVOS tras la
  purga y su push NUNCA será fast-forward (será force-push coordinado).
  En el móvil, `b26fea0` conserva su hash y sí es fast-forward.
* `expresiones_sincronizador.txt` vive FUERA del repositorio
  (`%TEMP%\opencode\purga\`) porque contiene los literales vivos que se
  van a borrar; jamás debe versionarse.
* Respaldo externo acordado: `C:\Users\estra\Backups\purga-2026-08-24\`.


## Secuencia correcta (resuelve la contradicción v3)

> **NO publicar primero y purgar después.** El orden vinculante es:

```
1. ROTAR secretos            (JWT nueva, AdminPassword fuerte, usuario
                              Firebird RUTX_SYNC — ver Docs/FIREBIRD_GRANTS_RUTX_SYNC.md)
2. SANEAR working tree       (placeholders CHANGE_ME_* versionados;
                              valores reales solo en appsettings.Local.json / env vars)
3. RESPALDAR refs            (git bundle de ambos repos, guardado FUERA
                              del equipo y del repo)
4. PURGAR historial          (git filter-repo --replace-text, coordinado
                              con todo el equipo, en fecha acordada)
5. FORCE-PUSH controlado     (--force --all --tags en ambos repos)
6. TODOS reclonan            (los clones viejos reintroducen los secretos)
7. GATES en verde            (Invoke-SecretScan.ps1 -IncludeHistory,
                              build, tests, hook activo)
8. PUSH/PR normal            recién aquí continúa el flujo habitual
```

## Literales objetivo (archivo externo `expresiones_sincronizador.txt`)

> Sintaxis filter-repo: `literal==>reemplazo` (DOS equals). El espaciado
> debe coincidir EXACTO con el blob histórico; verificado contra
> `git show 9e426cb:appsettings.json`.

```
RUTX_SECRET_KEY_PRODUCTION_SECURE_KEY_GENERATED_32_BYTES_MINIMUM_2026_TOKEN==>REDACTED_ROTATED_JWT
Password=masterkey==>Password=REDACTED_ROTATED_FIREBIRD
"AdminPassword": "admin"==>"AdminPassword": "REDACTED"
```

> Los fixtures deliberados de tests y el validador usan `masterkey` como
> palabra para DETECTARLA: esos usos son defensivos y pueden permanecer
> tras la purga (el scanner los tiene en allowlist). Si se prefiere cero
> apariciones absolutas, sustituir también en
> `Security/ProductionConfigurationValidator.cs` por comparación contra
> constante ofuscada y regenerar fixtures.

## Comandos (SOLO sincronizador; ejecutar en la ventana coordinada)

```powershell
# 0. Prerrequisito: equipo notificado, validación demo completa,
#    sin cambios locales pendientes (git status limpio).
cd "C:\MyProyects\Teknologix prueba\sincronizador-demo"

# 1. RESPALDO EXTERNO bloqueante
git bundle create C:\Users\estra\Backups\purga-2026-08-24\rutx-sincronizador.bundle --all

# 2. PURGA (filter-repo ya instalado; si falta: pip install git-filter-repo)
git filter-repo --replace-text "$env:TEMP\opencode\purga\expresiones_sincronizador.txt" --force

# 3. filter-repo elimina el remote; re-agregar con la URL exacta:
git remote add origin https://github.com/estr05/RutX-Sincronizador.git

# 4. FORCE-PUSH controlado (NO es fast-forward: los hashes cambiaron)
git push --force --all origin
git push --force --tags origin

# 5. TODOS los clones locales del equipo se BORRAN y reclonan.
```

> El móvil NO se reescribe: su push de `b26fea0` es fast-forward normal
> y puede hacerse en cualquier momento posterior a esta coordinación.

## Post-purga

1. Clon FRESCO en otra carpeta y verificación ROBUSTA por blobs
   (`git grep` sobre cada commit; `log -S` queda descartado por el
   ruido de textconv de `.doc`):
   ```powershell
   git clone C:\Users\estra\Backups\purga-2026-08-24\rutx-sincronizador.bundle clon-verificacion
   cd clon-verificacion

   $literales = @(
     'RUTX_SECRET_KEY_PRODUCTION_SECURE_KEY_GENERATED_32_BYTES_MINIMUM_2026_TOKEN',
     'Password=masterkey',
     '"AdminPassword": "admin"'
   )
   foreach ($lit in $literales) {
     $hits = 0
     foreach ($sha in git rev-list --all) {
       if (git grep -qF -- "$lit" $sha 2>$null) { Write-Output "QUEDA: $lit en $sha"; $hits++ }
     }
     if ($hits -eq 0) { Write-Output "LIMPIO: $lit" }
   }
   ```
   Resultado esperado: tres líneas `LIMPIO:` y cero `QUEDA:`.
2. Gates en el clon sanitizado:
   ```powershell
   dotnet build Rutx.Sincronizador.csproj -c Release
   dotnet test Rutx.Sincronizador.Tests -c Release --filter Category!=Integration   # 230/230
   pwsh Scripts/Invoke-SecretScan.ps1 -IncludeHistory                               # exit 0
   ```
3. Verificar búsqueda de GitHub UI sobre el repo.
4. **Limitaciones honestas:** forks existentes y caches de PRs pueden
   retener blobs antiguos. GitHub eventualmente hace GC de objetos
   inalcanzables; si se juzga crítico, solicitar limpieza a soporte de
   GitHub indicando los SHAs afectados. Los colaboradores deben borrar
   sus clones locales viejos.
5. Con gates en verde sobre los HASHES NUEVOS se abren los PRs
   (los hashes a79cf8c/1487b39 citados en mensajes quedan como
   referencia histórica únicamente).

## Rollback de la purga

Solo posible restaurando desde los bundles del paso 3 (por eso son
prerrequisito bloqueante):

```powershell
git clone respaldo-<repo>.bundle restaurado
```
