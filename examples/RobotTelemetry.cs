// Realtime frame-track example: publish robot telemetry and subscribe to it
// through the ClutchCall relay.
//
// Run (in a console project that references ClutchCall.SDK, with the native
// engine on LD_LIBRARY_PATH and a relay reachable):
//
//   RELAY_URL=quic://relay.clutchcall.dev:4443 dotnet run
using ClutchCall.SDK;
using System;
using System.Threading;

internal static class RobotTelemetry
{
    private static void Main()
    {
        string url = Environment.GetEnvironmentVariable("RELAY_URL") ?? "quic://127.0.0.1:4443";
        string ns = "robot/turtlebot4-001", name = "odom";
        int recv = 0, badPrio = 0;

        // State callback: Connecting/Connected/Reconnecting/Closed/Failed.
        using var subc = MoqtClient.Connect(url, "", s => Console.WriteLine($"sub state {s}"));
        using var pubc = MoqtClient.Connect(url, "", s => Console.WriteLine($"pub state {s}"));

        // Subscribe first: the relay holds it until the publisher announces.
        using var sub = subc.SubscribeFrame(ns, name, (ts, priority, data) =>
        {
            Interlocked.Increment(ref recv);
            if (priority != 200) Interlocked.Increment(ref badPrio);
        });
        using var track = pubc.PublishFrame(ns, name, "ros.telemetry", "ros2/cdr", 128);

        for (int i = 0; i < 100; i++)
        {
            track.Write((ulong)(i * 1000), new byte[48], 200); // stand-in for a serialized message
            Thread.Sleep(100);                                  // 10 Hz
        }
        Thread.Sleep(1000);
        Console.WriteLine($"received {recv} frames; priority ok: {badPrio == 0}");
    }
}
