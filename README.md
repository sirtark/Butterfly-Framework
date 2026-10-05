<div align="center">

<img src="eng/Butterfly-readme.png" alt="Butterfly" width="140" />

# Butterfly

**A modular framework for .NET, built from the socket up.**

Network clients, multi-protocol servers, reflection-free serialization, scripting, workflows
and host introspection, shipped as small NuGet packages that always move together.

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C# 14](https://img.shields.io/badge/C%23-14-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![Version](https://img.shields.io/badge/version-0.0.1-4A6CF0)](#versioning)
[![AOT](https://img.shields.io/badge/Native%20AOT-ready-2F3699)](#design-principles)
[![Platforms](https://img.shields.io/badge/platforms-Windows%20%7C%20Linux-lightgrey)](#)

[Packages](#-packages) •
[Quick start](#-quick-start) •
[Highlights](#-highlights) •
[Building](#-building-from-source) •
[CLI](#-the-butterfly-cli) •
[Contributing](#-extending-butterfly)

</div>

---

## ✨ Why Butterfly

- **One version for everything.** `Butterfly.Sdk` aligns every `Butterfly.*` reference to the same framework version, so packages never drift apart.
- **Own the stack.** Sockets, HTTP/1.1 and HTTP/2, HPACK, TLS negotiation, DNS, SMTP, IMAP, MQTT: implemented here, not wrapped.
- **No reflection at runtime.** Contracts are described at compile time by a source generator, ready for trimming and Native AOT.
- **Write once, expose everywhere.** A Chrysalis service is an interface; REST, SOAP, gRPC, JSON-RPC, XML-RPC and a binary protocol come for free.
- **Pay for what you use.** Every area is split into focused packages: reference `Butterfly.Serialization.Json` without pulling in YAML or Protobuf.

## 📦 Packages

| Area | Packages | What you get |
|---|---|---|
| 🔌 **Networking** | `Networking.Sockets` | A socket layer with OS-level timeouts, TLS and abortable I/O |
| 📡 **Communication** | `Core` · `Dns` · `Http` · `WebSockets` · `Mail` · `Smtp` · `Pop3` · `Imap` · `Ftp` · `Ntp` · `Mqtt` | Protocol clients with TLS by default and secure authentication rules |
| 🧬 **Serialization** | `Core` · `Json` · `Xml` · `Yaml` · `Csv` · `Protobuf` · `MessagePack` · `Cbor` | Source-generated contracts, opt-in/opt-out members and profiles |
| 🦋 **Chrysalis** | `Core` · `Http` · `Rest` · `Soap` · `JsonRpc` · `XmlRpc` · `Grpc` · `Binary` · `AspNetCore` | A multi-protocol RPC server with typed clients and ASP.NET Core integration |
| 📜 **Scripting** | `Core` · `CSharp` · `Lua` · `Lua.MoonSharp` · `Lua.NLua` · `AspNetCore` · `Chrysalis` | Sandboxed C# and Lua execution with timeouts, validation and telemetry |
| 🔀 **Workflows** | `Core` · `Scripting` · `AspNetCore` · `Chrysalis` | A JSON-defined workflow engine: branches, events, validations, jumps, Mermaid graphs |
| 🖥️ **SystemInfo** | `Core` · `CPU` · `Memory` · `OS` · `Chrysalis` | Real hardware and OS detection: CPU features, virtualization support, memory, OS version |
| ☁️ **Virtualization** | `Core` · `HyperV` · `Libvirt` · `Chrysalis` | Virtual machine lifecycle and checkpoints over Hyper-V and libvirt/KVM |
| 🧩 **DesignPatterns** | `Creational` · `Structural` · `Behavioral` · `Pipelines` · `StateMachines` | Shared abstractions for the classic patterns |
| 🛠️ **Tooling** | `Butterfly.Sdk` · `Butterfly.Tool` | The MSBuild SDK and the `butterfly` command-line tool |

> [!TIP]
> Every package id starts with `Butterfly.`, so the full name of `Serialization.Json` is `Butterfly.Serialization.Json`.

## 🚀 Quick start

Point a project at `Butterfly.Sdk` and add packages **without versions**. The SDK supplies them:

```xml
<Project Sdk="Butterfly.Sdk/0.0.1">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Butterfly.Serialization.Json" />
    <PackageReference Include="Butterfly.Chrysalis.Rest" />
  </ItemGroup>

</Project>
```

Already on another SDK (Web, Worker...)? Add Butterfly on top:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <Sdk Name="Butterfly.Sdk" Version="0.0.1" />
</Project>
```

Or let the CLI do it for you:

```bash
butterfly init
```

```bash
butterfly add Serialization.Json Chrysalis.Rest
```

## 🌟 Highlights

### 🧬 Serialization: one contract, seven formats

```csharp
[SerializationContract]                         // opt-out: everything is serialized unless marked
public sealed class Customer
{
    public int Id { get; init; }

    [Serialize(Profiles = ["Admin"])]           // only in the Admin profile
    public string? Email { get; init; }

    [DontSerialize("Public")]                   // everywhere except the Public profile
    public decimal CreditLimit { get; init; }

    [DontSerialize]                             // never
    public string Password { get; set; } = "";

    [Serialize(Name = "tag_list", Number = 20)] // wire name and Protobuf field number
    public List<string> Tags { get; init; } = [];
}

var admin = new SerializationProfile("Admin") { Naming = NamingPolicy.SnakeCase, Indented = true };

string json  = ButterflySerializer.SerializeToString(customer, JsonFormat.Instance, admin);
byte[] proto = ButterflySerializer.Serialize(customer, ProtobufFormat.Instance);
Customer copy = ButterflySerializer.Deserialize<Customer>(proto, ProtobufFormat.Instance);
```

- **Profiles** choose members *and* conventions: naming, enums as names or numbers, nulls, defaults, indentation, depth limits, unknown members.
- **Hidden means hidden both ways.** A member a profile cannot see is not read either, which blocks mass-assignment.
- **Opt-in contracts** (`Mode = SerializationMode.OptIn`) only carry what is marked with `[Serialize]`.
- **Dynamic values** (`object`, `JsonNode`, `JsonElement`, `SerializationValue`) work in every format.
- **Interoperable:** Protobuf uses Google's `Timestamp`, `Duration` and `Value`, and the output reads with `Google.Protobuf`.

### 🦋 Chrysalis: write a service once, serve it six ways

```csharp
[ChrysalisService(Namespace = "demo.v1", Route = "greeter")]
public interface IGreeter
{
    [HttpGet("hello/{name}")]
    Greeting Hello(string name);
}

var chrysalis = new ChrysalisServer().Expose<IGreeter>(new Greeter());

// REST, SOAP (with WSDL), JSON-RPC, XML-RPC and gRPC on a single port, on Butterfly's own HTTP/1.1 + HTTP/2 server
var http = new HttpServer(options)
    .MapRest(chrysalis, "/api")
    .MapSoap(chrysalis, "/soap")
    .MapJsonRpc(chrysalis, "/rpc")
    .MapXmlRpc(chrysalis, "/xmlrpc")
    .MapGrpc(chrysalis);

// ...or over the multiplexed binary protocol, with a generated typed client
await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", port);
IGreeter greeter = connection.CreateClient<IGreeter>();
```

Prefer ASP.NET Core? The same services plug into dependency injection, authorization and Kestrel:

```csharp
builder.Services.AddChrysalis(c => c.Expose<IGreeter, Greeter>());

app.UseChrysalisGrpc();
app.MapChrysalisRest().RequireAuthorization();
app.MapChrysalisSoap();
app.MapChrysalisJsonRpc();
```

**Ready-made services.** Scripting, Workflows, SystemInfo and Virtualization each ship a `*.Chrysalis` package. Library errors map to statuses such as `NotFound`, `InvalidArgument` and `FailedPrecondition`, and each protocol translates them into its own form: HTTP codes, SOAP faults, JSON-RPC errors or gRPC statuses.

### 📡 Communication: secure by default

```csharp
using var smtp = new SmtpClient();
smtp.Connect("smtp.example.com");            // port 587, STARTTLS required
smtp.Authenticate("user", "password");
smtp.Send(new MailMessage
{
    From = "me@example.com",
    To = { "you@example.com" },
    Subject = "Hello",
    TextBody = "Sent with Butterfly"
});
```

| Client | Protocol | Ports (TLS) |
|---|---|---|
| `Dns` | A, AAAA, CNAME, MX, TXT, NS, PTR, SRV, SOA, CAA over UDP + TCP, EDNS | 53 |
| `Http` | HTTP/1.1: keep-alive, chunked, gzip/deflate/br, redirects, cookies, forms, multipart | 80 (443) |
| `WebSockets` | `ws://` and `wss://` | 80 (443) |
| `Mail` | MIME messages: text, HTML, attachments, inline images, encoded headers | |
| `Smtp` | STARTTLS, PLAIN / LOGIN / CRAM-MD5 / XOAUTH2 | 587, 25 (465) |
| `Pop3` | STLS, SASL, UIDL, TOP | 110 (995) |
| `Imap` | Folders, search, fetch, flags, move, append, XOAUTH2 | 143 (993) |
| `Ftp` | FTP and FTPS (explicit and implicit, encrypted data channel), MLSD/LIST, resume | 21 (990) |
| `Ntp` | SNTP: clock offset and round-trip delay | 123 |
| `Mqtt` | MQTT 3.1.1: QoS 0/1/2, retained messages, wills, persistent sessions | 1883 (8883) |

<details>
<summary><b>Rules every client follows</b></summary>

- **TLS by default** (`TlsMode.Auto`): implicit TLS on the secure port and **mandatory** STARTTLS elsewhere. If the server does not offer it, the connection fails instead of continuing in clear text. Use `TlsMode.StartTlsWhenAvailable` or `None` to opt out. Certificates are always validated unless `TlsOptions` says otherwise.
- **No clear-text passwords.** Authenticating without TLS to a remote host throws unless `AllowInsecureAuthentication = true`. Loopback is allowed.
- **Timeouts everywhere.** `ConnectionOptions` sets the connect, read and write timeouts, and the sockets enforce them at the OS level.
- **Name resolution** goes literal IP → `localhost` → hosts file → Butterfly DNS → system resolver, which covers search domains, mDNS and VPNs.
- **Sync + `…Async`.** Operations block. The async versions run on the pool, and cancelling aborts the connection.
- **`HttpClient` without ambiguity.** The HTTP package removes the implicit `using System.Net.Http`. Set `ButterflyKeepSystemNetHttpUsing=true` to keep it.

</details>

### 📜 Scripting, 🔀 Workflows, 🖥️ SystemInfo, ☁️ Virtualization

- **Scripting** runs C# (Roslyn) and Lua (MoonSharp or NLua) behind one `IScriptInvoker`, with parameters, timeouts, validation, logging and telemetry.
- **Workflows** runs processes defined in JSON: branches, wait nodes, event and manual transitions, validation pipelines, jumps, script steps, and graphs rendered as Mermaid.
- **SystemInfo** detects the CPU (vendor, model, cores and packages, instruction-set features, virtualization support, hypervisor and VM detection), memory and operating system on Windows and Linux.
- **Virtualization** creates, starts, pauses, saves and checkpoints virtual machines on Hyper-V and libvirt/KVM, and checks whether the host is ready to run them.

## 🏗️ Building from source

```powershell
./build.ps1              # Pack: build everything and publish to Packages/Release/Butterfly
./build.ps1 Test         # run the test suites
./build.ps1 Samples      # build the samples against the local feed
./build.ps1 InstallTool  # install or reinstall the global "butterfly" tool
./build.ps1 Clean        # remove this version from the feed and the NuGet cache
./build.ps1 All          # Pack + Test + InstallTool + Samples
```

> [!IMPORTANT]
> Do not delete the `Packages/Release/Butterfly` folder by hand. It is registered as a NuGet source, and without it every restore on the machine fails with `NU1301`. Use `./build.ps1 Clean` to start fresh.

Building in `Release`, from Visual Studio too, publishes the packages and evicts that version from `~/.nuget/packages`, so consumers pick up changes even when the version number has not moved. Pass `-p:ButterflyEvictPackageCache=false` to keep the cache.

<details>
<summary><b>Repository layout</b></summary>

```
Butterfly.slnx               Single solution with the whole framework (src, tests, tools)
Directory.Build.props        Common settings: version, signing, packaging, AOT
Directory.Build.targets      Conventions: InternalsVisibleTo, NuGet cache eviction, catalog
Directory.Packages.props     Central versions of third-party packages
NuGet.config                 Local feed "Butterfly" -> Packages/Release/Butterfly
build.ps1                    Build / Test / Pack / Clean / InstallTool / Samples / All
eng/                         Logo and strong-name key
src/
  Butterfly.Sdk/             MSBuild SDK; Sdk/Butterfly.Version.props is the ONLY source of the version
  <Area>/Butterfly.<Area>.<Module>/
tests/
  <Area>/Butterfly.<Area>.<Module>.Tests/
tools/
  Butterfly.Tool/            The "butterfly" CLI
samples/                     Package consumers, isolated from the framework build
```

</details>

<details>
<summary><b>Framework vs. consumers</b></summary>

The framework builds **from source only**. The packages in the feed are for consumers.

| Project | SDK | Butterfly dependencies |
|---|---|---|
| Framework (`src/`, `tests/`, `tools/`) | `Microsoft.NET.Sdk` | `ProjectReference` |
| Consumer (`samples/`, other solutions) | `Butterfly.Sdk/x.y.z` | `PackageReference` without a version |

If a framework project used `Butterfly.Sdk` or a `Butterfly.*` package, it would depend on its own output and would only build while the feed held an older build. The build stops that with an explanatory error, and `butterfly add` and `butterfly init` refuse to touch framework projects.

</details>

### Versioning

The version lives in exactly one place: `src/Butterfly.Sdk/Sdk/Butterfly.Version.props`. Every library, `Butterfly.Sdk` and `Butterfly.Tool` ship with it. A reference with an explicit `Version` (or `VersionOverride` under central package management) is pinned and the SDK leaves it alone.

## 🧰 The `butterfly` CLI

The CLI always installs packages at the framework version it was built with.

```
butterfly list                     Available packages (* = referenced by the project)
butterfly add Networking.Sockets   Add packages (short or full name)
butterfly remove Networking.Sockets
butterfly init                     Move the project to Butterfly.Sdk (or update its version)
butterfly version
```

Common options are `-p|--project <path>` and `--no-restore`. If the restore fails, the changes are rolled back. Depending on the project, `add` writes:

- with **Butterfly.Sdk**: a reference without a version, which the SDK fills in;
- with **central package management**: a `PackageVersion` at the framework version;
- otherwise: `Version="<framework version>"` on the reference.

After bumping the framework version, run `./build.ps1 All`, then `butterfly init` in each consumer project, including those in `samples/`.

## 🧭 Design principles

- **Compile-time over runtime.** Source generators instead of reflection, `IsAotCompatible` on every library.
- **Secure defaults.** TLS on, certificates validated, no clear-text credentials, serialization profiles that hide members in both directions.
- **Small packages, one version.** Areas split into modules, all released together through `Butterfly.Sdk`.
- **Tested against the real thing.** Protocol tests run against scripted fake servers, real Kestrel or `Grpc.Net.Client`, and the reference libraries of each format.

## 🤝 Extending Butterfly

| I want to... | Do this |
|---|---|
| Add a library | Create `src/<Area>/Butterfly.<Area>.<Module>/` with a csproj holding only `Title` and `Description`, then add it to `Butterfly.slnx`. It is packed and listed by `butterfly` automatically. |
| Test it | Create `tests/<Area>/Butterfly.<Area>.<Module>.Tests/` with just the `ProjectReference`. xUnit and `InternalsVisibleTo` are wired up for you. |
| Add a protocol client | Use `src/Communication/Butterfly.Communication.<X>/` referencing `Core`. Connection-based clients derive from `ProtocolClient` (TLS, credentials, shutdown) and read through `Connection.Reader`. Test them with `TestServer`, the scripted fake server in `tests/Communication/`. |
| Expose a library over Chrysalis | Add `Butterfly.<Area>.Chrysalis` with a `[ChrysalisService]` interface and translate the library's exceptions into `ChrysalisException` statuses. |
| Use a third-party package | Put its version in `Directory.Packages.props` and reference it without a version. |
| Add a CLI command | Implement `ICommand` in `tools/Butterfly.Tool/Commands/` and register it in `Program.cs`. |
| Add a sample | Create a project in `samples/` with `Sdk="Butterfly.Sdk/x.y.z"` and add it to `samples/Samples.slnx`. |

---

<div align="center">

<img src="eng/Butterfly-readme.png" alt="" width="32" />

Made by **Antril Organization** · [antril.org/Butterfly](https://www.antril.org/Butterfly/)

</div>
