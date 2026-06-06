// Games modality — multiplayer rooms over MoQT.
//
// Three channels per room (state, input, event) mapped onto the MoQT
// substrate with the right priority + QUIC lane per channel. Namespaces
// baked in; input + event frames carry a 1-byte from-header so the
// server's single subscribe callback can sort frames by player.

using System;
using System.Text;

namespace ClutchCall.SDK.Games
{
    public class GamesException : Exception { public GamesException(string m) : base(m) {} }

    public static class Wire
    {
        public const int FromHeaderBytes = 1;
        public const int MaxFromLen      = 0xFF;

        public static byte[] EncodeWithFrom(string fromPlayerId, byte[] payload)
        {
            var fromBytes = Encoding.UTF8.GetBytes(fromPlayerId);
            if (fromBytes.Length > MaxFromLen)
                throw new GamesException($"from_player_id > 255 ({fromBytes.Length})");
            var buf = new byte[1 + fromBytes.Length + payload.Length];
            buf[0] = (byte)fromBytes.Length;
            Buffer.BlockCopy(fromBytes, 0, buf, 1, fromBytes.Length);
            Buffer.BlockCopy(payload,   0, buf, 1 + fromBytes.Length, payload.Length);
            return buf;
        }

        public static (string from, byte[] payload) DecodeWithFrom(byte[] buf)
        {
            if (buf.Length < 1) throw new GamesException("frame too short");
            int n = buf[0];
            if (buf.Length < 1 + n) throw new GamesException($"truncated (from_len={n})");
            var from = Encoding.UTF8.GetString(buf, 1, n);
            var payload = new byte[buf.Length - 1 - n];
            Buffer.BlockCopy(buf, 1 + n, payload, 0, payload.Length);
            return (from, payload);
        }
    }

    public sealed class StatePublisher : IDisposable
    {
        private readonly FramePublication _track;
        internal StatePublisher(FramePublication t) { _track = t; }
        public void Write(byte[] stateBytes)
        {
            var ts = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
            _track.Write(ts, stateBytes, 100);
        }
        public void Dispose() => _track?.Dispose();
    }

    public sealed class FromPublisher : IDisposable
    {
        private readonly FramePublication _track;
        private readonly string _from;
        private readonly byte   _defaultPriority;
        internal FromPublisher(FramePublication t, string from, byte defaultPriority)
        { _track = t; _from = from; _defaultPriority = defaultPriority; }

        public void Write(byte[] payload)
        {
            var frame = Wire.EncodeWithFrom(_from, payload);
            var ts = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
            _track.Write(ts, frame, _defaultPriority);
        }
        public void Dispose() => _track?.Dispose();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly FrameSubscription _sub;
        internal Subscription(FrameSubscription s) { _sub = s; }
        public void Dispose() => _sub?.Dispose();
    }

    public sealed class Games : IDisposable
    {
        private MoqtClient? _client;
        public string  RelayHost { get; set; } = "relay.clutchcall.dev";
        public string  Token     { get; }
        public string  RoomId    { get; }
        public string? PlayerId  { get; }

        public Games(string token, string roomId, string? playerId = null)
        {
            if (string.IsNullOrEmpty(token))  throw new GamesException("token required");
            if (string.IsNullOrEmpty(roomId)) throw new GamesException("roomId required");
            Token = token; RoomId = roomId; PlayerId = playerId;
        }

        public string StateNs => $"game/{RoomId}/state";
        public string InputNs => $"game/{RoomId}/input";
        public string EventNs(string channel) => $"game/{RoomId}/event/{Uri.EscapeDataString(channel)}";

        private MoqtClient Ensure()
        {
            if (_client != null) return _client;
            var pidSeg = string.IsNullOrEmpty(PlayerId)
                ? "/_authority"
                : $"/{Uri.EscapeDataString(PlayerId!)}";
            var url = $"moq://{RelayHost}/games/{Uri.EscapeDataString(RoomId)}{pidSeg}";
            _client = MoqtClient.Connect(url, Token, _ => { });
            return _client;
        }

        public StatePublisher PublishState(int? tickHz = null)
        {
            var c = Ensure();
            var tag = tickHz.HasValue ? $"game/state;tickHz={tickHz.Value}" : "game/state";
            var t = c.PublishFrame(StateNs, "tick", "game.state", tag, 100);
            return new StatePublisher(t);
        }

        public Subscription SubscribeState(Action<byte[]> onState)
        {
            var c = Ensure();
            var s = c.SubscribeFrame(StateNs, "tick", (ts, prio, data) => onState(data));
            return new Subscription(s);
        }

        public FromPublisher PublishInput()
        {
            if (string.IsNullOrEmpty(PlayerId))
                throw new GamesException("PublishInput: PlayerId required — only players can publish input");
            var c = Ensure();
            var t = c.PublishFrame(InputNs, "frame", "game.input", "game/input", 100);
            return new FromPublisher(t, PlayerId!, 100);
        }

        public Subscription SubscribeInputs(Action<string, byte[]> onInput)
        {
            var c = Ensure();
            var s = c.SubscribeFrame(InputNs, "frame", (ts, prio, data) => {
                try {
                    var (from, payload) = Wire.DecodeWithFrom(data);
                    onInput(from, payload);
                } catch (GamesException) { /* drop malformed */ }
            });
            return new Subscription(s);
        }

        public FromPublisher PublishEvent(string channel)
        {
            if (string.IsNullOrEmpty(channel)) throw new GamesException("channel required");
            var from = string.IsNullOrEmpty(PlayerId) ? "_authority" : PlayerId!;
            var c = Ensure();
            var t = c.PublishFrame(EventNs(channel), "msg",
                "game.event", $"game/event;channel={channel}", 50);
            return new FromPublisher(t, from, 50);
        }

        public Subscription SubscribeEvents(string channel, Action<string, byte[]> onEvent)
        {
            if (string.IsNullOrEmpty(channel)) throw new GamesException("channel required");
            var c = Ensure();
            var ns = EventNs(channel);
            var s = c.SubscribeFrame(ns, "msg", (ts, prio, data) => {
                try {
                    var (from, payload) = Wire.DecodeWithFrom(data);
                    onEvent(from, payload);
                } catch (GamesException) { /* drop malformed */ }
            });
            return new Subscription(s);
        }

        public void Dispose() { _client?.Dispose(); _client = null; }
    }
}
