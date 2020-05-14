using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace NServiceBus.SqlTransport.Tests.Herd
{
    class Program
    {
        static void Main(int endpointsNumber = 2, string senderPath = null, string receiverPath = null)
        {
            senderPath = senderPath ?? "..\\..\\..\\..\\NServiceBus.SqlTransport.Tests.Sender\\bin\\Debug\\netcoreapp3.1\\win-x64";
            receiverPath = receiverPath ?? "..\\..\\..\\..\\NServiceBus.SqlTransport.Tests.Receiver\\bin\\Debug\\netcoreapp3.1\\win-x64";

            var senderExe = $"{senderPath}\\{typeof(Sender.Program).Assembly.GetName().Name}.exe";
            var receiverExe = $"{receiverPath}\\{typeof(Receiver.Program).Assembly.GetName().Name}.exe";

            var processes = new List<Process>();

            for (var i = 0; i < endpointsNumber; i++)
            {
                var endpointProcesses =  StartEndpointProcesses(senderExe, receiverExe, i);

                processes.AddRange(endpointProcesses);
            }

            Console.WriteLine("press <enter> to stop ...");

            Console.ReadLine();

            Console.WriteLine("Stopping...");

            foreach (var process in processes)
            {
                process.Kill();
            }

            Console.WriteLine("Done.");
        }

        static Process[] StartEndpointProcesses(string senderExe, string receiverExe, int endpointNumber)
        {
            var receiver = StartProcess(
                receiverExe, 
                $"--dump-interval 0 --endpoint-name {Shared.Configuration.ReceiverEndpointName}-Herd-{endpointNumber}", 
                $"Herd-Receiver-{endpointNumber}"
                );

            var sender = StartProcess(
                senderExe, 
                $"send --number-of-tasks 1 --send-delay-ms 30000 --destination {Shared.Configuration.ReceiverEndpointName}-Herd-{endpointNumber}", 
                $"Herd-Sender-{endpointNumber}"
            );

            return new []{sender, receiver};
        }

        static Process StartProcess(string fileName, string arguments, string processName)
        {
            var process = new Process
            {
                StartInfo =
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            DataReceivedEventHandler outputHandler = (o, args) =>
            {
                Console.WriteLine($"[{processName}] {args.Data}");
            };

            process.OutputDataReceived += outputHandler;
            process.ErrorDataReceived += outputHandler;

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return process;
        }
    }
}
