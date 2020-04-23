using System;

namespace NServiceBus.SqlTransport.Tests.Shared
{
    public class Configuration
    {
        public static string SenderEndpointName = "SqlTransport-Test-Sender";

        public static string ReceiverEndpointName = "SqlTransport-Test-Receiver";

        public static string ConnectionString = Environment.GetEnvironmentVariable("SqlTestsConnectionString");
    }
}
