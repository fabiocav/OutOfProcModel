//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Hosting;
//using OutOfProcModel.Abstractions.ControlPlane;
//using OutOfProcModel.Mock;

//// Build DI container:
//var builder = Host.CreateApplicationBuilder();


//builder.Services.AddSingleton<MockWorkerFactory>();

//// Acts like an external worker and external event source
//builder.Services.AddHostedService<ConsoleMockListener>();

//// Worker controller
//builder.Services.AddSingleton<DefaultWorkerController>();
//builder.Services.AddHostedService(s => s.GetRequiredService<DefaultWorkerController>());
//builder.Services.AddSingleton<IWorkerController>(s => s.GetRequiredService<DefaultWorkerController>());

//var host = builder.Build();

//host.Start();

//await host.WaitForShutdownAsync();
// Invocation flow:
// WebJobs -> Invoker -> EventProcessor.ProcessEvent(context)

// EventProcessor flow (default implementation):
//          ProcessEvent -> EventProcessorResolver (Worker based default) -> Processor.ProcessEvent(context);

// Default implementation of the worker based event processor:
//  -> ProcessEvent -> Resolver.ResolveWorker -> 