// Robotics modality — typed pub/sub for a robot fleet over MoQT.
//
// Bidirectional teleop convention baked in: telemetry on `robot/<id>`,
// commands on `robot/<id>/ctl`. The wire adds a u16 BE type-name prefix
// so cross-language subscribers pick the right deserializer.

using System;
using System.Buffers.Binary;
using System.Text;

namespace ClutchCall.SDK.Robotics
{
    public class RoboticsException : Exception { public RoboticsException(string m) : base(m) {} }

    // ── wire ────────────────────────────────────────────────────────────

    public static class Wire
    {
        public const int HeaderBytes = 2;
        public const int MaxTypeName = 0xFFFF;

        public static byte[] EncodeFrame(string typeName, byte[] payload)
        {
            if (string.IsNullOrEmpty(typeName)) throw new RoboticsException("typeName required");
            var typeBytes = Encoding.UTF8.GetBytes(typeName);
            if (typeBytes.Length > MaxTypeName)
                throw new RoboticsException($"typeName > 65535 ({typeBytes.Length})");
            var buf = new byte[HeaderBytes + typeBytes.Length + payload.Length];
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), (ushort)typeBytes.Length);
            Buffer.BlockCopy(typeBytes, 0, buf, HeaderBytes, typeBytes.Length);
            Buffer.BlockCopy(payload,   0, buf, HeaderBytes + typeBytes.Length, payload.Length);
            return buf;
        }

        public static (string typeName, byte[] payload) DecodeFrame(byte[] buf)
        {
            if (buf.Length < HeaderBytes) throw new RoboticsException("frame too short");
            var n = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(0, 2));
            var end = HeaderBytes + n;
            if (buf.Length < end) throw new RoboticsException($"truncated (type_name_len={n})");
            var name = Encoding.UTF8.GetString(buf, HeaderBytes, n);
            var payload = new byte[buf.Length - end];
            Buffer.BlockCopy(buf, end, payload, 0, payload.Length);
            return (name, payload);
        }
    }

    // ── QoS ─────────────────────────────────────────────────────────────

    public enum Reliability { BestEffort, Reliable }
    public enum Durability { Volatile, TransientLocal }

    public sealed class QoSProfile
    {
        public Reliability Reliability { get; set; } = Reliability.BestEffort;
        public Durability  Durability  { get; set; } = Durability.Volatile;
        public int Depth                            { get; set; } = 10;

        internal string Capability =>
            (Durability, Reliability) switch
            {
                (Durability.TransientLocal, Reliability.Reliable)   => "ros.tl_reliable",
                (Durability.TransientLocal, Reliability.BestEffort) => "ros.tl_be",
                (_, Reliability.Reliable)                           => "ros.reliable",
                _                                                   => "ros.best_effort",
            };

        internal byte DefaultPriority => Reliability == Reliability.Reliable ? (byte)50 : (byte)100;
    }

    // ── handles ─────────────────────────────────────────────────────────

    public sealed class Publication : IDisposable
    {
        private readonly FramePublication _track;
        private readonly string _typeName;
        private readonly byte   _defaultPriority;

        internal Publication(FramePublication track, string typeName, byte defaultPriority)
        { _track = track; _typeName = typeName; _defaultPriority = defaultPriority; }

        public void Write(byte[] payload, byte? priority = null)
        {
            var frame = Wire.EncodeFrame(_typeName, payload);
            var ts = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
            _track.Write(ts, frame, priority ?? _defaultPriority);
        }

        public void Dispose() => _track?.Dispose();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly FrameSubscription _sub;
        public string NS   { get; }
        public string Name { get; }
        internal Subscription(FrameSubscription s, string ns, string name) { _sub = s; NS = ns; Name = name; }
        public void Dispose() => _sub?.Dispose();
    }

    // ── client ──────────────────────────────────────────────────────────

    public sealed class Robotics : IDisposable
    {
        private MoqtClient? _client;
        public string RelayHost { get; set; } = "relay.clutchcall.dev";
        public string Token     { get; }
        public string RobotId   { get; }

        public Robotics(string token, string robotId)
        {
            if (string.IsNullOrEmpty(token))   throw new RoboticsException("token required");
            if (string.IsNullOrEmpty(robotId)) throw new RoboticsException("robotId required");
            Token = token; RobotId = robotId;
        }

        public string TelemetryNs => $"robot/{RobotId}";
        public string CommandNs   => $"robot/{RobotId}/ctl";

        private MoqtClient Ensure()
        {
            if (_client != null) return _client;
            var url = $"moq://{RelayHost}/robotics/{Uri.EscapeDataString(RobotId)}";
            _client = MoqtClient.Connect(url, Token, _ => { });
            return _client;
        }

        public Publication PublishTelemetry(string topic, string typeName, QoSProfile? qos = null) =>
            Publish(TelemetryNs, topic, typeName, qos ?? new QoSProfile());

        public Subscription SubscribeTelemetry(string topic, Action<string, byte[]> onMessage) =>
            Subscribe(TelemetryNs, topic, onMessage);

        public Publication PublishCommand(string topic, string typeName, QoSProfile? qos = null) =>
            Publish(CommandNs, topic, typeName, qos ?? new QoSProfile());

        public Subscription SubscribeCommand(string topic, Action<string, byte[]> onMessage) =>
            Subscribe(CommandNs, topic, onMessage);

        public void Dispose() { _client?.Dispose(); _client = null; }

        private Publication Publish(string ns, string name, string typeName, QoSProfile qos)
        {
            var c = Ensure();
            var track = c.PublishFrame(ns, name, qos.Capability, $"ros2/cdr;type={typeName}", 0);
            return new Publication(track, typeName, qos.DefaultPriority);
        }

        private Subscription Subscribe(string ns, string name, Action<string, byte[]> onMessage)
        {
            var c = Ensure();
            var sub = c.SubscribeFrame(ns, name, (ts, prio, data) => {
                try {
                    var (tn, payload) = Wire.DecodeFrame(data);
                    onMessage(tn, payload);
                } catch (RoboticsException) { /* drop malformed */ }
            });
            return new Subscription(sub, ns, name);
        }
    }
}
