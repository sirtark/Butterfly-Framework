# Roadmap

## Dónde estamos

| Área            | Estado                                                                                  |
|-----------------|-----------------------------------------------------------------------------------------|
| Infraestructura | `Butterfly.Sdk`, `butterfly` (CLI), feed local, `build.ps1`, firma, versión única        |
| Networking      | `Sockets` propio                                                                        |
| Communication   | Clientes DNS, HTTP/1.1, WebSockets, SMTP, POP3, IMAP, FTP/FTPS, NTP, MQTT 3.1.1, Mail    |
| Serialization   | Core + generador, JSON, XML, YAML, CSV, Protobuf, MessagePack, CBOR, perfiles            |
| Chrysalis       | Servidor HTTP/1.1 + HTTP/2 propio, REST, SOAP, JSON-RPC, XML-RPC, gRPC, binario, ASP.NET Core |
| Scripting       | C#, Lua (MoonSharp, NLua), ASP.NET Core, Chrysalis                                      |
| Workflows       | Motor, componentes, scripting, ASP.NET Core, Chrysalis (**solo stores en memoria**)     |
| SystemInfo      | CPU, memoria, SO, Chrysalis                                                             |
| Virtualization  | Hyper-V, libvirt, Chrysalis                                                             |
| DesignPatterns  | Creational, Structural, Behavioral, Pipelines, StateMachines (**sin tests**)             |

Huecos que hoy impiden llamarlo framework publicable:

- El repositorio git **no tiene ningún commit**: no hay historia, ramas ni forma de volver atrás.
- No hay CI, licencia, `CHANGELOG`, documentación XML en los paquetes ni SourceLink/símbolos.
- `DesignPatterns` no tiene tests; `IsAotCompatible` está activado pero nunca se publicó nada con AOT para comprobarlo.
- No existen todavía las piezas transversales que toda aplicación necesita: persistencia, seguridad,
  observabilidad, configuración. Workflows ya lo sufre (sus instancias se pierden al reiniciar).

## 0.0.1 — Primera versión publicable: cerrar lo que existe

Objetivo: **no agregar áreas nuevas**; que lo que ya hay sea instalable, reproducible y confiable.

### 1. Repositorio y proceso
- [ ] Primer commit, `.gitignore` (bin/obj/.vs/Packages), rama `main`, tag `v0.0.1`.
- [ ] `LICENSE` y `PackageLicenseExpression` en `Directory.Build.props`.
- [ ] CI (GitHub Actions o Azure DevOps): build + tests en **Windows y Linux** (SystemInfo, Virtualization y
      Sockets tienen código por plataforma), `./build.ps1 Samples` contra el feed recién generado.
- [ ] `CHANGELOG.md`.

### 2. Calidad de los paquetes
- [ ] `GenerateDocumentationFile` + `TreatWarningsAsErrors` para CS1591 en lo público (o lista explícita de excepciones).
- [ ] SourceLink, paquetes de símbolos (`.snupkg`), builds deterministas, `PackageReadmeFile` por paquete.
- [ ] Prueba de AOT: un sample publicado con `PublishAot` que use Serialization + Chrysalis + Communication.
- [ ] `Microsoft.CodeAnalysis.PublicApiAnalyzers` (`PublicAPI.Shipped.txt`): a partir de aquí cada cambio de API se ve en el diff.

### 3. Revisión de API (antes de congelarla)
- [ ] Nombres coherentes entre áreas (`…Options`, `Add…`/`Map…`, sync + `…Async`).
- [ ] Excepciones: una jerarquía por área, que los servicios Chrysalis ya traducen a estados.
- [ ] Tests para `DesignPatterns` (o sacarlo de 0.0.1 si no está maduro).

### 4. Persistencia mínima para Workflows
- [ ] `Butterfly.Workflows.Storage.FileSystem` (JSON en disco) como primer store durable, para que el motor sea usable
      en producción sin esperar a la capa de datos.

### 5. Documentación
- [ ] Una página por área: qué resuelve, ejemplo mínimo, decisiones (como la sección de Communication del README).
- [ ] Un sample por área (faltan Workflows, SystemInfo, Virtualization, DesignPatterns, Chrysalis + ASP.NET Core).

**Criterio de salida:** CI verde en Windows y Linux, samples compilando contra el feed, API pública registrada, tag `v0.0.1`.

## Después de 0.0.1: las piezas transversales

Orden propuesto según lo que desbloquea a las áreas existentes:

| Versión | Área nueva                    | Contenido                                                                                       | Desbloquea |
|---------|-------------------------------|-------------------------------------------------------------------------------------------------|------------|
| 0.1.0   | **Butterfly.Observability**   | Logging estructurado, métricas y trazas (`ActivitySource`/`Meter`, exportación OpenTelemetry)   | Chrysalis, Workflows, Communication: hoy cada uno loguea a su manera |
| 0.2.0   | **Butterfly.Data**            | Abstracción de almacenamiento (documentos/clave-valor) usando Serialization; SQLite, PostgreSQL, SQL Server | Stores de Workflows, auditoría, caché |
| 0.3.0   | **Butterfly.Security**        | Hashing de contraseñas, cifrado, JWT, API keys, autenticación/autorización en Chrysalis (sin ASP.NET Core) | Chrysalis standalone en producción |
| 0.4.0   | **Butterfly.Messaging**       | Bus de mensajes (en memoria, MQTT ya existente, RabbitMQ/Kafka), outbox                          | Eventos de Workflows entre servicios |
| 0.5.0   | **Butterfly.Configuration / Caching / Scheduling** | Configuración tipada y validada, caché en memoria/distribuida, tareas programadas | Hosting de aplicaciones completas |
| 0.6.0   | **Butterfly.Hosting**         | Plantillas (`butterfly new`), host que arma Chrysalis + Observability + Data + Security          | "Framework completo" de cara al usuario |

Pendientes de áreas existentes para ir intercalando: HTTP/2 y proxies en el cliente HTTP, IMAP IDLE, MQTT 5, SSH/SFTP,
clientes Chrysalis para REST/JSON-RPC (hoy solo binario), más hipervisores (VMware, Proxmox).
