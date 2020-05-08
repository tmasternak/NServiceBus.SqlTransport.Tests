using System;
using System.Diagnostics;
using System.IO;

namespace NServiceBus.SqlTransport.Tests.Herd
{
    class Program
    {
        static void Main(int endpointsNumber = 1, int receiversPerEndpoint = 1, string senderPath = null, string receiverPath = null)
        {
            senderPath = senderPath ?? "..\\..\\..\\..\\NServiceBus.SqlTransport.Tests.Sender\\bin\\Debug\\netcoreapp3.1";
            receiverPath = receiverPath ?? "..\\..\\..\\..\\NServiceBus.SqlTransport.Tests.Receiver\\bin\\Debug\\netcoreapp3.1";

            var senderExe = $"{senderPath}\\{typeof(Sender.Program).Assembly.GetName().Name}.exe";
            var receiverExe = $"{receiverPath}\\{typeof(Sender.Program).Assembly.GetName().Name}.exe";

            for (var i = 0; i < endpointsNumber; i++)
            {
                StartEndpointProcesses(senderExe, receiverExe, receiversPerEndpoint, i);
            }

            Console.WriteLine(Directory.GetCurrentDirectory());
            var sender = Process.Start(senderExe);

            sender?.WaitForExit();
        }

        static void StartEndpointProcesses(string senderExe, string receiverExe, int receiversPerEndpoint, int endpointNumber)
        {
            var senderName = $"{Shared.Configuration.SenderEndpointName}-Herd-{endpointNumber}";
            var receiverName = $"{Shared.Configuration.ReceiverEndpointName}-Herd-{endpointNumber}";

            var sender = Process.Start(senderExe);
            throw new NotImplementedException();
        }
    }
}
