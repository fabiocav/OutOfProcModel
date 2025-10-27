using Microsoft.AspNetCore.Server.Kestrel.Core;
using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.FunctionsHost.Grpc;
using OutOfProcModel.Mock;
using OutOfProcModel.Workers;
using ProtoBuf.Grpc.Server;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddCodeFirstGrpc();

builder.Services.AddOptions<WebHostOptions>()
    .Configure(o =>
    {
        var envVar = Environment.GetEnvironmentVariable("IsPlaceholderMode");
        o.IsPlaceholderMode = envVar is not null && envVar == "1";
    });

// Set up the inner JobHost stuff
builder.Services.AddSingleton<JobHostManager>();
builder.Services.AddSingleton<IJobHostManager>(s => s.GetRequiredService<JobHostManager>());
builder.Services.AddSingleton<IServiceCollection>(builder.Services);

// This reaches into current JobHost to pull out current HandlerManager
builder.Services.AddSingleton<IActiveWorkerManagerProvider, DefaultActiveWorkerManagerProvider>();

builder.Services.AddOptions<KestrelServerOptions>()
.Configure<IConfiguration>((options, config) =>
{
    var url = config["ASPNETCORE_URLS"]!.Split(";")[1]; // http
    var uri = new Uri(url);
    options.ListenLocalhost(uri.Port, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

builder.Services.AddSingleton<FunctionsHostGrpcService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
// app.UseHttpsRedirection();

app.MapGrpcService<FunctionsHostGrpcService>();
app.MapGrpcService<FunctionsAdminGrpcService>();

app.Run();