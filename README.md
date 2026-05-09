# ClutchCall .NET SDK

The official .NET wrapper for ClutchCall — telephony origination, media
streaming, and zero-trust JWT auth from C# / F# / VB.NET. Targets
`netstandard2.0` so it runs from .NET Framework 4.6.1+ through .NET 8.

## Install

```bash
dotnet add package ClutchCall.SDK
```

(Or build locally from this repo: `dotnet build ClutchCall.SDK/ClutchCall.SDK.csproj`.)

## Quick start

Set `CLUTCHCALL_CREDENTIALS` to point at your service-account JSON, then:

```csharp
using ClutchCall.SDK;

var client = new ClutchCallClient("pbx.clutchcall.com:443");

// Originate a call against an external trunk.
var resp = await client.OriginateAsync(
    to:    "+1234567890",
    aiWss: "wss://my-chatbot.com/media");

Console.WriteLine($"Call SID: {resp.CallSid}");
```

The native FFI core (`libclutchcall_core_ffi.{so,dylib,dll}`) is loaded via
`NativeMethods.cs`. Set `CLUTCHCALL_LIB_PATH` if it isn't on the default loader path.

## Project layout

- `ClutchCall.SDK/`        — the library project (the NuGet artifact).
- `ClutchCall.SDK.Tests/`  — xUnit test suite.
