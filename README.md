# ClutchCall .NET SDK

The official .NET SDK for ClutchCall. **Modality-oriented**: each modality
is its own namespace under `ClutchCall.SDK.*`, all riding the same MoQT
substrate underneath. Targets `netstandard2.0` so it runs from .NET
Framework 4.6.1+ through .NET 8.

| Namespace                       | Modality                                            | Status |
| ------------------------------- | --------------------------------------------------- | ------ |
| `ClutchCall.SDK.Streams`        | Live broadcasts + signed playback URLs              | **GA** |
| `ClutchCall.SDK.Robotics`       | Robotics topic pub/sub (ROS 2 CDR)                  | **GA** |
| `ClutchCall.SDK.Games`          | Games (rooms, state/input/event channels)           | **GA** |
| `ClutchCall.SDK.Data`           | MQTT-style typed pub/sub (`+` / `#` filters)        | **GA** |
| `ClutchCall.SDK.Voice`          | Voice (calls + bidirectional audio bridge)          | **GA** |
| `ClutchCall.SDK` (root)         | Legacy voice surface (`ClutchCallClient`) — kept for backwards compat | legacy |

## Install

```bash
dotnet add package ClutchCall.SDK
```

## Streams — watch a live broadcast

```csharp
using ClutchCall.SDK.Streams;

using var s   = new Streams("https://app.clutchcall.dev", apiKey, "org_abc");
var inp       = await s.LiveInputs.GetAsync("li_xyz");
var ticket    = await inp.SignedPlaybackUrlAsync(3600);

using var viewer = BroadcastViewer.Open(ticket.Url,
    (isInit, chunk) => { /* feed chunk.Data to MSE / file */ });
```

## Robotics — telemetry + commands

```csharp
using ClutchCall.SDK.Robotics;

using var r = new Robotics(token, "turtlebot-7");
var odom    = r.PublishTelemetry("odom", "nav_msgs/msg/Odometry",
    new QoSProfile { Reliability = Reliability.Reliable });
odom.Write(cdrBytes);
```

## Games — multiplayer rooms

```csharp
using ClutchCall.SDK.Games;

// Authoritative server (no playerId)
using var auth = new Games(token, "duel-42");
using var state = auth.PublishState(tickHz: 30);
using var sub   = auth.SubscribeInputs((pid, bytes) => { /* apply */ });
```

## Data — MQTT-style pub/sub

```csharp
using ClutchCall.SDK.Data;

using var d = new Data(token, "device-7");
d.Publish("sensors/room1/temperature", Encoding.UTF8.GetBytes("23.5"));
using var sub = d.Subscribe("sensors/+/temperature", msg => {
    Console.WriteLine($"{msg.Topic} ← {msg.FromClientId}: {Encoding.UTF8.GetString(msg.Payload)}");
});
```

## Voice — calls + audio bridge

```csharp
using ClutchCall.SDK.Voice;

using var v = new Voice("https://app.clutchcall.dev", apiKey, "org_abc");
var call    = await v.Calls.OriginateAsync(new OriginateArgs {
    To = "+15551234567", From = "+15558675309",
    TrunkId = "trunk_main", Agent = "healthcare-assistant",
});

using var bridge = v.AudioBridge.Attach(call.Sid, new AudioBridgeOpts {
    Codec = Codec.Opus,
    OnUplink = (frame, tsUs) => asr.Feed(frame),
});
// later
await call.HangupAsync();
```

### Legacy voice surface

`ClutchCall.SDK.ClutchCallClient` remains available for backwards compat.
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
