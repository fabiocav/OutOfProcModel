using OutOfProcModel.Abstractions.ControlPlane;
using OutOfProcModel.WorkerController;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<MockWorkerFactory>();

// Worker controller
builder.Services.AddSingleton<DefaultWorkerController>();

var app = builder.Build();

// Configure the HTTP request pipeline.
//app.UseHttpsRedirection();

var serializerOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

app.MapPost("/addworker", async (HttpRequest request, DefaultWorkerController workerController) =>
{
    var payload = await request.ReadFromJsonAsync<JsonElement>();
    var appContext = payload.GetProperty("applicationContext").Deserialize<ApplicationContext>(serializerOptions);
    var environment = payload.GetProperty("runtimeEnvironment").Deserialize<RuntimeEnvironment>(serializerOptions);

    workerController.IncrementWorkerTarget(appContext!, environment!);

    return Results.Accepted();
});

app.MapPost("/removeworker", async (HttpRequest request, DefaultWorkerController workerController) =>
{
    var payload = await request.ReadFromJsonAsync<JsonElement>();
    var appContext = payload.GetProperty("applicationContext").Deserialize<ApplicationContext>(serializerOptions);
    //workerController.DecrementWorkerTarget(appContext!, wo!);
    return Results.Accepted();
});

app.MapGet("/status", (DefaultWorkerController workerController) =>
{
    return Results.Ok(workerController.GetStatus());
});

app.Run();