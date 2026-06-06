// Voice modality — telephony over QUIC (MoQT).
//
// Two primitives: Calls (control plane — originate, transfer, hangup over
// the BFF tRPC via HttpClient) and AudioBridge (data plane — bidirectional
// Opus / PCM / G.711 over MoQT with the
// voice/<sid>/{uplink,downlink} namespace convention enforced).

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ClutchCall.SDK.Voice
{
    public class VoiceException : Exception { public VoiceException(string m) : base(m) {} }

    // ── client ──────────────────────────────────────────────────────────

    public sealed class Voice : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        public  string OrgId     { get; }
        public  string RelayHost { get; set; } = "relay.clutchcall.dev";
        internal string ApiKey   { get; }

        public Voice(string baseUrl, string apiKey, string orgId, HttpClient? httpClient = null)
        {
            if (string.IsNullOrEmpty(baseUrl)) throw new VoiceException("baseUrl required");
            if (string.IsNullOrEmpty(apiKey))  throw new VoiceException("apiKey required");
            if (string.IsNullOrEmpty(orgId))   throw new VoiceException("orgId required");
            _baseUrl = baseUrl.TrimEnd('/');
            OrgId    = orgId;
            ApiKey   = apiKey;
            _http    = httpClient ?? new HttpClient();
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        public Calls Calls           => new Calls(this);
        public AudioBridgeFactory AudioBridge => new AudioBridgeFactory(this);
        public Agents Agents         => new Agents(this);

        public void Dispose() => _http?.Dispose();

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
                throw new VoiceException($"tRPC {path} {(int)resp.StatusCode}: {Truncate(text, 200)}");
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new VoiceException(err.GetProperty("message").GetString() ?? "tRPC error");
            if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("data", out var data))
                throw new VoiceException($"tRPC {path}: empty result");
            return JsonSerializer.Deserialize<T>(data.GetRawText())!;
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
    }

    // ── calls ───────────────────────────────────────────────────────────

    public sealed class CallData
    {
        [JsonPropertyName("sid")]       public string Sid    { get; set; } = "";
        [JsonPropertyName("status")]    public string Status { get; set; } = "";
        [JsonPropertyName("to")]        public string To     { get; set; } = "";
        [JsonPropertyName("from")]      public string From   { get; set; } = "";
        [JsonPropertyName("startedAt")] public string StartedAt { get; set; } = "";
        [JsonPropertyName("trunkId")]   public string? TrunkId  { get; set; }
        [JsonPropertyName("agent")]     public string? Agent    { get; set; }
    }

    public sealed class OriginateArgs
    {
        public string To             { get; set; } = "";
        public string From           { get; set; } = "";
        public string TrunkId        { get; set; } = "";
        public string? Agent         { get; set; }
        public int RingTimeoutSec    { get; set; } = 30;
    }

    public sealed class Calls
    {
        private readonly Voice _v;
        internal Calls(Voice v) { _v = v; }

        public async Task<Call> OriginateAsync(OriginateArgs args)
        {
            var data = await _v.CallAsync<CallData>(
                "voice.calls.originate",
                new { orgId = _v.OrgId, to = args.To, from = args.From,
                      trunkId = args.TrunkId, agent = args.Agent,
                      ringTimeoutSec = args.RingTimeoutSec },
                mutation: true).ConfigureAwait(false);
            return new Call(_v, data);
        }

        public async Task<Call> GetAsync(string sid)
        {
            var data = await _v.CallAsync<CallData>(
                "voice.calls.get",
                new { orgId = _v.OrgId, sid },
                mutation: false).ConfigureAwait(false);
            return new Call(_v, data);
        }
    }

    public sealed class Call
    {
        public CallData Data { get; }
        public string Sid => Data.Sid;
        private readonly Voice _v;
        internal Call(Voice v, CallData d) { _v = v; Data = d; }

        public Task TransferToAsync(string to) => TransferAsync(to, null);
        public Task TransferToAgentAsync(string agent) => TransferAsync(null, agent);

        private async Task TransferAsync(string? to, string? agent)
        {
            await _v.CallAsync<JsonElement>(
                "voice.calls.transfer",
                new { orgId = _v.OrgId, sid = Sid, to, agent },
                mutation: true).ConfigureAwait(false);
        }

        public Task HangupAsync() =>
            _v.CallAsync<JsonElement>(
                "voice.calls.hangup",
                new { orgId = _v.OrgId, sid = Sid },
                mutation: true);
    }

    // ── audio bridge ────────────────────────────────────────────────────

    public enum Codec { Opus, Pcm16, G711ULaw, G711ALaw }

    public sealed class AudioBridgeOpts
    {
        public Codec Codec      { get; set; } = Codec.Opus;
        public uint  SampleRate { get; set; } = 48000;
        public byte  Channels   { get; set; } = 1;
        public ushort FrameMs   { get; set; } = 20;
        public Action<byte[], ulong>? OnUplink { get; set; }
    }

    public sealed class AudioBridge : IDisposable
    {
        private readonly MoqtClient _client;
        private readonly AudioPublication _pub;
        private readonly AudioSubscription _sub;
        public string CallSid { get; }

        internal AudioBridge(MoqtClient c, AudioPublication p, AudioSubscription s, string sid)
        { _client = c; _pub = p; _sub = s; CallSid = sid; }

        public void PublishDownlink(byte[] frame)
        {
            var tsUs = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
            _pub.Write(tsUs, frame);
        }

        public void Dispose() { _pub?.Dispose(); _sub?.Dispose(); _client?.Dispose(); }
    }

    public sealed class AudioBridgeFactory
    {
        private readonly Voice _v;
        internal AudioBridgeFactory(Voice v) { _v = v; }

        public AudioBridge Attach(string callSid, AudioBridgeOpts opts)
        {
            if (string.IsNullOrEmpty(callSid)) throw new VoiceException("Attach: callSid required");
            if (opts.OnUplink == null)         throw new VoiceException("Attach: OnUplink required");
            var url = $"moq://{_v.RelayHost}/voice/{Uri.EscapeDataString(callSid)}";
            var client = MoqtClient.Connect(url, _v.ApiKey, _ => { });
            // SubscribeAudio is (ulong ts, byte[] frame); OnUplink is (frame, ts) to
            // match the other modality callbacks in this SDK.
            var sub = client.SubscribeAudio($"voice/{callSid}/uplink", "audio",
                (ulong ts, byte[] frame) => opts.OnUplink(frame, ts));
            var capability = "voice/" + opts.Codec.ToString().ToLowerInvariant().Replace("g711u", "g711_u").Replace("g711a", "g711_a");
            var pub = client.PublishAudio($"voice/{callSid}/downlink", "audio",
                capability, opts.SampleRate, opts.Channels, opts.FrameMs);
            return new AudioBridge(client, pub, sub, callSid);
        }
    }

    // ── agents ──────────────────────────────────────────────────────────

    public sealed class Agents
    {
        private readonly Voice _v;
        internal Agents(Voice v) { _v = v; }

        public Task AttachAsync(string callSid, string agent)
        {
            if (string.IsNullOrEmpty(callSid)) throw new VoiceException("Attach: callSid required");
            if (string.IsNullOrEmpty(agent))   throw new VoiceException("Attach: agent required");
            return _v.CallAsync<JsonElement>(
                "voice.agents.attach",
                new { orgId = _v.OrgId, sid = callSid, agent },
                mutation: true);
        }
    }
}
