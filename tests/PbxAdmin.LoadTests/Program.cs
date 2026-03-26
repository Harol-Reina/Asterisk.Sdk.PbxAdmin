using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
// Services will be registered here as we build components
var app = builder.Build();
Console.WriteLine("PbxAdmin SDK Test Platform v0.1");
Console.WriteLine("Usage: dotnet run -- --scenario <name> --agents <N> --target <realtime|file>");
