// See https://aka.ms/new-console-template for more information

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.Mock;
using OutOfProcModel.Workers;

// Build DI container:
var builder = new HostApplicationBuilder();

builder.Services.AddSingleton<IWorkerManager, WorkerManager>();
builder.Services.AddSingleton<IWorkerResolver, DefaultWorkerResolver>();
builder.Services.AddSingleton<IEventProcessor, WorkerEventProcessor>();
builder.Services.AddSingleton<IWorkerFactory, DefaultWorkerFactory>();
builder.Services.AddHostedService<ConsoleMockListener>();
builder.Services.AddHostedService<EventGenerator>();

var host = builder.Build();

host.Start();

await host.WaitForShutdownAsync();
// Invocation flow:
// WebJobs -> Invoker -> EventProcessor.ProcessEvent(context)

// EventProcessor flow (default implementation):
//          ProcessEvent -> EventProcessorResolver (Worker based default) -> Processor.ProcessEvent(context);

// Default implementation of the worker based event processor:
//  -> ProcessEvent -> Resolver.ResolveWorker -> 