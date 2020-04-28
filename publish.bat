rmdir /s /q output 

dotnet publish ./src/NServiceBus.SqlTransport.Tests.Receiver  -o ./output/receiver --self-contained true --runtime win-x64
dotnet publish ./src/NServiceBus.SqlTransport.Tests.Sender  -o ./output/sender --self-contained true --runtime win-x64
dotnet publish ./src/NServiceBus.SqlTransport.Tests.Monitor  -o ./output/monitor --self-contained true --runtime win-x64

cd ./output
powershell Compress-Archive . publish.zip