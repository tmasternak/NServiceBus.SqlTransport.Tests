using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NServiceBus.SqlTransport.Tests.Shared;

namespace NServiceBus.SqlTransport.Tests.Receiver
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var configuration = new EndpointConfiguration(Shared.Configuration.ReceiverEndpointName);

            configuration.UseTransport<SqlServerTransport>().ConnectionString(() => Shared.Configuration.ConnectionString);

            configuration.UsePersistence<InMemoryPersistence>();

            configuration.EnableInstallers();

            var endpoint = await Endpoint.Start(configuration);

            await Task.Delay(TimeSpan.FromHours(1));
        }

        class TestHandler : IHandleMessages<TestCommand>{
            public Task Handle(TestCommand message, IMessageHandlerContext context)
            {
                return Task.CompletedTask;
            }
        }
    }
}
