var builder = DistributedApplication.CreateBuilder(args);

var placeholderMode = false;

var functionsHost = builder.AddProject<Projects.OutOfProcModel_FunctionsHost>("functionshost")
    .WithEnvironment("IsPlaceholderMode", placeholderMode ? "1" : "0");

var workerController = builder.AddProject<Projects.OutOfProcModel_WorkerController>("workercontroller")
    .WithReference(functionsHost);

var blazorClient = builder.AddProject<Projects.OutOfProcModel_Blazor>("blazor")
    .WithReference(workerController)
    .WithReference(functionsHost);

builder.Build().Run();
