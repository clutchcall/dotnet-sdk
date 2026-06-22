#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// ClutchCall Analytics — publish rows to a platform datasource for ingestion
// into ClickHouse via the Tinybird-style Events API (POST /v0/events?name=).
//
// Independent of the MoQT transport client: a plain authenticated HTTPS POST,
// auth'd with a tqs_live_… API token carrying an "ingest" scope. The platform
// resolves the tenant from the token.
//
//   var a = new AnalyticsClient("https://api.clutchcall.dev", token);
//   await a.IngestAsync("match_server_sample", new[] {
//       new Dictionary<string, object> { ["match_id"] = "m1", ["cpu_pct"] = 42.0 } });
//
//   using var b = a.Batcher("robot_state_sample", maxRows: 500, flushMs: 1000);
//   b.Add(new Dictionary<string, object> { ["robot_id"] = "r1", ["battery_pct"] = 88 });
namespace ClutchCall.SDK
{
    public sealed class AnalyticsResult
    {
        public int SuccessfulRows { get; set; }
        public int QuarantinedRows { get; set; }
    }

    public sealed class AnalyticsClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly string _base;
        private readonly string _token;

        public AnalyticsClient(string baseUrl, string token)
        {
            if (string.IsNullOrEmpty(baseUrl)) throw new ArgumentException("baseUrl required");
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("token required");
            _base = baseUrl.TrimEnd('/');
            _token = token;
        }

        /// <summary>Publish a batch. Throws on transport/auth failure; schema-
        /// rejected rows are reported in <see cref="AnalyticsResult.QuarantinedRows"/>.</summary>
        public async Task<AnalyticsResult> IngestAsync(string datasource,
            IEnumerable<IDictionary<string, object>> rows)
        {
            var sb = new StringBuilder();
            var count = 0;
            foreach (var r in rows) { sb.Append(JsonSerializer.Serialize(r)).Append('\n'); count++; }
            if (count == 0) return new AnalyticsResult();

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{_base}/v0/events?name={Uri.EscapeDataString(datasource)}");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);
            req.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/x-ndjson");

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if ((int)resp.StatusCode >= 300)
                throw new Exception($"analytics ingest {(int)resp.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var res = new AnalyticsResult();
            if (doc.RootElement.TryGetProperty("successful_rows", out var s)) res.SuccessfulRows = s.GetInt32();
            if (doc.RootElement.TryGetProperty("quarantined_rows", out var q)) res.QuarantinedRows = q.GetInt32();
            return res;
        }

        public AnalyticsBatcher Batcher(string datasource, int maxRows = 500, int flushMs = 1000)
            => new AnalyticsBatcher(this, datasource, maxRows, flushMs);
    }

    /// <summary>Buffers rows and flushes by size or interval. Thread-safe;
    /// Dispose flushes the tail and stops the timer.</summary>
    public sealed class AnalyticsBatcher : IDisposable
    {
        private readonly AnalyticsClient _client;
        private readonly string _ds;
        private readonly int _max;
        private readonly List<IDictionary<string, object>> _buf = new List<IDictionary<string, object>>();
        private readonly Timer _timer;
        private readonly object _lock = new object();
        private bool _disposed;

        internal AnalyticsBatcher(AnalyticsClient client, string ds, int maxRows, int flushMs)
        {
            _client = client;
            _ds = ds;
            _max = maxRows <= 0 ? 500 : maxRows;
            var f = flushMs <= 0 ? 1000 : flushMs;
            _timer = new Timer(_ => Flush(), null, f, f);
        }

        public void Add(IDictionary<string, object> row)
        {
            bool full;
            lock (_lock) { _buf.Add(row); full = _buf.Count >= _max; }
            if (full) Flush();
        }

        public void Flush()
        {
            List<IDictionary<string, object>> batch;
            lock (_lock)
            {
                if (_buf.Count == 0) return;
                batch = new List<IDictionary<string, object>>(_buf);
                _buf.Clear();
            }
            try { _client.IngestAsync(_ds, batch).GetAwaiter().GetResult(); }
            catch { /* telemetry is best-effort */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            Flush();
        }
    }
}
