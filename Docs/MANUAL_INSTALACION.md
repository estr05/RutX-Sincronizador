# Manual de instalación — Sincronizador RUTX

| | |
|---|---|
| **Software** | Sincronizador RUTX (.NET 10) |
| **Versión** | 1.3 |
| **Fecha** | Agosto 2026 |
| **Componentes** | `Rutx.Sincronizador.Admin.exe` (launcher) · `Rutx.Sincronizador.exe` (API como servicio Windows, puerto 5047) |

Este manual describe cómo preparar e instalar el Sincronizador RUTX. El
Sincronizador actúa como puente entre la app móvil RUTX y la base de datos de
Microsip (Firebird): recibe las operaciones de los vendedores y las registra en
el esquema de Punto de Venta (PV) de Microsip.

**Modelo de despliegue**: los ejecutables se preparan en una máquina de
desarrollo y se llevan a la PC del cliente **en un USB**. No es necesario
clonar nada en la PC del cliente. Se usa el paquete **self-contained**
(incluye .NET dentro), porque la PC del cliente no tiene frameworks
instalados.

```
Máquina de desarrollo                PC del cliente
┌─────────────────────────┐   USB   ┌──────────────────────────────┐
│ git clone + publish     │ ──────► │ Copiar carpeta al disco      │
│ → carpeta "Sincronizador" │      │ Ejecutar Admin.exe → asistente│
└─────────────────────────┘         └──────────────────────────────┘
```

---

> ⚠️ **LEE ESTO PRIMERO — NO escribas `dotnet publish` a mano.**
> Para generar los ejecutables solo haz **doble clic en
> `publicar_selfcontained.bat`** (el que se lleva a la PC del cliente, que no
> tiene .NET instalado) o en `publicar.bat` (versión estándar para tu PC de
> desarrollo). Ambos están en la raíz de la carpeta `RutX-Sincronizador`.
> Escribir los comandos a mano ha causado errores una y otra vez
> (`--selfcontained` sin guion, comandos cortados a la mitad al pegarlos,
> etc.). Detalle en la sección 2.2.

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
| .NET Runtime 10 | **No necesario**: el paquete que se lleva al cliente (*self-contained*, sección 2.2) ya lo incluye |
| Servidor Firebird | En ejecución en `localhost:3050`, con la base de Microsip (`.fdb`). **Recomendado: Firebird 2.5 o superior** (versiones soportadas: sección 1, *Versiones de Firebird soportadas*) |
| Credenciales Firebird | Usa credenciales seguras (SEC-01); ten las contraseñas reales a la mano |
| Puerto 5047 | Libre (lo usa la API del Sincronizador) |
| Red | La app móvil debe alcanzar esta PC por la red local en `IP:5047` |

> **Nota.** Si Microsip Punto de Venta está abierto en la PC del cliente, sus
> catálogos (cajas, formas de cobro, etc.) están en caché: los cambios en la
> base de datos no se verán hasta reiniciar Microsip. Si es posible, instala
> con Microsip cerrado.

### Versiones de Firebird soportadas

El asistente detecta la versión de Firebird de la BD elegida en **dos niveles**:

1. **Por el header del archivo `.fdb`** — sin conexión y con cualquier versión:
   lee el *On-Disk Structure* (ODS) directamente del archivo e identifica la
   **familia** (ej. "Firebird 3.0"). Funciona incluso si las credenciales aún
   no conectan.
2. **Por SQL** — una vez que la conexión es válida, obtiene la **versión exacta**
   del motor (ej. "3.0.14") vía `RDB$GET_CONTEXT('SYSTEM','ENGINE_VERSION')`
   (disponible en Firebird 2.1+), con respaldo en las tablas de monitoreo MON$
   (`MON$ODS_MAJOR` / `MON$ODS_MINOR`).

| Firebird | ODS | Soporte |
|---|---|---|
| 5.0 | 13.1 | ✅ Soportado |
| 4.0 | 13.0 | ✅ Soportado |
| 3.0 | 12.0 | ✅ Soportado (recomendado) |
| 2.5 | 11.2 | ✅ Soportado |
| 2.1 | 11.1 | ⚠️ Detectable por ODS, pero el driver incluido (FirebirdClient 10.x) soporta oficialmente 2.5+; conviene actualizar |
| 2.0 | 11.0 | ⚠️ Muy antiguo; el driver incluido ya no lo soporta |
| 1.5 / 1.0 | 10.x | ❌ No soportado (muy antiguo para la app móvil) |

> - En el **paso 2** del asistente, el mensaje 🟢 **Conexión OK** muestra la
>   versión detectada, ej. `Conexión OK — Firebird 3.0 (ODS 12.0) — 3.0.14`.
> - Si la BD es de **Firebird 1.5/2.0**, se muestra la familia por ODS
>   (`Firebird 1.5 (ODS 10.1) — version desconocida`): esas versiones no tienen
>   la función SQL que entrega la versión exacta. Es esperado.
> - Que el asistente **detecte** la versión (por ODS) no garantiza que el driver
>   pueda **conectar** con ella: el driver incluido soporta oficialmente
>   **Firebird 2.5 en adelante**.

#### Esquema compatible entre versiones (universal)

El esquema de Microsip es **prácticamente idéntico entre versiones**: se
verificó una BD de Microsip 2025/2026 (**Firebird 5.0, ODS 13.1**) contra una
anterior (**Firebird 3.0, ODS 12.0**) y resultaron las **mismas 33 tablas
críticas**, las mismas columnas requeridas, los **mismos 8 triggers críticos**
(`DOCTOS_PV_BEFINS`, `DOCTOS_PV_DET_BEFINS`, `DOCTOS_PV_COBROS_BEFINS`,
`DOCTOS_PV_COBROS_AFTINS_0`, `DOCTOS_PV_AFTUPD_0`, `DOCTOS_PV_BEFUPD_0`,
`DOCTOS_PV_LIGAS_BEFINS`, `DOCTOS_CC_BEFINS`), la misma FK
`CAJEROS_A_DOCTOS_PV` y los generadores `ID_DOCTOS` / `ID_CATALOGOS`.

Las únicas diferencias son **columnas nuevas opcionales** (ej.
`DOCTOS_CC.CENTRO_COSTO_ID`, `CONCEPTOS_CC.MANEJA_CENTROS_COSTO`) que **no
afectan** al sincronizador. Por eso **no hay que configurar nada por versión**:
las consultas funcionan igual en Firebird 3.0, 4.0 y 5.0.

**Validación de escritura real en FB 5.0**: el INSERT de venta de
`VentaServicePv` (header `DOCTOS_PV` con los IDs reales de la BD del cliente)
se ejecutó exitosamente contra una BD Firebird 5.0 (ODS 13.1) con el motor
5.0.4 embebido: los triggers generan el ID (`-1 → GEN_ID`), las CHECKs/FKs
aceptan los valores del sync y `ES_FAC_GLOBAL` se completa con el default del
dominio. La prueba se hizo en transacción y se revirtió, sin dejar datos.

Lo que sí cambia entre clientes son los **IDs** (moneda, condición de pago,
impuestos, sucursal, cajero...), porque cada BD Microsip tiene los suyos. Eso
se configura desde el panel web (sección 5.1) con la validación automática.

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

**La que se lleva al cliente — el archivo `publicar_selfcontained.bat`**:
la PC del cliente no tendrá .NET instalado, por eso esta versión incluye
"todo adentro" (≈150 MB). No se escribe nada a mano, no hay typos posibles:

1. En el Explorador, entra a la carpeta `RutX-Sincronizador` (la que clonaste).
2. Haz **doble clic** en `publicar_selfcontained.bat`.
3. Espera a que termine: verás la carpeta `Sincronizador` con los dos `.exe`.

> **[Captura de pantalla: el archivo publicar_selfcontained.bat dentro de la carpeta y su ventana al terminar]**

Resultado esperado — la carpeta `Sincronizador` debe contener:

```
Sincronizador/
├── Rutx.Sincronizador.exe        ← API (el Sincronizador)
├── Rutx.Sincronizador.Admin.exe  ← Launcher (uso diario)
├── appsettings.json              ← Configuración (plantilla; se regenera al instalar)
└── wwwroot/admin.html            ← Panel web
```

> **Para tu PC de desarrollo** (que sí tiene .NET), puedes usar la versión
> estándar con `publicar.bat` (≈37 MB, más rápida de generar). En la PC del
> cliente siempre se usa la self-contained.

> ❌ **No existe alternativa con comandos a mano.** Escribir `dotnet publish`
> directamente ha causado typos (`--selfcontained` sin guion) y comandos
> cortados a la mitad al pegarlos, generando `MSB1001` o "Falta el argumento
> requerido para la opción: '-c'". Los `.bat` ejecutan los mismos comandos
> con la ortografía correcta, siempre.

> **Si la publicación falla** con errores de dependencias: ejecuta `dotnet restore`
> en la raíz del repositorio y vuelve a intentar.

---

## 3. Instalar en la PC del cliente (desde USB)

1. Copia la carpeta `Sincronizador` (≈150 MB, la versión self-contained
generada con `publicar_selfcontained.bat`) a un **USB**.
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
2. El asistente verifica la firma del archivo y solicitará usuario y contraseña.
3. Resultado:
   - 🟢 **Conexión OK** → haz clic en **Siguiente →**.
   - 🔴 **Fallo la conexión** → escribe el usuario y la contraseña reales de la
     BD del cliente y haz clic en **Probar conexión** hasta obtener el verde.
4. Haz clic en **Siguiente →**.

> **[Captura de pantalla: asistente, paso 2 (BD y credenciales)]**

> El asistente guarda en la configuración **las credenciales que sí conectaron**,
> no los valores por defecto. Cada cliente puede tener su propia BD y
> credenciales: el proceso es el mismo.

> 🟢 **Conexión OK** muestra también **la versión de Firebird detectada**, ej.
> `Conexión OK — Firebird 3.0 (ODS 12.0) — 3.0.14`. Se detecta primero por el
> header del archivo `.fdb` (funciona sin conexión y con cualquier versión) y se
> afina por SQL cuando la conexión es válida. Ver *Versiones de Firebird
> soportadas* en la sección 1.

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

Al abrir `Rutx.Sincronizador.Admin.exe` se muestra la ventana principal:

| Botón | Función |
|---|---|
| **▶ Iniciar** | Arranca la API en `:5047` (o inicia el servicio Windows si está instalado) |
| **⏹ Detener** | Detiene la API (o detiene el servicio Windows) |
| **⚙ Conf (web)** | Abre el panel web `http://localhost:5047/admin` (inicia la API si es necesario) |
| **📋 Copy logs** | Copia los logs de la ventana al portapapeles |
| **🔧 Instalar servicio** | Instala el Sincronizador como servicio de Windows (pide permisos de administrador vía UAC, sección 4.1) |

**Comportamiento de bandeja**: al cerrar la ventana (X), el launcher se minimiza
a la bandeja del sistema (no mata el proceso). Para cerrar definitivamente,
haz clic derecho en el icono de bandeja → **Salir**.

**Servicio Windows** (opcional pero recomendado): si instalas el servicio,
el sincronizador arranca automáticamente con Windows, sobrevive al cierre de
sesión y se reinicia si falla. Puedes gestionarlo desde el launcher o con
`sc query RutxSincronizador`.

### 4.1 Instalar el servicio de Windows (requiere permisos de administrador)

1. Haz clic en **🔧 Instalar servicio** y confirma en el diálogo *"¿Continuar?"*.
2. Windows muestra el aviso de **Control de cuentas de usuario (UAC)** — el
   clásico *"¿Quieres permitir que esta aplicación haga cambios en este
   dispositivo?"* → haz clic en **Sí**.
3. El launcher se relanza elevado, instala el servicio (`RutxSincronizador`) y
   muestra *"El servicio se instaló correctamente."*. El botón cambia a
   **🔧 Servicio ON**.

> - Si el launcher ya corre **como administrador**, no aparece el aviso UAC y la
>   instalación se hace directo.
> - Si haces clic en **No** en el aviso de UAC (o el equipo no permite elevar la
>   app), la instalación se cancela y el launcher te lo avisa: no queda el
>   servicio a medias. Reintenta aceptando el aviso o ejecuta el launcher con
>   clic derecho → **Ejecutar como administrador**.
> - La cuenta de Windows debe tener **privilegios de administrador**.

**Gestionar el servicio instalado** — con el botón **🔧 Servicio ON**:

- **SI** = iniciar el servicio · **NO** = detenerlo · **CANCEL** = no hacer nada.
- Iniciar o detener el servicio también requiere administrador: si el launcher
  no está elevado, vuelve a aparecer el aviso UAC automáticamente.

- **Estado**: `● En ejecución · :5047` / `● Servicio activo · :5047` / `● Detenido`.
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

La card **Conexión a la base de datos** muestra los parámetros de la BD
Firebird **desglosados en campos** (ruta del `.fdb`, servidor, puerto, usuario,
contraseña, dialecto, charset, pooling y timeout) en **solo lectura** por
ahora: la cadena se conserva intacta en `appsettings.json`. También muestra la
**versión detectada** de la BD (ej. `🗄️ CRUZROJASCLC.fdb · Firebird 5.0 (ODS
13.1)`), leída del **header del archivo `.fdb`** (sin conexión), así que se
muestra incluso cuando el servidor local no puede abrir la BD (BD más nueva
que el servidor Firebird de la PC).

| Sección | Campos |
|---|---|
| `ConnectionStrings` | `FirebirdConnection` (ruta de la BD, usuario, contraseña, `localhost:3050`) |
| `MicrosipSettings` | `DefaultMonedaId`, `DefaultCondPagoId`, `DefaultSucursalId`, `DefaultAlmacenId`, `DefaultImpuestoId`, `DefaultPrecioEmpresaId`, `DefaultFormaCobroId`, `CreditFormaCobroIds`, `DefaultCajeroId`, `DefaultConceptoCobroId` |

#### Validación automática de IDs contra la BD

Al abrir el panel, cada campo de `MicrosipSettings` se **valida en vivo contra
la BD configurada** (`GET /api/v2/admin/ids-criticos`):

- **✓ Campo correcto** — el ID existe en la BD: queda **bloqueado** y muestra el
  nombre real del registro (ej. `✓ CONTADO · CONDICIONES_PAGO.COND_PAGO_ID`).
- **✗ Campo con error** — el ID no existe (los IDs cambian entre clientes):
  queda **editable** con un botón **🔍 Buscar** que abre una **consulta
  predefinida** del catálogo (buscar por nombre o por ID) para elegir el valor
  correcto.
- **Formas de cobro a crédito** (`CreditFormaCobroIds`) — lista editable
  separada por comas, con verificación individual por ID.
- Al **guardar**, la validación se re-ejecuta automáticamente.

> Catálogos con consulta predefinida: `MONEDAS`, `CONDICIONES_PAGO`,
> `SUCURSALES`, `ALMACENES`, `IMPUESTOS`, `FORMAS_COBRO`, `CAJEROS`,
> `CONCEPTOS_CC` y `PRECIOS_EMPRESA`. La columna de nombre se descubre sola de
> los metadatos, porque cada BD Microsip la nombra distinto.

> **[Captura de pantalla: panel web, sección Configuración con validación de IDs]**

### 5.2 Estado de compatibilidad

El panel muestra una card con **puntitos de estado** que se **re-evalúa
automáticamente al abrir**:

- 🔴 **Faltantes** — requiere configuración.
- 🟡 **Avisos** — funciona, pero conviene revisar.
- 🟢 **Ok** — verificación correcta.

La auditoría incluye la sección **NOT NULL**: verifica en las tablas que el
sincronizador escribe (ventas, cobranzas, clientes, folios) que toda columna
`NOT NULL` sin **default efectivo** (de la columna o del **dominio** de
Firebird) sea una que el sincronizador escribe explícitamente al insertar.
Si una versión nueva de Microsip agrega una columna obligatoria sin default
que el sincronizador no llena, la auditoría la marca en 🔴 con su nombre para
revisarla antes de que falle la primera venta.

> **[Captura de pantalla: panel web, card Estado de compatibilidad con puntitos]**

### 5.3 Sincronización matutina

`POST /api/v2/admin/sync-matutino` con un `vendedor_id` ejecuta la descarga
matutina (clientes, productos, caja) reutilizando la lógica de la app móvil,
sin requerir token.

### 5.4 Cola offline (reintentos)

Cuando la app móvil no tiene conexión, las operaciones (ventas, clientes) se
encolan en SQLite (`Data/cola_offline.db`) y el servidor las procesa en cuanto
puede. Configurable desde el panel:

| Campo | Qué hace |
|---|---|
| `IntervaloProcesoSegundos` | Cada cuántos segundos el fondo revisa la cola (default 5) |
| `MaxIntentos` | Reintentos máximos por operación (default 5) |
| `BaseDelaySegundos` / `MaxDelaySegundos` | Backoff exponencial entre reintentos (1 → 30 s) |
| `RutaSqlite` | Ubicación del archivo de cola |

Comportamiento importante (para no perder datos):

- **Proceso muerto a mitad**: si el sincronizador se cae mientras procesaba una
  operación, esta queda en estado `PROCESANDO`. Pasados **2 minutos** de
  inactividad se libera sola y vuelve a la cola de reintentos — no se pierde.
- **Heartbeat anti-duplicados**: mientras una operación se está procesando, el
  servidor renueva su marca de tiempo cada 20 s. Así una operación *en curso*
  (aunque tarde más de 2 min por una BD lenta o reintentos de `deadlock`)
  **nunca** se reprocesa en paralelo, evitando ventas duplicadas con folio
  distinto.
- **Irreparables**: tras `MaxIntentos` fallidos la operación queda en estado
  `FALLIDO` y deja de reintentarse (puede revisarse en la BD SQLite).

> Ver `Docs/DISENO_COLA_OFFLINE.md` para el diseño completo.

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
| `MSB1001: Modificador desconocido` al publicar | El comando se escribió mal | Doble clic en `publicar.bat` (sección 2.2) |
| "Ya hay una instancia del Sincronizador ejecutándose" | Otra instancia activa o servicio corriendo | Cierra el launcher/terminal o detén el servicio: `sc stop RutxSincronizador` |
| "No se localizó `Rutx.Sincronizador.exe`" | El launcher no encuentra el ejecutable | Verifica que ambos .exe estén en la misma carpeta; vuelve a ejecutar el asistente |
| "Publicador desconocido" al ejecutar | Ejecutables sin firma digital | **Más información → Ejecutar de todas formas** |
| El servicio no arranca al iniciar Windows | Servicio instalado pero en modo manual | Cambia a automático: `sc config RutxSincronizador start= delayed-auto` |
| El servicio no se inicia | Firebird detenido, puerto 3050 bloqueado o permisos | Verifica el servicio Firebird y las credenciales en `appsettings.json` |
| `OpenSCManager ERROR 5: Acceso denegado` al instalar el servicio | El launcher no tenía permisos de administrador | Con el aviso UAC el launcher se relanza elevado solo (sección 4.1). Si el error persiste, ejecuta el launcher con clic derecho → **Ejecutar como administrador** |
| El panel `/admin` no carga | La API está detenida | Presiona **▶ Iniciar** o inicia el servicio |
| `Could not find file: wwwroot\admin.html` | La API se arrancó desde una carpeta sin `wwwroot` | Arranca desde el launcher instalado |
| Error de SQLite repetido (`SQLite Error 14`) | Falta la carpeta `Data\` | Arranca desde el launcher; borra `Data\cola_offline.db` si persiste |
| La auditoría marca IDs faltantes | IDs por defecto que no existen en la BD | Reasigna los `Default*Id` desde el panel web (sección 5.1) |
| La app móvil no conecta a la API | IP/puerto incorrectos, red o firewall | Verifica `IP:5047`, misma red que los vendedores, puerto abierto |
| La conexión a la BD falla | Firebird detenido, puerto 3050 o credenciales | Confirma el servicio Firebird y las credenciales reales |
| Error `Violación de llave foránea` al guardar una venta | Un ID referenciado (cliente, cajero, forma de cobro, impuesto…) no existe en la BD | El error indica la constraint y la tabla (ej. `ARTS_A_DOCTOS_PV_DET`). Corrige los IDs desde el panel web (sección 5.1) |
| Error de campo vacío (`NOT NULL`) al insertar | La app envió un dato incompleto | El mensaje indica la columna. Revisa la información enviada desde la app |
| Error `Valor duplicado` | Ya existe el folio/llave (otro proceso lo creó) | El sincronizador concilia en el siguiente intento; si persiste, revisa folios de la caja (sección 8) |
| Error de `deadlock` / base ocupada | Microsip está escribiendo en la misma tabla en ese momento | Es transitorio: la operación se reintenta sola (respuesta `reintentable: true`) |
| Una operación de la cola offline no se procesa | Quedó en `PROCESANDO` y el proceso murió a mitad | Se libera sola pasados 2 min de inactividad (sección 5.4); revisa el log del servicio si persiste |
| Una operación de la cola offline falla siempre (estado `FALLIDO`) | Payload irreparable (datos inválidos, FK rota) | Tras `MaxIntentos` (default 5) se marca `FALLIDO` y deja de reintentarse (sección 5.4) |
| Los errores de la API ahora incluyen `categoria` y `reintentable` | Comportamiento nuevo: los errores SQL se clasifican con mensaje accionable | La app y el panel pueden mostrar el mensaje `mensaje` directamente; `categoria` indica el tipo (llave foránea, conexión, ODS…) |
| El asistente muestra `Firebird (ODS X.Y) — version desconocida` | BD de Firebird 1.5/2.0: esas versiones no tienen la función SQL de la versión exacta | Es esperado: la familia se identifica por el ODS del archivo (sección 1). Considera actualizar Firebird |
| El asistente avisa que la versión de Firebird es muy antigua | Firebird 2.0/2.1/2.5 (sin soporte oficial del motor) | Funciona, pero conviene actualizar a 3.0+ (sección 1) |
| El asistente detecta la versión pero no conecta (🔴) | El driver incluido soporta oficialmente Firebird 2.5+; en 2.1 o inferior la conexión puede fallar | Actualiza el servidor Firebird a 2.5 o superior (sección 1) |
| El asistente muestra `unsupported on-disk structure ... found X.Y, support A.B` | La BD es de una versión de Firebird **más nueva que el servidor local** (ej. BD Firebird 5.0 con un servidor Firebird 3.0 en esta PC) | Instala en la PC el servidor Firebird de la **misma versión (o superior)** con la que se creó la BD (sección 1) |
| Microsip no muestra los tickets | La caja abierta no coincide | Verifica acceso del cajero a la caja correcta (sección 8) |

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

1. En el launcher, presiona **⏹ Detener** (o **🔧 Servicio ON → Detener servicio**).
2. Cierra el launcher (haz clic derecho en la bandeja → **Salir**).
3. Si el servicio está instalado, desinstálalo: `sc delete RutxSincronizador`
   (requiere una consola **como administrador**: sin elevación, `sc delete` es
   rechazado con *Acceso denegado*).
4. Elimina la carpeta de instalación `C:\ProgramData\RUTX\Sincronizador`.
5. Para restaurar una configuración anterior, el panel web guarda respaldos
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

1. Prepara el paquete (sección 2): doble clic en `publicar_selfcontained.bat`
   (la misma versión que llevarías a un cliente real).
2. Copia la carpeta `Sincronizador` a una carpeta limpia de prueba
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
