# Manual de instalación — Sincronizador RUTX

| | |
|---|---|
| **Software** | Sincronizador RUTX (.NET 10) |
| **Versión** | 1.0 |
| **Fecha** | Agosto 2026 |
| **Componentes** | `Rutx.Sincronizador.Admin.exe` (launcher) · `Rutx.Sincronizador.exe` (API, puerto 5047) |

Este manual describe cómo preparar e instalar el Sincronizador RUTX. El
Sincronizador actúa como puente entre la app móvil RUTX y la base de datos de
Microsip (Firebird): recibe las operaciones de los vendedores y las registra en
el esquema de Punto de Venta (PV) de Microsip.

**Modelo de despliegue**: los ejecutables se preparan en una máquina de
desarrollo y se llevan a la PC del cliente **en un USB**. No es necesario
clonar nada en la PC del cliente.

```
Máquina de desarrollo                PC del cliente
┌─────────────────────────┐   USB   ┌──────────────────────────────┐
│ git clone + publish     │ ──────► │ Copiar carpeta al disco      │
│ → carpeta "publicacion" │         │ Ejecutar Admin.exe → asistente│
└─────────────────────────┘         └──────────────────────────────┘
```

---

> ⚠️ **LEE ESTO PRIMERO — NO escribas `dotnet publish` a mano.**
> Para generar los ejecutables solo haz **doble clic en `publicar.bat`**
> (está en la raíz de la carpeta `RutX-Sincronizador`). Escribir los comandos
> a mano ha causado errores una y otra vez (`--selfcontained` sin guion,
> comandos cortados a la mitad al pegarlos, etc.). El .bat hace exactamente lo
> mismo y sin errores. Detalle en la sección 2.2.

---

## 1. Requisitos previos

### En la máquina de desarrollo (donde se prepara el paquete)

| Componente | Requisito |
|---|---|
| Sistema operativo | Windows 10 u 11 (64 bits) |
| .NET SDK 10 | Necesario para compilar y publicar los ejecutables |
| Git | Para clonar el repositorio |
| Acceso a GitHub | Para clonar el repositorio |

### En la PC del cliente (donde se instala)

| Componente | Requisito |
|---|---|
| Sistema operativo | Windows 10 u 11 (64 bits) |
| .NET Runtime 10 | Para ejecutar los programas (no necesario si el paquete se publica *self-contained*) |
| Servidor Firebird | En ejecución en `localhost:3050`, con la base de Microsip (`.fdb`) |
| Credenciales Firebird | Por defecto `SYSDBA` / `masterkey`; ten las reales a la mano |
| Puerto 5047 | Libre (lo usa la API del Sincronizador) |
| Red | La app móvil debe alcanzar esta PC por la red local en `IP:5047` |

> **Nota.** Si Microsip Punto de Venta está abierto en la PC del cliente, sus
> catálogos (cajas, formas de cobro, etc.) están en caché: los cambios en la
> base de datos no se verán hasta reiniciar Microsip. Si es posible, instala
> con Microsip cerrado.

---

## 2. Preparar los ejecutables (máquina de desarrollo)

### 2.1 Clonar el repositorio

```bash
git clone https://github.com/estr05/RutX-Sincronizador.git
cd RutX-Sincronizador
```

> **Si falla el clonado** (error de red o credenciales): verifica la conexión a
> internet y los permisos de lectura del repositorio.

### 2.2 Publicar los ejecutables

**Única forma — el archivo `publicar.bat`** (no se escribe nada a mano,
no hay typos posibles):

1. En el Explorador, entra a la carpeta `RutX-Sincronizador` (la que clonaste).
2. Haz **doble clic** en `publicar.bat`.
3. Espera a que termine: verás la carpeta `publicacion` con los dos `.exe`.

> **[Captura de pantalla: el archivo publicar.bat dentro de la carpeta y su ventana al terminar]**

Resultado esperado — la carpeta `publicacion` debe contener:

```
publicacion/
├── Rutx.Sincronizador.exe        ← API (el Sincronizador)
├── Rutx.Sincronizador.Admin.exe  ← Launcher (uso diario)
├── appsettings.json              ← Configuración (plantilla; se regenera al instalar)
└── wwwroot/admin.html            ← Panel web
```

> **Si la PC del cliente no tendrá .NET Runtime instalado**, usa la versión
> "todo adentro" (*self-contained*): abre una terminal en la carpeta y escribe
> la única línea `publicar.bat selfcontained`. El paquete pesa más (≈150 MB),
> pero no requiere instalar nada en el cliente.

> ❌ **No existe alternativa con comandos a mano.** Escribir `dotnet publish`
> directamente ha causado typos (`--selfcontained` sin guion) y comandos
> cortados a la mitad al pegarlos, generando `MSB1001` o "Falta el argumento
> requerido para la opción: '-c'". El `publicar.bat` ejecuta los mismos dos
> comandos con la ortografía correcta, siempre.

> **Si la publicación falla** con errores de dependencias: ejecuta `dotnet restore`
> en la raíz del repositorio y vuelve a intentar.

---

## 3. Instalar en la PC del cliente (desde USB)

1. Copia la carpeta `publicacion` (≈37 MB en versión estándar) a un **USB**.
2. En la PC del cliente, **copia la carpeta del USB al disco local**
   (ej. `C:\Sincronizador`). Así el uso diario no dependerá del USB.
3. Ejecuta el launcher:

```bash
C:\Sincronizador\Rutx.Sincronizador.Admin.exe
```

   También puedes hacer **doble clic** sobre el archivo.

> **Si Windows muestra "Publicador desconocido"** (SmartScreen): haz clic en
> **Más información → Ejecutar de todas formas**. Los ejecutables no están
> firmados con certificado; es esperado en esta etapa.

> **[Captura de pantalla: carpeta copiada en el disco con los dos .exe]**

En el **primer arranque** se abre el **asistente de instalación**, que guía el
proceso en 4 pasos:

### Paso 1 — Ubicación

1. Confirma la carpeta de instalación (valor por defecto:
   `C:\ProgramData\RUTX\Sincronizador`).
2. Usa **Examinar…** solo si deseas otra ruta.
3. Haz clic en **Siguiente →**.

> **[Captura de pantalla: asistente, paso 1 (Ubicación)]**

> **Si la carpeta no se puede crear** (permisos o ruta inválida): el asistente lo
> indica y no permite avanzar. Cambia la ruta y reintenta.

### Paso 2 — Base de datos y credenciales

1. Haz clic en **Buscar BD…** y selecciona el archivo `.fdb` **de la BD del
   cliente** (ej. `C:\Microsip datos\SU_BD.fdb`).
2. El asistente verifica la firma del archivo e intenta conectar con
   `SYSDBA` / `masterkey`.
3. Resultado:
   - 🟢 **Conexión OK** → haz clic en **Siguiente →**.
   - 🔴 **Fallo la conexión** → escribe el usuario y la contraseña reales de la
     BD del cliente y haz clic en **Probar conexión** hasta obtener el verde.
4. Haz clic en **Siguiente →**.

> **[Captura de pantalla: asistente, paso 2 (BD y credenciales)]**

> El asistente guarda en la configuración **las credenciales que sí conectaron**,
> no los valores por defecto. Cada cliente puede tener su propia BD y
> credenciales: el proceso es el mismo.

### Paso 3 — Instalar y auditar

1. Haz clic en **▶ Instalar ahora**.
2. El asistente copia los ejecutables a la carpeta de instalación, genera la
   configuración con la BD elegida y ejecuta la **auditoría de compatibilidad**
   en vivo contra **esa** BD (solo lectura).
3. Al terminar muestra el conteo: 🔴 faltantes · 🟡 avisos · 🟢 ok.
4. Haz clic en **Siguiente →**.

> **[Captura de pantalla: asistente, paso 3 (instalación y auditoría)]**

> **Si el puerto 5047 está ocupado**: el asistente avisa y omite la auditoría
> automática. Ejecútala después desde el panel web (sección 5).
>
> **Si la API no responde al arrancar** (p. ej. la base de datos no conecta):
> revisa la cadena de conexión en el panel web y vuelve a ejecutar la auditoría.

### Paso 4 — Finalizar

1. Revisa el resumen: carpeta de instalación, base de datos, usuario y resultado
   de la auditoría.
2. Haz clic en **✓ Finalizar**.

> **[Captura de pantalla: asistente, paso 4 (finalizar)]**

La instalación queda registrada en `instalacion.json` dentro de la carpeta de
instalación; el launcher la detecta automáticamente en los arranques siguientes.

> **Recomendado**: crea un **acceso directo en el escritorio** de
> `Rutx.Sincronizador.Admin.exe` (carpeta `C:\Sincronizador`) para el uso diario.

---

## 4. Operación diaria (launcher)

Al abrir `Rutx.Sincronizador.Admin.exe` (carpeta `C:\Sincronizador`, o el acceso
directo del escritorio) se muestra la ventana principal:

| Botón | Función |
|---|---|
| **▶ Iniciar** | Arranca la API en `:5047` |
| **⏹ Detener** | Detiene la API |
| **⚙ Conf (web)** | Abre el panel web `http://localhost:5047/admin` (inicia la API si es necesario) |
| **📋 Copy logs** | Copia los logs de la ventana al portapapeles |

- **Estado**: `● En ejecución · :5047` / `● Detenido`.
- **Logs**: verde = INFO · amarillo = WARN · rojo = ERROR.

> **[Captura de pantalla: ventana principal del launcher con logs]**

> **Si al iniciar aparece "Ya hay una instancia del Sincronizador ejecutándose"**:
> hay otra instancia activa (p. ej. una terminal anterior o un launcher abierto).
> Ciérrala o detén el proceso que ocupa el puerto 5047:
> `netstat -ano | findstr :5047` → `taskkill /F /PID <pid>`.

---

## 5. Configuración desde el panel web (`/admin`)

Abre el panel con **⚙ Conf (web)**.

### 5.1 Configuración de la base de datos e IDs

El formulario edita `appsettings.json`. Al guardar, el servidor crea un respaldo
`.bak` automático y aplica los cambios **en caliente** (sin reiniciar).

| Sección | Campos |
|---|---|
| `ConnectionStrings` | `FirebirdConnection` (ruta de la BD, usuario, contraseña, `localhost:3050`) |
| `MicrosipSettings` | `DefaultMonedaId`, `DefaultCondPagoId`, `DefaultSucursalId`, `DefaultAlmacenId`, `DefaultImpuestoId`, `DefaultPrecioEmpresaId`, `DefaultFormaCobroId`, `CreditFormaCobroIds`, `DefaultCajeroId`, `DefaultConceptoCobroId` |

> **[Captura de pantalla: panel web, sección Configuración]**

### 5.2 Estado de compatibilidad

El panel muestra una card con **puntitos de estado** que se **re-evalúa
automáticamente al abrir**:

- 🔴 **Faltantes** — requiere configuración.
- 🟡 **Avisos** — funciona, pero conviene revisar.
- 🟢 **Ok** — verificación correcta.

> **[Captura de pantalla: panel web, card Estado de compatibilidad con puntitos]**

### 5.3 Sincronización matutina

`POST /api/v2/admin/sync-matutino` con un `vendedor_id` ejecuta la descarga
matutina (clientes, productos, caja) reutilizando la lógica de la app móvil,
sin requerir token.

---

## 6. Verificación posterior a la instalación

Confirma que la instalación quedó operativa:

- [ ] `▶ Iniciar` → el estado muestra `● En ejecución · :5047`.
- [ ] `GET http://localhost:5047/health` responde `200`.
- [ ] El panel `http://localhost:5047/admin` carga sin errores.
- [ ] La auditoría muestra **0 fallos corregibles** (los avisos deben estar justificados).
- [ ] La app móvil inicia sesión y completa la descarga matutina.
- [ ] Una venta de prueba aparece en Microsip Punto de Venta (con la caja abierta).

---

## 7. Solución de problemas

| Síntoma | Causa probable | Solución |
|---|---|---|
| `MSB1001: Modificador desconocido` o `Falta el argumento requerido para la opción: '-c'` al publicar | El comando se escribió mal o se pegó partido (typo `--selfcontained`, comando cortado, etc.) | Evita escribir a mano: doble clic en `publicar.bat` (sección 2.2). Si insistes con comandos: `--self-contained` (con guion), `Rutx.Sincronizador.Admin/Rutx.Sincronizador.Admin.csproj`, desde la raíz del repositorio |
| "Ya hay una instancia del Sincronizador ejecutándose" | Otra instancia activa o proceso huérfano en :5047 | Cierra el launcher/terminal anterior o ejecuta `taskkill /F /PID <pid>` sobre el proceso que escucha en 5047 |
| "No se localizó `Rutx.Sincronizador.exe`" | El launcher no encuentra el ejecutable del sync | Verifica que ambos .exe estén en la misma carpeta (`C:\Sincronizador`); vuelve a ejecutar el asistente o ubica el archivo manualmente |
| "Publicador desconocido" al ejecutar | Ejecutables sin firma digital | **Más información → Ejecutar de todas formas** |
| El panel `/admin` no carga | La API está detenida | Presiona **▶ Iniciar** (el botón **⚙ Conf (web)** inicia la API automáticamente) |
| `Could not find file: wwwroot\admin.html` | La API se arrancó desde una carpeta sin `wwwroot` | Arranca desde el launcher instalado (usa la carpeta de instalación como raíz) |
| Error de SQLite repetido (`SQLite Error 14`) | Falta la carpeta `Data\` en el directorio de trabajo | Arranca desde el launcher; si persiste, borra `Data\cola_offline.db` y reinicia |
| La auditoría marca IDs faltantes | IDs por defecto de `MicrosipSettings` que no existen en la BD del cliente | Reasigna los `Default*Id` al valor real desde el panel web (sección 5.1) o crea el dato en Microsip (sección 8) |
| La app móvil no conecta a la API | IP/puerto incorrectos, red WiFi distinta o firewall | Verifica la IP de la PC en la red (`IP:5047`), misma red que los vendedores, puerto 5047 abierto en el firewall |
| La conexión a la BD falla en el paso 2 | Firebird detenido, puerto 3050 bloqueado o credenciales incorrectas | Confirma el servicio Firebird en la PC del cliente y prueba las credenciales reales |
| Microsip no muestra los tickets del día | La caja abierta en PVenta no coincide con la del cajero | Verifica que el cajero tenga acceso de apertura/operación sobre la caja correcta (sección 8) |

---

## 8. Resultados de la auditoría: qué configurar

Cuando la auditoría marca un **fallo (🔴)** o **aviso (🟡)**, consulta esta tabla:

| Hallazgo | Implicación | Acción |
|---|---|---|
| `CONDICIONES_PAGO` id 1 (Contado) | En Production, el sincronizador usa este id como default; sin él, los inserts de clientes/ventas fallan por FK | Reasigna `DefaultCondPagoId` al id que sí exista en la BD, o crea el id 1 |
| `MONEDAS` id 1 | No existe la moneda base | Reasigna `DefaultMonedaId` o crea la moneda |
| `IMPUESTOS` id 622 (IVA 16%) | No existe el impuesto default | Reasigna `DefaultImpuestoId` o crea el impuesto |
| `FORMAS_COBRO` id 67 (Efectivo) | No existe la forma de cobro default | Reasigna `DefaultFormaCobroId` o crea la forma |
| `FORMAS_COBRO` 71 / 703 / 2205 | Formas de crédito (solo afectan ventas a crédito) | Crea las que se utilicen o limpia `CreditFormaCobroIds` |
| `SUCURSALES` id 4274 / `ALMACENES` id 19 | No existen sucursal/almacén default | Reasigna `DefaultSucursalId` / `DefaultAlmacenId` a los valores reales |
| `CAJEROS` id 2419 (aviso) | Cajero de respaldo; se usa solo si el agente no tiene cajero por usuario | Crea el cajero con el **mismo usuario** del vendedor |
| `CONCEPTOS_CC` id 11 | No existe el concepto de cobro (abono CxC) | Crea el concepto o reasigna `DefaultConceptoCobroId` |
| Triggers / generador `ID_DOCTOS` faltantes | Esquema de Microsip PV incompleto | **No se crean a mano**: generarlos con Microsip |
| FK `CAJEROS_A_DOCTOS_PV` (aviso) | El resolver de cajero usa el fallback | No rompe el flujo si cada agente tiene cajero por usuario |
| Datos mínimos faltantes (cajas, vendedores, clientes, artículos con precio, folios) | La BD no tiene catálogo suficiente | Dar de alta los datos en Microsip |
| Agente sin cajero por usuario (aviso) | El vendedor usará el cajero de respaldo | Crear el cajero con el mismo usuario y asignar acceso a la caja |

### Cadena de datos del flujo (orden de creación)

```
VENDEDOR → AGENTE (usuario + contraseña de la app) → CAJERO (mismo usuario) → CAJAS_CAJEROS (acceso 'A' + 'O' a la caja)
```

Si falta algún eslabón, el vendedor no puede operar con la app. Los cambios son
**solo de datos** (INSERT/UPDATE); no se modifica estructura (DDL) ni triggers
de Microsip. Realiza cambios de datos con respaldo (`gbak -b`) y con Microsip
cerrado cuando sea posible.

---

## 9. Desinstalación / reversión

1. En el launcher, presiona **⏹ Detener** y cierra la ventana.
2. Elimina la carpeta de instalación `C:\ProgramData\RUTX\Sincronizador`
   (incluye el marcador `instalacion.json`).
3. Elimina la carpeta del disco `C:\Sincronizador` y la copia del USB.
4. Para restaurar una configuración anterior, el panel web guarda respaldos
   automáticos como `appsettings.json.bak`.

---

## 10. Referencias

- `README.md` del repositorio — arquitectura, endpoints y contrato móvil.
- `Docs/CONTRATOS.md` — contratos de los endpoints.
- `Docs/DISENO_COLA_OFFLINE.md` — diseño de la cola offline.
- `Integracion/auditar_compatibilidad.py` — auditoría de compatibilidad por
  línea de comandos (100% solo lectura).

---

## Anexo A — Simular la instalación en una sola PC (prueba)

Para validar el flujo completo **sin una segunda máquina**, usa tu propia PC
como si fuera la del cliente:

1. Prepara el paquete (sección 2): doble clic en `publicar.bat`
   (o `publicar.bat selfcontained` si quieres la versión completa).
2. Copia la carpeta `publicacion` a una carpeta limpia de prueba
   (ej. `C:\PruebaCliente\`) — simula el USB y el disco del cliente.
3. Ejecuta `C:\PruebaCliente\Rutx.Sincronizador.Admin.exe`.
   El asistente se abre (no existe `instalacion.json` y el sync está junto al
   launcher).
4. En el paso 2, selecciona **una BD de prueba diferente** a la habitual y
   prueba la conexión con sus credenciales.
5. Completa los 4 pasos y verifica:
   - La estructura creada en `C:\ProgramData\RUTX\Sincronizador`.
   - El `appsettings.json` generado apunta a la **BD de prueba**.
   - La auditoría muestra los contadores 🔴🟡🟢 de esa BD.
   - El panel web (`/admin`) carga con sus puntitos.
6. **Limpieza** (para volver a tu flujo de desarrollo normal):
   - Detén el sync (⏹ Detener) y cierra el launcher.
   - Elimina `C:\ProgramData\RUTX\Sincronizador` (si no, el launcher preferirá
     esta instalación de prueba sobre `dotnet run`).
   - Elimina `C:\PruebaCliente`.
