// Streams modality — broadcast over QUIC (MoQT) with signed playback URLs.
//
// Mirrors @clutchcall/sdk/streams and clutchcall.streams. The control plane
// (Streams) talks the BFF tRPC over HTTPS via HttpClient; the data plane
// (BroadcastViewer / BroadcastPublisher) wraps MoqtClient so the
// integrator doesn't deal with relay path conventions.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ClutchCall.SDK.Streams
{
    public class StreamsException : Exception { public StreamsException(string m) : base(m) {} }

    // ── control plane ───────────────────────────────────────────────────

    public sealed class Streams : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        public string OrgId { get; }

        public Streams(string baseUrl, string apiKey, string orgId, HttpClient? httpClient = null)
        {
            if (string.IsNullOrEmpty(baseUrl)) throw new StreamsException("baseUrl required");
            if (string.IsNullOrEmpty(apiKey))  throw new StreamsException("apiKey required");
            _baseUrl = baseUrl.TrimEnd('/');
            OrgId    = orgId ?? "";
            _http    = httpClient ?? new HttpClient();
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        public LiveInputs LiveInputs => new LiveInputs(this);
        public SigningKeys SigningKeys => new SigningKeys(this);

        public void Dispose() => _http?.Dispose();

        // Internal tRPC HTTP shape: GET for queries, POST for mutations.
        internal async Task<T> CallAsync<T>(string path, object payload, bool mutation)
        {
            HttpResponseMessage resp;
            if (mutation)
            {
                var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                resp = await _http.PostAsync($"{_baseUrl}/api/trpc/{path}", body).ConfigureAwait(false);
            }
            else
            {
                var input = Uri.EscapeDataString(JsonSerializer.Serialize(payload));
                resp = await _http.GetAsync($"{_baseUrl}/api/trpc/{path}?input={input}").ConfigureAwait(false);
            }
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new StreamsException($"tRPC {path} {(int)resp.StatusCode}: {Truncate(text, 200)}");
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new StreamsException(err.GetProperty("message").GetString() ?? "tRPC error");
            if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("data", out var data))
                throw new StreamsException($"tRPC {path}: empty result");
            return JsonSerializer.Deserialize<T>(data.GetRawText())!;
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
    }

    // ── live inputs ─────────────────────────────────────────────────────

    public sealed class LiveInputData
    {
        [JsonPropertyName("id")]                public string Id { get; set; } = "";
        [JsonPropertyName("external_input_id")] public string ExternalInputId { get; set; } = "";
        [JsonPropertyName("name")]              public string Name { get; set; } = "";
        [JsonPropertyName("status")]            public string Status { get; set; } = "";
        [JsonPropertyName("ingest")]            public string? Ingest { get; set; }
        [JsonPropertyName("createdAt")]         public string? CreatedAt { get; set; }
    }

    public sealed class SignedPlaybackUrl
    {
        public string Url { get; set; } = "";
        public string Kid { get; set; } = "";
        public string Alg { get; set; } = "";
        public long ExpiresAt { get; set; }
    }

    public sealed class LiveInputWithSecret
    {
        public LiveInput Input { get; set; } = null!;
        public string StreamKey { get; set; } = "";
    }

    public sealed class LiveInputs
    {
        private readonly Streams _s;
        internal LiveInputs(Streams s) { _s = s; }

        public async Task<LiveInputWithSecret> CreateAsync(string name, string ingest = "fmp4")
        {
            RequireOrg();
            var row = await _s.CallAsync<JsonElement>(
                "streams.liveInputs.create",
                new { orgId = _s.OrgId, name, ingest },
                mutation: true).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<LiveInputData>(row.GetRawText())!;
            var sk = row.TryGetProperty("stream_key_cleartext", out var k) ? k.GetString() ?? "" : "";
            return new LiveInputWithSecret { Input = new LiveInput(_s, data), StreamKey = sk };
        }

        public async Task<LiveInput> GetAsync(string id)
        {
            RequireOrg();
            var data = await _s.CallAsync<LiveInputData>(
                "streams.liveInputs.get",
                new { orgId = _s.OrgId, id },
                mutation: false).ConfigureAwait(false);
            return new LiveInput(_s, data);
        }

        private void RequireOrg()
        {
            if (string.IsNullOrEmpty(_s.OrgId))
                throw new StreamsException("Streams.LiveInputs: OrgId required on Streams()");
        }
    }

    public sealed class LiveInput
    {
        private readonly Streams _s;
        public LiveInputData Data { get; }
        public string Id => Data.Id;
        public string ExternalInputId => Data.ExternalInputId;

        internal LiveInput(Streams s, LiveInputData data) { _s = s; Data = data; }

        public async Task<SignedPlaybackUrl> SignedPlaybackUrlAsync(int ttlSeconds = 3600)
        {
            if (string.IsNullOrEmpty(_s.OrgId))
                throw new StreamsException("LiveInput.SignedPlaybackUrl: OrgId required");
            var r = await _s.CallAsync<JsonElement>(
                "streams.liveInputs.mintPlaybackToken",
                new { orgId = _s.OrgId, id = Id, ttlSeconds },
                mutation: true).ConfigureAwait(false);
            var token = r.GetProperty("token").GetString() ?? "";
            var input = r.GetProperty("input").GetString() ?? "";
            return new SignedPlaybackUrl {
                Url       = $"moq://relay.clutchcall.dev/playback/{input}?tok={token}",
                Kid       = r.GetProperty("kid").GetString() ?? "",
                Alg       = r.GetProperty("alg").GetString() ?? "",
                ExpiresAt = r.GetProperty("expires_at").GetInt64(),
            };
        }
    }

    // ── signing keys (minimal — create only) ────────────────────────────

    public sealed class SigningKeyData
    {
        [JsonPropertyName("id")]              public string Id { get; set; } = "";
        [JsonPropertyName("alg")]             public string Alg { get; set; } = "";
        [JsonPropertyName("use")]             public string Use { get; set; } = "";
        [JsonPropertyName("publicKeyPem")]    public string PublicKeyPem { get; set; } = "";
        [JsonPropertyName("status")]          public string Status { get; set; } = "";
    }

    public sealed class SigningKeys
    {
        private readonly Streams _s;
        internal SigningKeys(Streams s) { _s = s; }

        public Task<SigningKeyData> CreateAsync(string alg = "Ed25519", string use = "playback")
        {
            if (string.IsNullOrEmpty(_s.OrgId))
                throw new StreamsException("Streams.SigningKeys: OrgId required");
            return _s.CallAsync<SigningKeyData>(
                "streams.signingKeys.create",
                new { orgId = _s.OrgId, alg, use },
                mutation: true);
        }
    }

    // ── viewer ──────────────────────────────────────────────────────────

    public enum CloseReason { Complete, AuthFailed, Network, ClosedByCaller }

    public sealed class BroadcastChunk
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public ulong  TimestampUs { get; set; }
        public int    Priority    { get; set; }
        public bool   IsInit      { get; set; }
    }

    public sealed class BroadcastViewer : IDisposable
    {
        private readonly MoqtClient _client;
        private readonly FrameSubscription _sub;

        private BroadcastViewer(MoqtClient client, FrameSubscription sub) { _client = client; _sub = sub; }

        public static BroadcastViewer Open(string moqUrl, Action<bool, BroadcastChunk> onChunk, Action<CloseReason, string?>? onClose = null)
        {
            var (wt, token, ns) = ParsePlaybackUrl(moqUrl);
            var closed = false;
            void Fire(CloseReason r, string? d) { if (closed) return; closed = true; onClose?.Invoke(r, d); }
            var client = MoqtClient.Connect(wt, token, state => {
                if (state == 3) Fire(CloseReason.Network, null);
                if (state == 4) Fire(CloseReason.AuthFailed, null);
            });
            var sawInit = false;
            var sub = client.SubscribeFrame(ns, "broadcast", (ts, prio, data) => {
                bool isInit = !sawInit;
                sawInit = true;
                onChunk(isInit, new BroadcastChunk { Data = data, TimestampUs = ts, Priority = prio, IsInit = isInit });
            });
            return new BroadcastViewer(client, sub);
        }

        public void Dispose() { _sub?.Dispose(); _client?.Dispose(); }

        private static (string wt, string token, string ns) ParsePlaybackUrl(string moqUrl)
        {
            if (!moqUrl.StartsWith("moq://"))
                throw new StreamsException($"expected moq:// URL, got {moqUrl.Substring(0, Math.Min(32, moqUrl.Length))}");
            var rest = moqUrl.Substring("moq://".Length);
            var qIdx = rest.IndexOf('?');
            var pathPart = qIdx >= 0 ? rest.Substring(0, qIdx) : rest;
            var query    = qIdx >= 0 ? rest.Substring(qIdx + 1) : "";
            var parts = pathPart.Split('/');
            if (parts.Length < 3 || parts[1] != "playback" || string.IsNullOrEmpty(parts[2]))
                throw new StreamsException("playback URL must be moq://<host>/playback/<input_id>?tok=…");
            string? tok = null;
            foreach (var kv in query.Split('&'))
            {
                var eq = kv.IndexOf('=');
                if (eq > 0 && kv.Substring(0, eq) == "tok") { tok = kv.Substring(eq + 1); break; }
            }
            if (string.IsNullOrEmpty(tok))
                throw new StreamsException("playback URL missing ?tok=<jwt>");
            return (moqUrl, tok!, $"playback/{parts[2]}");
        }
    }

    // ── publisher ───────────────────────────────────────────────────────

    public sealed class PublisherCodecs
    {
        public string? Video { get; set; }
        public string? Audio { get; set; }
    }

    public sealed class BroadcastPublisher : IDisposable
    {
        private readonly MoqtClient _client;
        private readonly FramePublication _track;
        private bool _wroteInit;

        private BroadcastPublisher(MoqtClient c, FramePublication t) { _client = c; _track = t; }

        public static BroadcastPublisher Open(string inputId, string streamKey, PublisherCodecs? codecs = null, string? relayHost = null)
        {
            if (string.IsNullOrEmpty(inputId))   throw new StreamsException("inputId required");
            if (string.IsNullOrEmpty(streamKey)) throw new StreamsException("streamKey required");
            relayHost ??= "relay.clutchcall.dev";
            var moqUrl = $"moq://{relayHost}/publish/{inputId}?sk={Uri.EscapeDataString(streamKey)}";
            var client = MoqtClient.Connect(moqUrl, "", _ => { });
            var tag = string.Join(",", new[] { codecs?.Video, codecs?.Audio }.Where(s => !string.IsNullOrEmpty(s)));
            var track = client.PublishFrame($"publish/{inputId}", "broadcast", "media.broadcast", tag, 0);
            return new BroadcastPublisher(client, track);
        }

        public void Write(byte[] chunk)
        {
            byte priority = (byte)(_wroteInit ? 1 : 0);
            _wroteInit = true;
            var tsUs = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
            _track.Write(tsUs, chunk, priority);
        }

        public void Dispose() { _track?.Dispose(); _client?.Dispose(); }
    }
}
