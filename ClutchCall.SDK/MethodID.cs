namespace ClutchCall.SDK
{
    public static class ErrorCode
    {
        public const uint SUCCESS = 0;
        public const uint ERR_INVALID_TRUNK = 1;
        public const uint ERR_INVALID_DESTINATION = 2;
        public const uint ERR_RATE_LIMITED = 3;
        public const uint ERR_CIRCUIT_BREAKER = 4;
        public const uint ERR_INTERNAL_ERROR = 5;
        public const uint ERR_VALIDATION_FAILED = 6;
        public const uint ERR_UNAUTHORIZED = 7;
    }
    public static class DialplanAction
    {
        public const uint HANGUP = 0;
        public const uint PARK = 1;
        public const uint MUSIC_ON_HOLD = 2;
        public const uint PLAYBACK = 3;
        public const uint UNPARK_AND_BRIDGE = 4;
        public const uint ANSWER = 5;
        public const uint AI_BIDIRECTIONAL_STREAM = 6;
        public const uint TRANSFER = 7;
        public const uint MUTE = 8;
        public const uint UNMUTE = 9;
        public const uint HOLD = 10;
        public const uint UNHOLD = 11;
        public const uint SEND_DTMF = 12;
        public const uint SUPERVISE = 13;
        public const uint LOOPBACK = 14;
    }
    public static class InboundRule
    {
        public const uint REJECT = 0;
        public const uint PLAY_AND_HANGUP = 1;
        public const uint NOTIFY_AND_HANGUP = 2;
        public const uint HANDLE_AI = 3;
    }
    public static class EventType
    {
        public const uint UNKNOWN = 0;
        public const uint CHANNEL_CREATE = 1;
        public const uint CHANNEL_ANSWER = 2;
        public const uint CHANNEL_HANGUP_COMPLETE = 3;
        public const uint CHANNEL_HOLD = 4;
        public const uint CHANNEL_RESUME = 5;
    }
    public static class MethodID
    {
        public const uint ORIGINATE = 1430677891;
        public const uint ORIGINATE_BULK = 721069100;
        public const uint ABORT_BULK = 3861915064;
        public const uint TERMINATE = 3834253405;
        public const uint STREAM_EVENTS = 959835745;
        public const uint SET_INBOUND_ROUTING = 1933986897;
        public const uint GET_INCOMING_CALLS = 1161946746;
        public const uint ANSWER_INCOMING_CALL = 2990157256;
        public const uint GET_ACTIVE_BUCKETS = 2624504207;
        public const uint GET_BUCKET_CALLS = 1217351135;
        public const uint EXECUTE_BUCKET_ACTION = 4030863293;
        public const uint EXECUTE_DIALPLAN = 80147304;
        public const uint BARGE = 3854301714;
        public const uint SUPERVISE_SUBSCRIBE = 425376200;
        public const uint AUDIO_FRAME = 2991054320;
    }
}