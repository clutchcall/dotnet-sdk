// Data modality — MQTT-style typed pub/sub over MoQT.
//
// Hierarchical topics with `+` / `#` wildcards (top-level segment must be
// concrete). The frame header carries the full topic + the publisher's
// client id so subscribers MQTT-filter and attribute without out-of-band
// lookup.

using System;
using System.Collections.Generic;
using System.Text;

namespace ClutchCall.SDK.Data
{
    public class DataException : Exception { public DataException(string m) : base(m) {} }

    public static class Wire
    {
        public const int FromLenBytes  = 1;
        public const int TopicLenBytes = 1;
        public const int MaxFromLen    = 0xFF;
        public const int MaxTopicLen   = 0xFF;

        public static byte[] EncodeDataFrame(string fromClientId, string topic, byte[] payload)
        {
            var fromBytes  = Encoding.UTF8.GetBytes(fromClientId);
            var topicBytes = Encoding.UTF8.GetBytes(topic);
            if (fromBytes.Length  > MaxFromLen)  throw new DataException($"from_client_id > 255 ({fromBytes.Length})");
            if (topicBytes.Length > MaxTopicLen) throw new DataException($"topic > 255 ({topicBytes.Length})");
            var buf = new byte[1 + fromBytes.Length + 1 + topicBytes.Length + payload.Length];
            int o = 0;
            buf[o++] = (byte)fromBytes.Length;
            Buffer.BlockCopy(fromBytes,  0, buf, o, fromBytes.Length);  o += fromBytes.Length;
            buf[o++] = (byte)topicBytes.Length;
            Buffer.BlockCopy(topicBytes, 0, buf, o, topicBytes.Length); o += topicBytes.Length;
            Buffer.BlockCopy(payload,    0, buf, o, payload.Length);
            return buf;
        }

        public static (string from, string topic, byte[] payload) DecodeDataFrame(byte[] buf)
        {
            if (buf.Length < 1) throw new DataException("frame too short");
            int fromLen = buf[0]; int pos = 1;
            if (buf.Length < pos + fromLen + 1) throw new DataException("truncated (from + topic_len)");
            var from = Encoding.UTF8.GetString(buf, pos, fromLen); pos += fromLen;
            int topicLen = buf[pos]; pos++;
            if (buf.Length < pos + topicLen) throw new DataException("truncated (topic)");
            var topic = Encoding.UTF8.GetString(buf, pos, topicLen); pos += topicLen;
            var payload = new byte[buf.Length - pos];
            Buffer.BlockCopy(buf, pos, payload, 0, payload.Length);
            return (from, topic, payload);
        }

        public static bool TopicMatches(string topic, string filter)
        {
            if (topic == filter) return true;
            var t = topic.Split('/');
            var f = filter.Split('/');
            for (int i = 0; i < f.Length; i++)
            {
                if (f[i] == "#") return i == f.Length - 1;
                if (i >= t.Length) return false;
                if (f[i] == "+") continue;
                if (f[i] != t[i]) return false;
            }
            return t.Length == f.Length;
        }

        public static string TopLevelSegment(string filterOrTopic)
        {
            var head = filterOrTopic.Split(new[] { '/' }, 2)[0];
            if (head == "+" || head == "#")
                throw new DataException($"top-level wildcard not supported ({filterOrTopic})");
            if (string.IsNullOrEmpty(head)) throw new DataException("empty topic / filter");
            return head;
        }
    }

    public sealed class Message
    {
        public string Topic        { get; set; } = "";
        public string FromClientId { get; set; } = "";
        public byte[] Payload      { get; set; } = Array.Empty<byte>();
        public bool   Retained     { get; set; }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly FrameSubscription _sub;
        public string NS          { get; }
        public string TopicFilter { get; }
        internal Subscription(FrameSubscription s, string ns, string topicFilter)
        { _sub = s; NS = ns; TopicFilter = topicFilter; }
        public void Dispose() => _sub?.Dispose();
    }

    public sealed class Data : IDisposable
    {
        private MoqtClient? _client;
        private readonly Dictionary<string, FramePublication> _pubs = new();

        public string RelayHost { get; set; } = "relay.clutchcall.dev";
        public string Token     { get; }
        public string ClientId  { get; }

        public Data(string token, string clientId)
        {
            if (string.IsNullOrEmpty(token))    throw new DataException("token required");
            if (string.IsNullOrEmpty(clientId)) throw new DataException("clientId required");
            Token = token; ClientId = clientId;
        }

        private MoqtClient Ensure()
        {
            if (_client != null) return _client;
            var url = $"moq://{RelayHost}/data/{Uri.EscapeDataString(ClientId)}";
            _client = MoqtClient.Connect(url, Token, _ => { });
            return _client;
        }

        public void Publish(string topic, byte[] payload, bool reliable = false, bool retained = false)
        {
            var top = Wire.TopLevelSegment(topic);
            if (!_pubs.TryGetValue(top, out var p))
            {
                var c = Ensure();
                p = c.PublishFrame($"data/{top}", "msg", "data.pubsub", $"data;top={top}", 100);
                _pubs[top] = p;
            }
            var frame = Wire.EncodeDataFrame(ClientId, topic, payload);
            byte priority = (byte)(reliable || retained ? 30 : 100);
            var ts = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
            p.Write(ts, frame, priority);
        }

        public Subscription Subscribe(string topicFilter, Action<Message> onMessage)
        {
            var top = Wire.TopLevelSegment(topicFilter);
            var c = Ensure();
            var ns = $"data/{top}";
            var s = c.SubscribeFrame(ns, "msg", (ts, prio, raw) =>
            {
                try
                {
                    var (from, topic, payload) = Wire.DecodeDataFrame(raw);
                    if (!Wire.TopicMatches(topic, topicFilter)) return;
                    onMessage(new Message {
                        Topic = topic, FromClientId = from, Payload = payload,
                        Retained = prio <= 30,
                    });
                }
                catch (DataException) { /* drop malformed */ }
            });
            return new Subscription(s, ns, topicFilter);
        }

        public void Dispose()
        {
            foreach (var p in _pubs.Values) p.Dispose();
            _pubs.Clear();
            _client?.Dispose();
            _client = null;
        }
    }
}
