using Xunit;
using ClutchCall.SDK;
using System.IO;

namespace ClutchCall.SDK.Tests
{
    public class ClientTests
    {
        [Fact]
        public void MethodIdConstantsAreStable()
        {
            Assert.Equal(1430677891u, MethodID.ORIGINATE);
            Assert.Equal(721069100u, MethodID.ORIGINATE_BULK);
            Assert.Equal(3834253405u, MethodID.TERMINATE);
            Assert.Equal(959835745u, MethodID.STREAM_EVENTS);
            Assert.Equal(2991054320u, MethodID.AUDIO_FRAME);
        }

        [Fact]
        public void MethodIdValuesAreUnique()
        {
            var ids = new uint[]
            {
                MethodID.ORIGINATE,
                MethodID.ORIGINATE_BULK,
                MethodID.ABORT_BULK,
                MethodID.TERMINATE,
                MethodID.STREAM_EVENTS,
                MethodID.BARGE,
                MethodID.AUDIO_FRAME,
            };
            Assert.Equal(ids.Length, new HashSet<uint>(ids).Count);
        }

        [Fact]
        public void ClutchCallClient_Initialization_SuccessfullyReadsCredentials()
        {
            var testCredsPath = Path.Combine(Path.GetTempPath(), $"ccsdk_test_{Guid.NewGuid()}.json");
            File.WriteAllText(testCredsPath,
                "{\"tenant_id\": \"tenant-A\", \"private_key\": \"mock-key\", \"private_key_id\": \"kid\"}");

            try
            {
                var client = new ClutchCallClient("quic://127.0.0.1:9090", testCredsPath);
                Assert.NotNull(client);
            }
            finally
            {
                File.Delete(testCredsPath);
            }
        }
    }
}
