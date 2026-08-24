# Procedimiento de purga de secretos del historial (OBLIGATORIA)

**Ambos repositorios son PÚBLICOS en GitHub.** Aunque los valores estén
rotados (muertos), siguen visibles en el historial. Esta purga es parte
obligatoria del feature Cloudflare Tunnel, no una tarea opcional futura.

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

## Literales objetivo (`expresiones.txt`)

```
RUTX_SECRET_KEY_PRODUCTION_SECURE_KEY_GENERATED_32_BYTES_MINIMUM_2026_TOKEN===>***REMOVED***
Password=masterkey===>Password=***REMOVED***
password=masterkey===>password=***REMOVED***
"AdminPassword":"admin"===>"AdminPassword":"***REMOVED***"
```

> Los fixtures deliberados de tests y el validador usan `masterkey` como
> palabra para DETECTARLA: esos usos son defensivos y pueden permanecer
> tras la purga (el scanner los tiene en allowlist). Si se prefiere cero
> apariciones absolutas, sustituir también en
> `Security/ProductionConfigurationValidator.cs` por comparación contra
> constante ofuscada y regenerar fixtures.

## Comandos (por repo, con `git-filter-repo` instalado)

```powershell
# 0. Prerrequisito: equipo notificado, sin cambios locales pendientes.
git bundle create ..\respaldo-<repo>.bundle --all

pip install git-filter-repo   # herramienta estándar (justificada; no es dependencia del producto)

git filter-repo --replace-text expresiones.txt --force
# filter-repo elimina el remote; re-agregar:
git remote add origin https://github.com/estr05/<repo>.git

git push --force --all origin
git push --force --tags origin
```

## Post-purga

1. Clon FRESCO en otra carpeta:
   ```powershell
   git log --all -S "RUTX_SECRET_KEY_PRODUCTION_SECURE_KEY_GENERATED_32_BYTES_MINIMUM_2026_TOKEN"  # vacío
   git log --all -S "masterkey" -- appsettings.json appsettings.Development.json                    # vacío
   pwsh Scripts/Invoke-SecretScan.ps1 -IncludeHistory                                               # exit 0
   ```
2. Verificar búsqueda de GitHub UI sobre el repo.
3. **Limitaciones honestas:** forks existentes y caches de PRs pueden
   retener blobs antiguos. GitHub eventualmente hace GC de objetos
   inalcanzables; si se juzga crítico, solicitar limpieza a soporte de
   GitHub indicando los SHAs afectados. Los colaboradores deben borrar
   sus clones locales viejos.

## Rollback de la purga

Solo posible restaurando desde los bundles del paso 3 (por eso son
prerrequisito bloqueante):

```powershell
git clone respaldo-<repo>.bundle restaurado
```
