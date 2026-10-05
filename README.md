# Butterfly

## Estructura

```
Butterfly.slnx               Solución única con todo el framework (src, tests, tools)
Directory.Build.props        Configuración común: versión, firma, empaquetado
Directory.Build.targets      Convenciones: InternalsVisibleTo, caché de NuGet, catálogo
Directory.Packages.props     Versiones centralizadas de paquetes de terceros
NuGet.config                 Feed local "Butterfly" -> Packages/Release/Butterfly
build.ps1                    Build / Test / Pack / InstallTool / Samples / All
eng/                         Butterfly.png (icono) y Butterfly.snk (clave de firma)
src/
  Butterfly.Sdk/             SDK de MSBuild; Sdk/Butterfly.Version.props es la ÚNICA fuente de la versión
  <Área>/Butterfly.<Área>.<Módulo>/
  Communication/             Protocolos de red (ver más abajo)
tests/
  <Área>/Butterfly.<Área>.<Módulo>.Tests/
  Communication/Shared/      Servidores falsos (TCP/UDP) y certificado TLS de prueba, compartidos por sus tests
tools/
  Butterfly.Tool/            CLI "butterfly"
samples/                     Consumidores de los paquetes (aislados del build del framework)
Packages/Release/Butterfly/  Feed local donde se publica todo
```

## Versión

La versión se cambia en un único sitio: `src/Butterfly.Sdk/Sdk/Butterfly.Version.props`.
Todas las librerías, `Butterfly.Sdk` y `Butterfly.Tool` se publican con esa versión.

## Dependencias: framework vs. consumidores

El framework se compila **solo desde el código fuente**; los paquetes del feed son únicamente para los consumidores.

| Proyecto                            | SDK                    | Dependencias Butterfly                       |
|-------------------------------------|------------------------|----------------------------------------------|
| Framework (`src/`, `tests/`, `tools/`) | `Microsoft.NET.Sdk`  | `ProjectReference`                           |
| Consumidor (`samples/`, otras soluciones) | `Butterfly.Sdk/x.y.z` | `PackageReference` sin versión (`butterfly add`) |

Si un proyecto del framework usara `Butterfly.Sdk` o un `PackageReference` a `Butterfly.*`, dependería de su
propio resultado: solo compilaría mientras el feed tuviera una build anterior. El build lo impide con un error
explicativo, y `butterfly add`/`init` se niegan a tocar proyectos del framework.

## Compilar y publicar

```powershell
./build.ps1              # empaqueta todo en Packages/Release/Butterfly
./build.ps1 Test
./build.ps1 Clean        # vacía el feed (versión actual) y la caché de NuGet, sin borrar la carpeta
./build.ps1 InstallTool  # instala/reinstala la herramienta global "butterfly"
./build.ps1 All          # Pack + Test + InstallTool + Samples
```

### Vaciar el feed

`./build.ps1 Clean` es la forma correcta de empezar de cero: quita los paquetes de la versión actual del
feed y de la caché de NuGet, y borra `bin/obj`. **No borres la carpeta `Packages/Release/Butterfly`**: está
registrada como fuente de NuGet y, si no existe, falla el restore de cualquier proyecto de la máquina
(`NU1301`). Igualmente, cualquier build del framework la vuelve a crear. `Samples` e `InstallTool` publican
primero si el feed está vacío.

Compilar en `Release` desde Visual Studio también publica los paquetes. Al empaquetar se borra la copia
de esa versión de la caché de NuGet (`~/.nuget/packages`), para que los consumidores reciban los cambios
aunque la versión no haya cambiado. Se desactiva con `-p:ButterflyEvictPackageCache=false`.

## Consumir Butterfly

```xml
<Project Sdk="Butterfly.Sdk/0.0.1">
  <ItemGroup>
    <!-- Sin versión: el SDK usa la del framework -->
    <PackageReference Include="Butterfly.Networking.Sockets" />
  </ItemGroup>
</Project>
```

Con otro SDK (Web, Worker...), Butterfly.Sdk se añade encima:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <Sdk Name="Butterfly.Sdk" Version="0.0.1" />
  ...
</Project>
```

Una referencia con `Version` (o `VersionOverride` si se usa gestión central) queda fijada y el SDK no la toca.

## Herramienta `butterfly`

Siempre entrega los paquetes en la versión del framework con la que se compiló.

```
butterfly list                           Paquetes disponibles (* = referenciado por el proyecto)
butterfly add Networking.Sockets         Añade paquetes (nombre corto o completo)
butterfly remove Networking.Sockets
butterfly init                           Pasa el proyecto a Butterfly.Sdk (o lo actualiza de versión)
butterfly version
```

Opciones comunes: `-p|--project <ruta>` y `--no-restore`. Si el restore falla, los cambios se revierten.

Según el proyecto, `add`:
- con **Butterfly.Sdk** → referencia sin versión (la pone el SDK);
- con **gestión central** (`Directory.Packages.props`) → `PackageVersion` con la versión del framework;
- en otro caso → `Version="<versión del framework>"` en la referencia.

Al subir la versión del framework: `./build.ps1 All` y luego `butterfly init` en cada proyecto consumidor
(incluidos los de `samples/`).

## Communication: protocolos

Clientes construidos sobre `Butterfly.Networking.Sockets` (sin `System.Net.Sockets`), en `src/Communication/`:

| Paquete                                | Protocolo                                                               | Puertos (TLS)   |
|----------------------------------------|-------------------------------------------------------------------------|-----------------|
| `Butterfly.Communication.Core`         | Base común: conexión, TLS/STARTTLS, resolución de nombres, SASL         |                 |
| `Butterfly.Communication.Dns`          | DNS (A, AAAA, CNAME, MX, TXT, NS, PTR, SRV, SOA, CAA), UDP + TCP, EDNS  | 53              |
| `Butterfly.Communication.Http`         | HTTP/1.1 y HTTPS: keep-alive, chunked, gzip/deflate/br, redirecciones, cookies, formularios, multipart | 80 (443) |
| `Butterfly.Communication.WebSockets`   | WebSockets `ws://` y `wss://`                                           | 80 (443)        |
| `Butterfly.Communication.Mail`         | Mensajes y MIME: texto, HTML, adjuntos, imágenes inline, cabeceras codificadas |          |
| `Butterfly.Communication.Smtp`         | Envío de correo: STARTTLS, PLAIN/LOGIN/CRAM-MD5/XOAUTH2                  | 587, 25 (465)   |
| `Butterfly.Communication.Pop3`         | Descarga de correo: STLS, SASL, UIDL, TOP                               | 110 (995)       |
| `Butterfly.Communication.Imap`         | Buzones: carpetas, búsqueda, fetch, flags, move, append, XOAUTH2        | 143 (993)       |
| `Butterfly.Communication.Ftp`          | FTP y FTPS (explícito/implícito, canal de datos cifrado), MLSD/LIST, reanudación | 21 (990) |
| `Butterfly.Communication.Ntp`          | Hora de red (SNTP): desfase y retardo                                   | 123             |
| `Butterfly.Communication.Mqtt`         | MQTT 3.1.1: QoS 0/1/2, retained, will, sesiones persistentes            | 1883 (8883)     |

```csharp
using var smtp = new SmtpClient();
smtp.Connect("smtp.example.com");              // 587 + STARTTLS obligatorio
smtp.Authenticate("usuario", "clave");
smtp.Send(new MailMessage { From = "yo@example.com", Subject = "Hola", TextBody = "..." , To = { "tu@example.com" } });
```

Decisiones comunes a todos los clientes:

- **TLS por defecto** (`TlsMode.Auto`): TLS implícito en el puerto seguro del protocolo y STARTTLS **obligatorio** en el
  resto; si el servidor no lo ofrece, falla en lugar de seguir en claro (`TlsMode.StartTlsWhenAvailable` o `None`
  para aceptarlo). Los certificados se validan siempre, salvo que `TlsOptions` diga lo contrario.
- **Sin contraseñas en claro**: autenticarse sin TLS contra un host remoto lanza excepción salvo
  `AllowInsecureAuthentication = true` (loopback está permitido).
- **Timeouts siempre**: `ConnectionOptions` (conexión, lectura, escritura). Las sockets los aplican en el SO.
- **Nombres**: IP literal → `localhost` → fichero hosts → DNS propio → resolver del sistema (búsquedas de dominio, mDNS, VPN).
- **Síncrono + `…Async`**: las operaciones son bloqueantes; las versiones async las ejecutan en el pool y la
  cancelación aborta la conexión (`Socket.Abort`).
- **`HttpClient`**: el paquete HTTP quita el `using System.Net.Http` implícito para que `HttpClient`/`HttpContent` no
  sean ambiguos (`ButterflyKeepSystemNetHttpUsing=true` lo conserva).

No incluido todavía: HTTP/2 y proxies HTTP, FTP activo, IMAP IDLE, MQTT 5, SSH/SFTP, servidores.

## Serialization: formatos

`Butterfly.Serialization.Core` describe los tipos **en tiempo de compilación** con un generador de código (sin
reflexión, compatible con AOT) que viaja dentro del paquete y llega también a quien lo recibe de forma transitiva.
Cada formato es un paquete:

| Paquete                                  | Formato                                                       |
|------------------------------------------|---------------------------------------------------------------|
| `Butterfly.Serialization.Json`           | JSON (`System.Text.Json` por debajo)                          |
| `Butterfly.Serialization.Xml`            | XML                                                           |
| `Butterfly.Serialization.Yaml`           | YAML                                                          |
| `Butterfly.Serialization.Csv`            | CSV (listas de filas planas; separador configurable)          |
| `Butterfly.Serialization.Protobuf`       | Protocol Buffers (`Timestamp`, `Duration`, `Value` de Google) |
| `Butterfly.Serialization.MessagePack`    | MessagePack                                                   |
| `Butterfly.Serialization.Cbor`           | CBOR                                                          |

```csharp
[SerializationContract]                      // opt-out (por defecto): se serializa todo salvo lo marcado
public sealed class Customer
{
    public int Id { get; init; }
    [Serialize(Profiles = ["Admin"])]        // solo en el perfil Admin
    public string? Email { get; init; }
    [DontSerialize("Public")]                // en todos menos Public
    public decimal CreditLimit { get; init; }
    [DontSerialize]                          // nunca
    public string Password { get; set; } = "";
    [Serialize(Name = "tag_list", Number = 20)]
    public List<string> Tags { get; init; } = [];
}

[SerializationContract(Mode = SerializationMode.OptIn)]   // solo lo marcado con [Serialize]
public sealed class Session { [Serialize] public string User { get; init; } = ""; public string Token { get; init; } = ""; }

var json  = ButterflySerializer.SerializeToString(customer, JsonFormat.Instance, new SerializationProfile("Admin") { Naming = NamingPolicy.SnakeCase });
var bytes = ButterflySerializer.Serialize(customer, ProtobufFormat.Instance);
var back  = ButterflySerializer.Deserialize<Customer>(bytes, ProtobufFormat.Instance);
```

- **Perfiles** (`SerializationProfile`): eligen miembros (`[Serialize(Profiles)]`, `[DontSerialize(perfiles)]`) y
  convenciones (nombres, enums como texto o número, nulos, valores por defecto, indentado, profundidad máxima,
  rechazo de miembros desconocidos). Un miembro oculto en un perfil **tampoco se lee** con ese perfil, así que un
  cliente no puede asignar lo que no ve.
- **Valores dinámicos**: `object`, `JsonNode`/`JsonElement` y `SerializationValue` se escriben en todos los formatos.
- Diagnósticos del generador: `CHRY001`–`CHRY006` (tipos no soportados, números o nombres repetidos, etc.).

## Chrysalis: servidor

Un servicio se escribe una vez, como interfaz, y se expone por varios protocolos a la vez. Los contratos usan
`Butterfly.Serialization` (mismos atributos y perfiles; `ChrysalisServerOptions.Profile`), y el generador crea
además clientes tipados.

| Paquete                          | Protocolo                                                                  |
|----------------------------------|----------------------------------------------------------------------------|
| `Butterfly.Chrysalis.Core`       | Servidor, middleware, estados (los de gRPC), clientes generados            |
| `Butterfly.Chrysalis.Http`       | Servidor HTTP propio: HTTP/1.1, HTTP/2 (h2c y TLS con ALPN), límites       |
| `Butterfly.Chrysalis.Rest`       | REST/JSON (`[HttpGet("ruta/{id}")]`…)                                      |
| `Butterfly.Chrysalis.Soap`       | SOAP 1.1/1.2 con WSDL generado                                             |
| `Butterfly.Chrysalis.JsonRpc`    | JSON-RPC 2.0 (lotes incluidos)                                             |
| `Butterfly.Chrysalis.XmlRpc`     | XML-RPC                                                                    |
| `Butterfly.Chrysalis.Grpc`       | gRPC sobre HTTP/2, con `.proto` generado                                   |
| `Butterfly.Chrysalis.Binary`     | RPC binario propio sobre sockets, multiplexado, con TLS                    |
| `Butterfly.Chrysalis.AspNetCore` | Los mismos protocolos dentro de ASP.NET Core, con inyección de dependencias |

```csharp
[ChrysalisService(Namespace = "demo.v1", Route = "greeter")]
public interface IGreeter { [HttpGet("hello/{name}")] Greeting Hello(string name); }

var chrysalis = new ChrysalisServer().Expose<IGreeter>(new Greeter());
var http = new HttpServer(options).MapRest(chrysalis).MapSoap(chrysalis).MapJsonRpc(chrysalis).MapGrpc(chrysalis);

// Cliente tipado sobre el protocolo binario
await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", port);
var greeter = connection.CreateClient<IGreeter>();
```

En ASP.NET Core:

```csharp
builder.Services.AddChrysalis(c => c.Expose<IGreeter, Greeter>());   // Scoped por defecto
app.UseChrysalisGrpc();
app.MapChrysalisRest().RequireAuthorization();
app.MapChrysalisSoap();
app.MapChrysalisJsonRpc();
// Servidores propios como IHostedService: AddChrysalisHttpServer(...) y AddChrysalisBinaryServer(...)
```

Servicios listos para exponer (`Butterfly.<Área>.Chrysalis`):

| Paquete                              | Ruta REST          | Servicio                                                         |
|--------------------------------------|--------------------|------------------------------------------------------------------|
| `Butterfly.Scripting.Chrysalis`      | `scripts`          | Ejecutar scripts (C#, Lua...) con el `IScriptInvoker` registrado  |
| `Butterfly.Workflows.Chrysalis`      | `workflows`        | Definiciones, instancias, eventos, transiciones, grafos y Mermaid |
| `Butterfly.SystemInfo.Chrysalis`     | `system`           | CPU, memoria y sistema operativo                                 |
| `Butterfly.Virtualization.Chrysalis` | `virtualization`   | Máquinas virtuales y checkpoints sobre el `IHypervisor` registrado |

Los errores de cada librería se traducen a estados (`NotFound`, `InvalidArgument`, `FailedPrecondition`...), que
cada protocolo convierte a lo suyo (código HTTP, fault SOAP, error JSON-RPC, estado gRPC).

## Extender

| Quiero...                    | Hago...                                                                                     |
|------------------------------|---------------------------------------------------------------------------------------------|
| Nuevo protocolo              | `src/Communication/Butterfly.Communication.<X>/` referenciando Core. Si es de conexión, heredar de `ProtocolClient` (TLS, credenciales, cierre) y leer con `Connection.Reader`. Tests en `tests/Communication/` usando `TestServer` (servidor falso con guion, ya incluido). |
| Nueva librería               | `src/<Área>/Butterfly.<Área>.<Módulo>/` con un csproj que solo tenga `Title` y `Description`, y añadirla a `Butterfly.slnx`. Se publica y entra en el catálogo de `butterfly` sola. |
| Tests de una librería        | `tests/<Área>/Butterfly.<Área>.<Módulo>.Tests/` con un csproj que solo tenga el `ProjectReference`. xUnit e `InternalsVisibleTo` son automáticos. |
| Paquete de terceros          | Versión en `Directory.Packages.props`, `PackageReference` sin versión en el csproj.           |
| Comando nuevo en la CLI      | Implementar `ICommand` en `tools/Butterfly.Tool/Commands/` y registrarlo en `Program.cs`.    |
| Ejemplo                      | Proyecto en `samples/` con `Sdk="Butterfly.Sdk/x.y.z"` y añadirlo a `samples/Samples.slnx`.  |
