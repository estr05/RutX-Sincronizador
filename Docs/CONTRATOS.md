# Contratos API REST - Sincronizador RUTX (Módulo 3)

Este documento define de forma precisa los contratos de la API REST (endpoints y esquemas JSON) que habilitan la sincronización de información entre la App Móvil y el sistema Microsip ERP a través del Sincronizador.

El Sincronizador está diseñado para mantener las cargas útiles (payloads) de la App Móvil ligeras. Por ello, se encarga de inyectar valores fiscales, contables e internos obligatorios en base de datos (`NOT NULL`), reduciendo la cantidad de datos transmitidos por la red.

---

## 1. Descarga de Catálogos (Sincronización Matutina)

*   **Endpoint:** `GET /api/v1/sync/morning`
*   **Descripción:** Descarga inicial de catálogos base indispensables para la operación offline diaria del vendedor. Contiene las entidades que Microsip exige como obligatorias (`MONEDA_ID`, `COND_PAGO_ID`, catálogos de artículos y clientes asignados).
*   **Códigos de Estado:**
    *   `200 OK`: Sincronización exitosa.
    *   `500 Internal Server Error`: Falla en la conexión con Firebird u otro error del sistema.

### Payload de Respuesta (JSON de ejemplo):
```json
{
  "mensaje": "Sincronización matutina exitosa",
  "datos": {
    "vendedor_id": 7,
    "ruta_activa": "Ruta Centro",
    "catalogos": {
      "monedas": [
        {
          "moneda_id": 1,
          "descripcion": "Peso Mexicano (MXN)"
        }
      ],
      "condiciones_pago": [
        {
          "cond_pago_id": 1,
          "descripcion": "Contado"
        },
        {
          "cond_pago_id": 2,
          "descripcion": "Crédito 15 días"
        }
      ],
      "articulos": [
        {
          "articulo_id": 88,
          "clave": "CHOC-01",
          "nombre": "Chocolate Carlos V 20g",
          "precio_sugerido": 15.50
        }
      ]
    },
    "clientes_asignados": [
      {
        "cliente_id": 1045,
        "nombre": "Abarrotes La Esperanza",
        "limite_credito": 5000.00
      }
    ]
  }
}
```

---

## 2. Creación de Clientes Nuevos

*   **Endpoint:** `POST /api/v1/clientes`
*   **Descripción:** Registra un nuevo cliente prospectado en la ruta. El Sincronizador se encargará de inyectar la moneda por defecto (`DefaultMonedaId`) y la condición de pago (`DefaultCondPagoId`) desde su archivo de configuración si el dispositivo móvil no las proporciona.
*   **Códigos de Estado:**
    *   `200 OK` / `201 Created`: Cliente creado exitosamente. Retorna el ID asignado por el trigger de base de datos de Microsip.
    *   `400 Bad Request`: Datos de entrada inválidos (ej. nombre vacío o sujetoIeps mal formateado).
    *   `500 Internal Server Error`: Falla en la inserción en Firebird.

### Estructura y Restricciones del Payload:
| Campo | Tipo | Requerido | Descripción / Restricciones |
| :--- | :--- | :--- | :--- |
| `nombre` | String | Sí | Nombre completo o razón social comercial. Máx. 200 caracteres. |
| `sujetoIeps` | String | No | Indica si el cliente es sujeto a IEPS (`"S"` o `"N"`). Por defecto `"N"`. |
| `diferirCfdiCobros` | Boolean | No | Si difiere o no la facturación de cobros en Microsip. Por defecto `false`. |
| `limiteCredito` | Decimal | No | Límite de crédito asignado para compras a plazo. Mayor o igual a `0.00`. Por defecto `0.00`. |
| `monedaId` | Integer | No | ID de la moneda en Microsip. Si es `null`, el Sincronizador inyecta la de configuración. |
| `condPagoId` | Integer | No | ID de la condición de pago. Si es `null`, el Sincronizador inyecta la de configuración. |

### Carga Útil de Petición (JSON de ejemplo):
```json
{
  "nombre": "Abarrotes La Esperanza",
  "sujetoIeps": "N",
  "diferirCfdiCobros": false,
  "limiteCredito": 5000.00,
  "monedaId": null,
  "condPagoId": null
}
```

### Payload de Respuesta:
```json
{
  "message": "Cliente creado exitosamente en Microsip",
  "clienteId": 1234
}
```

---

## 3. Registro de Ventas (Levantamiento de Pedidos)

*   **Endpoint:** `POST /api/v1/ventas`
*   **Descripción:** Envía la información de una venta o pedido recolectado en ruta. Impacta en las tablas cabecera `DOCTOS_VE` y detalle `DOCTOS_VE_DET` de Microsip.
*   **Regla de Negocio e Integridad:** La App Móvil **NO** envía datos de CFDI, comisiones ni valores por defecto del ERP. El Sincronizador inyecta dinámicamente los campos requeridos (`ES_CFD = 'N'`, `ENVIADO = 'N'`, asignación automática de folios, secuencia y posiciones de partidas) para asegurar que la transacción sea válida en Microsip.
*   **Códigos de Estado:**
    *   `201 Created`: Venta registrada con éxito.
    *   `400 Bad Request`: Payload estructurado incorrectamente o sin partidas de detalles.
    *   `500 Internal Server Error`: Falla interna al escribir la cabecera y el detalle en una misma transacción.

### Estructura y Restricciones del Payload:
#### Cabecera (Venta)
| Campo | Tipo | Requerido | Descripción / Restricciones |
| :--- | :--- | :--- | :--- |
| `venta_movil_id` | String | Sí | Identificador único temporal generado por la App Móvil (para evitar duplicidad por reintentos). |
| `vendedor_id` | Integer | Sí | ID numérico del vendedor que realizó la transacción. |
| `cliente_id` | Integer | Sí | ID numérico del cliente asignado. |
| `fecha_hora` | DateTime | Sí | Fecha y hora en formato ISO 8601 (`YYYY-MM-DDTHH:mm:ss`). |
| `notas` | String | No | Comentarios especiales o instrucciones de entrega. Máx. 250 caracteres. |
| `detalles` | Array | Sí | Listado de artículos vendidos (mínimo 1 partida). |

#### Detalle (Partidas)
| Campo | Tipo | Requerido | Descripción / Restricciones |
| :--- | :--- | :--- | :--- |
| `articulo_id` | Integer | Sí | ID del producto vendido. |
| `unidades` | Decimal | Sí | Cantidad física de piezas o kilogramos. Mayor que `0`. |
| `precio_unitario`| Decimal | Sí | Precio unitario cobrado al cliente. Mayor o igual a `0.00`. |

### Carga Útil de Petición (JSON de ejemplo):
```json
{
  "venta_movil_id": "VTA-99823",
  "vendedor_id": 7,
  "cliente_id": 1045,
  "fecha_hora": "2026-06-25T14:30:00",
  "notas": "Entregar por la puerta trasera",
  "detalles": [
    {
      "articulo_id": 88,
      "unidades": 2.00000,
      "precio_unitario": 15.50000
    }
  ]
}
```

### Payload de Respuesta:
```json
{
  "message": "Venta registrada exitosamente (MOCK)",
  "venta_movil_id": "VTA-99823",
  "docto_ve_id": 9999,
  "total_renglones": 1
}
```

---

## 4. Cierre del Día (Cierre de Ruta)

*   **Endpoint:** `POST /api/v1/sync/closing`
*   **Descripción:** Registra las operaciones finales del día del vendedor. Consolida el inventario que queda físicamente en la unidad de transporte, los productos mermados (dañados/caducos) y las devoluciones efectuadas por los clientes.
*   **Códigos de Estado:**
    *   `201 Created`: Cierre procesado exitosamente.
    *   `400 Bad Request`: Faltan campos de cabecera o contiene detalles inválidos (ej. cantidades negativas o nulas).
    *   `500 Internal Server Error`: Falla interna al procesar el consolidado.

### Estructura y Restricciones del Payload:
#### Cabecera (Cierre)
| Campo | Tipo | Requerido | Descripción / Restricciones |
| :--- | :--- | :--- | :--- |
| `vendedor_id` | Integer | Sí | ID del vendedor. |
| `ruta_activa` | String | Sí | Nombre o clave de la ruta activa del día. Máx. 100 caracteres. |
| `fecha_hora` | DateTime | Sí | Fecha y hora en formato ISO 8601 del cierre del día. |
| `inventario_final` | Array | No | Artículos y cantidades remanentes en el camión. |
| `mermas` | Array | No | Artículos que se declararon dañados/inutilizables durante el trayecto. |
| `devoluciones` | Array | No | Artículos retornados por los clientes con sus respectivos motivos. |

#### Inventario Final (`inventario_final`)
| Campo | Tipo | Requerido | Descripción / Restricciones |
| :--- | :--- | :--- | :--- |
| `articulo_id` | Integer | Sí | ID del producto. |
| `unidades_restantes`| Decimal | Sí | Cantidad física restante. Mayor a `0`. |

#### Mermas (`mermas`)
| Campo | Tipo | Requerido | Descripción / Restricciones |
| :--- | :--- | :--- | :--- |
| `articulo_id` | Integer | Sí | ID del producto. |
| `unidades` | Decimal | Sí | Cantidad mermada. Mayor a `0`. |
| `motivo` | String | Sí | Explicación del desperfecto (ej. `"Empaque dañado"`, `"Caducado"`). Máx. 200 caracteres. |

#### Devoluciones (`devoluciones`)
| Campo | Tipo | Requerido | Descripción / Restricciones |
| :--- | :--- | :--- | :--- |
| `cliente_id` | Integer | Sí | ID del cliente que realiza la devolución. |
| `articulo_id` | Integer | Sí | ID del producto. |
| `unidades` | Decimal | Sí | Cantidad devuelta. Mayor a `0`. |
| `motivo` | String | Sí | Explicación del retorno (ej. `"Producto equivocado"`, `"Mal estado"`). Máx. 200 caracteres. |

### Carga Útil de Petición (JSON de ejemplo):
```json
{
  "vendedor_id": 7,
  "ruta_activa": "Ruta Centro",
  "fecha_hora": "2026-06-25T18:00:00",
  "inventario_final": [
    {
      "articulo_id": 88,
      "unidades_restantes": 12.00000
    }
  ],
  "mermas": [
    {
      "articulo_id": 88,
      "unidades": 1.00000,
      "motivo": "Empaque roto durante trayecto"
    }
  ],
  "devoluciones": [
    {
      "cliente_id": 1045,
      "articulo_id": 88,
      "unidades": 2.00000,
      "motivo": "Exceso de pedido solicitado por cliente"
    }
  ]
}
```

### Payload de Respuesta:
```json
{
  "mensaje": "Cierre de día procesado exitosamente (MOCK)",
  "vendedor_id": 7,
  "ruta_activa": "Ruta Centro",
  "fecha_hora": "2026-06-25T18:00:00",
  "resumen": {
    "articulos_inventario": 1,
    "articulos_merma": 1,
    "articulos_devolucion": 1
  }
}
```

---

## N. Transporte y base URL p�blica (Cloudflare Tunnel)

Los payloads de este contrato NO cambian. Lo �nico que cambia es el origen:

- **Producci�n:** `https://sync.<cliente>.com` (Cloudflare Tunnel ? listener
  loopback `127.0.0.1:5048` del Sincronizador). La APK se compila con
  `--dart-define=API_BASE_URL=https://sync.<cliente>.com` (repo appmovil).
- **Desarrollo:** HTTP directo LAN/Tailscale a `:5047` (sin define).

### Allowlist publicada en el t�nel (todo lo dem�s ? 404 del edge)

| Ruta | M�todo |
|---|---|
| `/health` | GET |
| `/api/auth/login`, `/api/auth/me` | POST / GET |
| `/api/v1/routes/sync`, `/summary` | GET |
| `/api/v1/routes/close` | POST |
| `/api/v1/pv/ventas`, `/api/v1/pv/noventa` | POST |
| `/api/v1/cobranza/insert` | POST |
| `/api/v1/credito/pedidos` | GET |
| `/api/v1/credito/clientes/{id:int}/documentos` (regex `[0-9]+`) | GET |
| `/api/v1/messages` | GET |

NO publicadas: `/admin`, `/api/v2/admin/*`, `/api/v2/web/*`,
`/api/v1/routes/debug/*`, `/api/v1/inventario/*`, `/api/v1/pv/fotos/*`,
aplicar/cancelar/consultar ticket PV.

> Regla: una ruta nueva consumida por Flutter exige actualizaci�n
> SIMULT�NEA de este contrato + allowlist ingress + pruebas.
