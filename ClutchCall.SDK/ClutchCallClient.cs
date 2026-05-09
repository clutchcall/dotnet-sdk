#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ClutchCall.SDK
{
    public class Credentials
    {
        [JsonPropertyName("tenant_id")]
        public string TenantId { get; set; } = string.Empty;

        [JsonPropertyName("private_key")]
        public string PrivateKey { get; set; } = string.Empty;

        [JsonPropertyName("private_key_id")]
        public string PrivateKeyId { get; set; } = string.Empty;
    }

    public class ClutchCallClient
    {
        private readonly string _endpoint;
        private readonly Credentials _credentials;
        private QuicConnection? _quicConnection;
        private QuicStream? _audioOutStream;
        private readonly string _clientId;

        public Action<byte[]>? OnAudioFrame;
        public Action<byte[]>? OnCallEvent;

        public ClutchCallClient(string endpoint, string credentialsPath)
        {
            _endpoint = endpoint;
            var json = File.ReadAllText(credentialsPath);
            _credentials = JsonSerializer.Deserialize<Credentials>(json) ?? throw new Exception("Invalid credentials structure.");
            _clientId = Guid.NewGuid().ToString();
        }

        private async Task ConnectAsync()
        {
            if (_quicConnection != null) return;

            var hostPort = _endpoint.Replace("quic://", "").Split(':');
            var endPoint = new DnsEndPoint(hostPort[0], int.Parse(hostPort[1]));
            
            var clientOptions = new QuicClientConnectionOptions
            {
                RemoteEndPoint = endPoint,
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                MaxInboundUnidirectionalStreams = 100,
                MaxInboundBidirectionalStreams = 100,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = new System.Collections.Generic.List<SslApplicationProtocol> { new SslApplicationProtocol("h3") },
                    RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
                }
            };
            
            _quicConnection = await QuicConnection.ConnectAsync(clientOptions);
            _ = Task.Run(ReceiveLoop);

            var eBuf = NativeMethods.clutchcall_rpc_event_stream_request(_clientId);
            await SendRpcAsync(eBuf);
        }

        private async Task ReceiveLoop()
        {
            if (_quicConnection == null) return;
            while (true)
            {
                try
                {
                    var stream = await _quicConnection.AcceptInboundStreamAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var lenBuf = new byte[4];
                            while (true)
                            {
                                await ReadExactAsync(stream, lenBuf);
                                uint totalLen = BitConverter.ToUInt32(lenBuf, 0);
                                if (totalLen == 0 || totalLen > 1024 * 1024) continue;
                                
                                var payloadBuf = new byte[totalLen];
                                await ReadExactAsync(stream, payloadBuf);
                                
                                if (totalLen >= 4)
                                {
                                    uint dgId = BitConverter.ToUInt32(payloadBuf, 0);
                                    byte[] data = new byte[totalLen - 4];
                                    Array.Copy(payloadBuf, 4, data, 0, totalLen - 4);
                                    
                                    if (dgId == MethodID.AUDIO_FRAME) OnAudioFrame?.Invoke(data);
                                    else if (dgId == MethodID.STREAM_EVENTS) OnCallEvent?.Invoke(data);
                                }
                            }
                        }
                        catch { }
                    });
                }
                catch { break; }
            }
        }

        private async Task ReadExactAsync(QuicStream stream, byte[] buffer)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var memory = new Memory<byte>(buffer, totalRead, buffer.Length - totalRead);
                int read = await stream.ReadAsync(memory);
                if (read == 0) throw new EndOfStreamException();
                totalRead += read;
            }
        }

        private async Task SendRpcAsync(ClutchCallBuffer buffer)
        {
            await ConnectAsync();

            int length = (int)buffer.Length;
            byte[] payload = new byte[length];
            Marshal.Copy(buffer.Data, payload, 0, length);
            NativeMethods.clutchcall_free_buffer(buffer);

            await using var stream = await _quicConnection!.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
            await stream.WriteAsync(payload);
            stream.CompleteWrites();
        }

        public async Task DialAsync(string to, string trunkId, string callFrom = "", int maxDurationMs = 0, int defaultApp = 1, string defaultAppArgs = "", string aiWs = "", string aiQuic = "", bool autoBargeIn = false, int bargeInPatienceMs = 250)
        {
            var buffer = NativeMethods.clutchcall_rpc_originate_request(
                trunkId, to, callFrom, aiWs, aiQuic, _credentials.TenantId, maxDurationMs, "", defaultApp, defaultAppArgs, autoBargeIn, bargeInPatienceMs, _clientId
            );
            await SendRpcAsync(buffer);
        }

        public async Task OriginateBulkAsync(string csvUrl, string trunkId, int callsPerSecond, string campaignId, int defaultApp = 1, string defaultAppArgs = "", string aiWs = "", string aiQuic = "", bool autoBargeIn = false, int bargeInPatienceMs = 250)
        {
            var buffer = NativeMethods.clutchcall_rpc_bulk_request(
                csvUrl, trunkId, "", "", aiWs, aiQuic, _credentials.TenantId, 0, defaultApp, defaultAppArgs, callsPerSecond, 1000, campaignId, autoBargeIn, bargeInPatienceMs
            );
            await SendRpcAsync(buffer);
        }

        public async Task TerminateAsync(string callSid)
        {
            var buffer = NativeMethods.clutchcall_rpc_terminate_request(callSid);
            await SendRpcAsync(buffer);
        }

        public async Task BargeAsync(string callSid)
        {
            var buffer = NativeMethods.clutchcall_rpc_barge_request(callSid);
            await SendRpcAsync(buffer);
        }

        public async Task AbortBulkAsync(string campaignId)
        {
            var buffer = NativeMethods.clutchcall_rpc_abort_bulk_request(campaignId);
            await SendRpcAsync(buffer);
        }

        public async Task StreamEventsAsync(string clientId)
        {
            var buffer = NativeMethods.clutchcall_rpc_event_stream_request(clientId);
            await SendRpcAsync(buffer);
        }

        public async Task SetInboundRoutingAsync(string trunkId, int rule, string audioUrl, string webhookUrl, string aiWs, string aiQuic)
        {
            var buffer = NativeMethods.clutchcall_rpc_set_inbound_routing_request(trunkId, rule, audioUrl, webhookUrl, aiWs, aiQuic);
            await SendRpcAsync(buffer);
        }

        public async Task GetIncomingCallsAsync(string trunkId)
        {
            var buffer = NativeMethods.clutchcall_rpc_get_incoming_calls_request(trunkId);
            await SendRpcAsync(buffer);
        }

        public async Task AnswerIncomingCallAsync(string callSid, string aiWs, string aiQuic)
        {
            var buffer = NativeMethods.clutchcall_rpc_answer_incoming_call_request(callSid, aiWs, aiQuic, _clientId);
            await SendRpcAsync(buffer);
        }

        public async Task GetActiveBucketsAsync()
        {
            var buffer = NativeMethods.clutchcall_rpc_empty();
            await SendRpcAsync(buffer);
        }

        public async Task GetBucketCallsAsync(string bucketId)
        {
            var buffer = NativeMethods.clutchcall_rpc_bucket_request(bucketId);
            await SendRpcAsync(buffer);
        }

        public async Task ExecuteBucketActionAsync(string bucketId, int action)
        {
            var buffer = NativeMethods.clutchcall_rpc_bucket_action_request(bucketId, action);
            await SendRpcAsync(buffer);
        }

        public AudioFrame DeserializeAudioFrame(IntPtr payloadBuffer, UIntPtr length)
        {
            return NativeMethods.clutchcall_deserialize_audio_frame(payloadBuffer, length);
        }

        public CallEvent DeserializeCallEvent(IntPtr payloadBuffer, UIntPtr length)
        {
            return NativeMethods.clutchcall_deserialize_call_event(payloadBuffer, length);
        }

        public async Task PushAudioAsync(string callSid, byte[] payload, string codec, ulong sequenceNumber, bool endOfStream)
        {
            await ConnectAsync();

            IntPtr unmanagedPayload = Marshal.AllocHGlobal(payload.Length);
            Marshal.Copy(payload, 0, unmanagedPayload, payload.Length);

            var buffer = NativeMethods.clutchcall_serialize_audio_frame(callSid, unmanagedPayload, codec, sequenceNumber, endOfStream);
            Marshal.FreeHGlobal(unmanagedPayload);

            int length = (int)buffer.Length;
            byte[] serialized = new byte[length];
            Marshal.Copy(buffer.Data, serialized, 0, length);
            NativeMethods.clutchcall_free_buffer(buffer);

            byte[] packet = new byte[length + 8];
            int packetLen = length + 4;
            BitConverter.GetBytes((uint)packetLen).CopyTo(packet, 0);
            BitConverter.GetBytes(MethodID.AUDIO_FRAME).CopyTo(packet, 4);
            Array.Copy(serialized, 0, packet, 8, length);

            _audioOutStream ??= await _quicConnection!.OpenOutboundStreamAsync(QuicStreamType.Unidirectional);
            await _audioOutStream.WriteAsync(packet);
            await _audioOutStream.FlushAsync();
        }
    }
}
