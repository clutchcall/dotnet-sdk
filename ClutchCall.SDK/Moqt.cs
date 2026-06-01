using System;
using System.Runtime.InteropServices;

namespace ClutchCall.SDK
{
    // Capability-aware MoQT track pub/sub for .NET, over the shared
    // clutchcall_moqt_ffi C ABI (core/moqt_ffi.cc) — the same C++ engine the
    // Python/Go/C++ SDKs use. A published track carries a `capability` (intent /
    // routing key, e.g. "asr"/"tts"/"media.passthrough"); the relay/gateway routes
    // it to the module that registered that capability.
    //
    //   using var c = MoqtClient.Connect("quic://relay.acme.dev:4443", "tok", st => {});
    //   using var pub = c.PublishAudio("voice/acme/call-1", "mic", "asr", 48000, 1, 20);
    //   pub.Write(0, pcm);
    //   using var sub = c.SubscribeAudio("voice/acme/call-1", "agent", (ts, data) => Play(data));
    //
    // Callbacks fire from the engine io_thread; we pin the delegates (GCHandle)
    // so they outlive the native side.

    internal static class MoqtNative
    {
        private const string Lib = "clutchcall_moqt_ffi";

        public delegate void StateCb(IntPtr user, int state);
        public delegate void FrameCb(IntPtr user, ulong tsUs, IntPtr data, UIntPtr len);
        public delegate void FrameObjCb(IntPtr user, ulong tsUs, byte priority, IntPtr data, UIntPtr len);

        [DllImport(Lib)] public static extern IntPtr clutch_moqt_connect(string url, string token, StateCb cb, IntPtr user);
        [DllImport(Lib)] public static extern void   clutch_moqt_client_close(IntPtr h);
        [DllImport(Lib)] public static extern IntPtr clutch_moqt_publish_audio(IntPtr h, string ns, string name, string capability, uint sampleRate, byte channels, ushort frameMs);
        [DllImport(Lib)] public static extern void   clutch_moqt_pub_write(IntPtr h, ulong tsUs, byte[] data, UIntPtr len);
        [DllImport(Lib)] public static extern UIntPtr clutch_moqt_pub_subscriber_count(IntPtr h);
        [DllImport(Lib)] public static extern void   clutch_moqt_pub_close(IntPtr h);
        [DllImport(Lib)] public static extern IntPtr clutch_moqt_subscribe_audio(IntPtr h, string ns, string name, FrameCb cb, IntPtr user);
        [DllImport(Lib)] public static extern void   clutch_moqt_sub_close(IntPtr h);
        [DllImport(Lib)] public static extern IntPtr clutch_moqt_publish_frame(IntPtr h, string ns, string name, string capability, string schemaTag, byte defaultPriority);
        [DllImport(Lib)] public static extern void   clutch_moqt_frame_write(IntPtr h, ulong tsUs, byte[] data, UIntPtr len, byte priority);
        [DllImport(Lib)] public static extern UIntPtr clutch_moqt_frame_pub_subscriber_count(IntPtr h);
        [DllImport(Lib)] public static extern void   clutch_moqt_frame_pub_close(IntPtr h);
        [DllImport(Lib)] public static extern IntPtr clutch_moqt_subscribe_frame(IntPtr h, string ns, string name, FrameObjCb cb, IntPtr user);
        [DllImport(Lib)] public static extern void   clutch_moqt_frame_sub_close(IntPtr h);
    }

    public sealed class AudioPublication : IDisposable
    {
        private IntPtr _h;
        internal AudioPublication(IntPtr h) { _h = h; }
        public void Write(ulong timestampUs, byte[] pcm)
        {
            if (_h != IntPtr.Zero)
                MoqtNative.clutch_moqt_pub_write(_h, timestampUs, pcm, (UIntPtr)(pcm?.Length ?? 0));
        }
        public ulong SubscriberCount() => _h == IntPtr.Zero ? 0 : (ulong)MoqtNative.clutch_moqt_pub_subscriber_count(_h);
        public void Dispose() { if (_h != IntPtr.Zero) { MoqtNative.clutch_moqt_pub_close(_h); _h = IntPtr.Zero; } }
    }

    public sealed class AudioSubscription : IDisposable
    {
        private IntPtr _h;
        private MoqtNative.FrameCb? _cb;   // keep alive
        internal AudioSubscription(IntPtr h, MoqtNative.FrameCb cb) { _h = h; _cb = cb; }
        public void Dispose() { if (_h != IntPtr.Zero) { MoqtNative.clutch_moqt_sub_close(_h); _h = IntPtr.Zero; _cb = null; } }
    }

    /// A live published frame track (opaque binary, per-frame priority).
    public sealed class FramePublication : IDisposable
    {
        private IntPtr _h;
        internal FramePublication(IntPtr h) { _h = h; }
        public void Write(ulong timestampUs, byte[] data, byte priority = 128)
        {
            if (_h != IntPtr.Zero)
                MoqtNative.clutch_moqt_frame_write(_h, timestampUs, data, (UIntPtr)(data?.Length ?? 0), priority);
        }
        public ulong SubscriberCount() => _h == IntPtr.Zero ? 0 : (ulong)MoqtNative.clutch_moqt_frame_pub_subscriber_count(_h);
        public void Dispose() { if (_h != IntPtr.Zero) { MoqtNative.clutch_moqt_frame_pub_close(_h); _h = IntPtr.Zero; } }
    }

    /// A live frame subscription.
    public sealed class FrameSubscription : IDisposable
    {
        private IntPtr _h;
        private MoqtNative.FrameObjCb? _cb;   // keep alive
        internal FrameSubscription(IntPtr h, MoqtNative.FrameObjCb cb) { _h = h; _cb = cb; }
        public void Dispose() { if (_h != IntPtr.Zero) { MoqtNative.clutch_moqt_frame_sub_close(_h); _h = IntPtr.Zero; _cb = null; } }
    }

    /// A MoQT session against the relay; track publish/subscribe are capability-aware.
    public sealed class MoqtClient : IDisposable
    {
        private IntPtr _h;
        private MoqtNative.StateCb _stateCb;   // keep alive

        private MoqtClient(IntPtr h, MoqtNative.StateCb cb) { _h = h; _stateCb = cb; }

        public static MoqtClient Connect(string url, string token = "", Action<int>? onState = null)
        {
            MoqtNative.StateCb cb = (user, state) => onState?.Invoke(state);
            IntPtr h = MoqtNative.clutch_moqt_connect(url, token ?? "", cb, IntPtr.Zero);
            if (h == IntPtr.Zero) throw new InvalidOperationException("clutch_moqt_connect failed");
            return new MoqtClient(h, cb);
        }

        public AudioPublication PublishAudio(string ns, string name, string capability = "",
                                             uint sampleRate = 48000, byte channels = 1, ushort frameMs = 20)
        {
            IntPtr h = MoqtNative.clutch_moqt_publish_audio(_h, ns, name, capability ?? "", sampleRate, channels, frameMs);
            if (h == IntPtr.Zero) throw new InvalidOperationException("publish_audio failed");
            return new AudioPublication(h);
        }

        public AudioSubscription SubscribeAudio(string ns, string name, Action<ulong, byte[]> onFrame)
        {
            MoqtNative.FrameCb cb = (user, ts, data, len) =>
            {
                int n = (int)len;
                byte[] buf = (data == IntPtr.Zero || n == 0) ? Array.Empty<byte>() : new byte[n];
                if (n > 0) Marshal.Copy(data, buf, 0, n);
                onFrame?.Invoke(ts, buf);
            };
            IntPtr h = MoqtNative.clutch_moqt_subscribe_audio(_h, ns, name, cb, IntPtr.Zero);
            if (h == IntPtr.Zero) throw new InvalidOperationException("subscribe_audio failed");
            return new AudioSubscription(h, cb);
        }

        public FramePublication PublishFrame(string ns, string name, string capability = "",
                                             string schemaTag = "", byte defaultPriority = 128)
        {
            IntPtr h = MoqtNative.clutch_moqt_publish_frame(_h, ns, name, capability ?? "", schemaTag ?? "", defaultPriority);
            if (h == IntPtr.Zero) throw new InvalidOperationException("publish_frame failed");
            return new FramePublication(h);
        }

        public FrameSubscription SubscribeFrame(string ns, string name, Action<ulong, int, byte[]> onFrame)
        {
            MoqtNative.FrameObjCb cb = (user, ts, priority, data, len) =>
            {
                int n = (int)len;
                byte[] buf = (data == IntPtr.Zero || n == 0) ? Array.Empty<byte>() : new byte[n];
                if (n > 0) Marshal.Copy(data, buf, 0, n);
                onFrame?.Invoke(ts, priority, buf);
            };
            IntPtr h = MoqtNative.clutch_moqt_subscribe_frame(_h, ns, name, cb, IntPtr.Zero);
            if (h == IntPtr.Zero) throw new InvalidOperationException("subscribe_frame failed");
            return new FrameSubscription(h, cb);
        }

        public void Dispose() { if (_h != IntPtr.Zero) { MoqtNative.clutch_moqt_client_close(_h); _h = IntPtr.Zero; } }
    }
}
