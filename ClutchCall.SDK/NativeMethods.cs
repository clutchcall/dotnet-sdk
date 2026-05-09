using System;
using System.Runtime.InteropServices;

namespace ClutchCall.SDK
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ClutchCallBuffer
    {
        public IntPtr Data;
        public UIntPtr Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioFrame
    {
        public IntPtr CallSid;
        public IntPtr Payload;
        public IntPtr Codec;
        public ulong SequenceNumber;
        [MarshalAs(UnmanagedType.I1)] public bool EndOfStream;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CallEvent
    {
        public IntPtr CallSid;
        public int EventType;
        public IntPtr Status;
        public long StartTimestampMs;
        public int Q850Cause;
        public IntPtr RecordingUrl;
        public int DurationSeconds;
    }

    internal static class NativeMethods
    {
        private const string DllName = "clutchcall_core_ffi";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void clutchcall_free_buffer(ClutchCallBuffer buf);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_rpc_originate_request(
            [MarshalAs(UnmanagedType.LPStr)] string trunk_id,
            [MarshalAs(UnmanagedType.LPStr)] string to,
            [MarshalAs(UnmanagedType.LPStr)] string call_from,
            [MarshalAs(UnmanagedType.LPStr)] string ai_websocket_url,
            [MarshalAs(UnmanagedType.LPStr)] string ai_quic_url,
            [MarshalAs(UnmanagedType.LPStr)] string tenant_id,
            int max_duration_ms,
            [MarshalAs(UnmanagedType.LPStr)] string call_sid,
            int default_app,
            [MarshalAs(UnmanagedType.LPStr)] string default_app_args,
            [MarshalAs(UnmanagedType.I1)] bool auto_barge_in,
            int barge_in_patience_ms,
            [MarshalAs(UnmanagedType.LPStr)] string client_id);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_rpc_bulk_request(
            [MarshalAs(UnmanagedType.LPStr)] string csv_url,
            [MarshalAs(UnmanagedType.LPStr)] string trunk_id,
            [MarshalAs(UnmanagedType.LPStr)] string template_to,
            [MarshalAs(UnmanagedType.LPStr)] string call_from,
            [MarshalAs(UnmanagedType.LPStr)] string template_ai_websocket_url,
            [MarshalAs(UnmanagedType.LPStr)] string template_ai_quic_url,
            [MarshalAs(UnmanagedType.LPStr)] string template_tenant_id,
            int template_max_duration_ms,
            int template_default_app,
            [MarshalAs(UnmanagedType.LPStr)] string template_default_app_args,
            int calls_per_second,
            int max_concurrent,
            [MarshalAs(UnmanagedType.LPStr)] string campaign_id,
            [MarshalAs(UnmanagedType.I1)] bool auto_barge_in,
            int barge_in_patience_ms);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_rpc_abort_bulk_request([MarshalAs(UnmanagedType.LPStr)] string campaign_id);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_rpc_terminate_request([MarshalAs(UnmanagedType.LPStr)] string call_sid);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_rpc_event_stream_request([MarshalAs(UnmanagedType.LPStr)] string client_id);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_rpc_barge_request([MarshalAs(UnmanagedType.LPStr)] string call_sid);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_rpc_set_inbound_routing_request(
            [MarshalAs(UnmanagedType.LPStr)] string trunk_id,
            int rule,
            [MarshalAs(UnmanagedType.LPStr)] string audio_url,
            [MarshalAs(UnmanagedType.LPStr)] string webhook_url,
            [MarshalAs(UnmanagedType.LPStr)] string ai_websocket_url,
            [MarshalAs(UnmanagedType.LPStr)] string ai_quic_url);



        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_rpc_get_incoming_calls_request([MarshalAs(UnmanagedType.LPStr)] string trunk_id);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_rpc_answer_incoming_call_request(
            [MarshalAs(UnmanagedType.LPStr)] string call_sid,
            [MarshalAs(UnmanagedType.LPStr)] string ai_websocket_url,
            [MarshalAs(UnmanagedType.LPStr)] string ai_quic_url,
            [MarshalAs(UnmanagedType.LPStr)] string client_id);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_rpc_empty();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_rpc_bucket_request([MarshalAs(UnmanagedType.LPStr)] string bucket_id);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_rpc_bucket_action_request([MarshalAs(UnmanagedType.LPStr)] string bucket_id, int action);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern AudioFrame clutchcall_deserialize_audio_frame(IntPtr buffer, UIntPtr length);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern CallEvent clutchcall_deserialize_call_event(IntPtr buffer, UIntPtr length);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ClutchCallBuffer clutchcall_serialize_audio_frame(
            [MarshalAs(UnmanagedType.LPStr)] string call_sid,
            IntPtr payload,
            [MarshalAs(UnmanagedType.LPStr)] string codec,
            ulong sequence_number,
            [MarshalAs(UnmanagedType.I1)] bool end_of_stream);
    }
}
