using Grpc.Net.Client;
using OutOfProcModel.Grpc.Abstractions;
using OutOfProcModel.WorkerController;
using ProtoBuf.Grpc.Client;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OutOfProcModel.Abstractions.ControlPlane;

internal class DefaultWorkerController : IWorkerController
{
    private readonly ILogger<DefaultWorkerController> _logger;
    private readonly MockWorkerFactory _workerFactory;

    private readonly Dictionary<ApplicationContext, EnvironmentCount> _workerTargets = [];

    // For every placeholder, we track a list of target urls to ping for specialization
    private readonly Dictionary<RuntimeEnvironment, List<PlaceholderState>> _placeholders = [];

    // For every ApplicationId, we track the current worker states by workerId.
    private readonly Dictionary<string, Dictionary<string, ControllerWorkerState>> _workerStates = [];

    private readonly string _hostUri;

    private IFunctionsAdminGrpcService? _adminService;

    public DefaultWorkerController(MockWorkerFactory workerFactory, IConfiguration configuration, ILogger<DefaultWorkerController> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerFactory = workerFactory ?? throw new ArgumentNullException(nameof(workerFactory));

        var hostUriPath = ConfigurationPath.Combine("services", "functionsHost", "http", "0");
        _hostUri = configuration[hostUriPath]!;
    }

    private IFunctionsAdminGrpcService GetFunctionsAdminGrpcService(string address)
    {
        if (_adminService is null) // there's only one for now...
        {
            GrpcClientFactory.AllowUnencryptedHttp2 = true;
            var http = GrpcChannel.ForAddress(address);
            _adminService = http.CreateGrpcService<IFunctionsAdminGrpcService>();
        }
        return _adminService;
    }

    private async Task EvaluateStateAsync()
    {
        // Only add one worker at a time; loop again if needed.
        bool evaluateAgain = false;

        foreach ((var appContext, var envCount) in _workerTargets)
        {
            if (!_workerStates.TryGetValue(appContext.ApplicationId, out var workerStateMap))
            {
                // We had no entries for this app -- create one
                workerStateMap = [];
                _workerStates[appContext.ApplicationId] = workerStateMap;
            }

            var currentCount = workerStateMap.Values.Count(s => s.Definition.RuntimeEnvironment == envCount.Environment && s.Status <= ControllerWorkerStatus.Running);
            if (currentCount < envCount.TargetCount)
            {
                await CreateWorkerStatesAndStartWorkers(appContext, envCount.Environment, workerStateMap);
                evaluateAgain = true;
            }
        }

        if (evaluateAgain)
        {
            // If we added a worker, we need to evaluate again.
            await Task.Delay(2000);
            _ = EvaluateStateAsync();
        }
        else
        {
            _logger.LogInformation("Worker evaluation completed. No more workers needed at this time.");
        }

        async Task CreateWorkerStatesAndStartWorkers(ApplicationContext context, RuntimeEnvironment environment, IDictionary<string, ControllerWorkerState> workerStateMap)
        {
            var workerId = $"w_{Guid.NewGuid().ToString()[..8]}";
            bool startWorker = true;

            // first -- do we have a placeholder we can use?
            var phEnvironment = environment with { IsPlaceholder = true };
            if (_placeholders.TryGetValue(phEnvironment, out var placeholderStates))
            {
                var phToUse = placeholderStates.FirstOrDefault(ph => ph.IsWarm);
                if (phToUse != null)
                {
                    // TODO -- gross. Clean this up.
                    foreach (var states in _placeholders.Values)
                    {
                        List<PlaceholderState> statesToRemove = [];
                        foreach (var phState in states)
                        {
                            // Nothing connected to this host should be an eligible placeholder anymore
                            if (phState.TargetUrl == phToUse.TargetUrl)
                            {
                                statesToRemove.Add(phState);
                            }
                        }

                        foreach (var stateToRemove in statesToRemove)
                        {
                            states.Remove(stateToRemove);
                        }
                    }

                    var adminService = GetFunctionsAdminGrpcService(phToUse.TargetUrl);
                    var specContext = new SpecializationContext(context.ApplicationId, context.ApplicationVersion, environment);
                    await adminService.SpecializeApplicationAsync(specContext);

                    workerId = phToUse.WorkerId;
                    startWorker = false;
                }
            }

            var workerDef = new ControllerWorkerDefinition(context, workerId, environment);
            var workerState = new ControllerWorkerState(workerDef);

            workerStateMap.TryAdd(workerState.Definition.WorkerId, workerState);

            if (startWorker)
            {
                await StartNewWorkerAsync(workerDef);
            }

            await UpdateWorkerStateAsync(workerDef.ApplicationContext.ApplicationId, workerDef.WorkerId, ControllerWorkerStatus.Running);
        }
    }

    private async Task WarmupPlaceholderAsync(ControllerWorkerDefinition workerDef, PlaceholderState placeholderState)
    {
        var newWorker = _workerFactory.CreateWorker(workerDef);
        await newWorker.StartAsync();

        var warmupClient = GetFunctionsAdminGrpcService(placeholderState.TargetUrl);

        var invokeContext = new InvokeContext { ApplicationId = workerDef.ApplicationContext.ApplicationId };

        var invokeTask = warmupClient.InvokeAsync(invokeContext);
        placeholderState.WarmupSent = true;
        var result = await invokeTask;

        while (result.InvocationId == string.Empty)
        {
            await Task.Delay(1000);
            result = await warmupClient.InvokeAsync(invokeContext);
        }

        placeholderState.IsWarm = true;
    }

    private async Task StartNewWorkerAsync(ControllerWorkerDefinition workerDef)
    {
        var newWorker = _workerFactory.CreateWorker(workerDef);
        await newWorker.StartAsync();
    }

    public bool IncrementWorkerTarget(ApplicationContext context, RuntimeEnvironment environment)
    {
        if (environment.IsPlaceholder)
        {
            // If the environment is a placeholder, we need to track it separately
            if (!_placeholders.TryGetValue(environment, out var placeholderStates))
            {
                placeholderStates = [];
                _placeholders[environment] = placeholderStates;
            }

            var state = new PlaceholderState(workerId: $"w_{Guid.NewGuid().ToString()[..8]}", targetUrl: _hostUri);
            placeholderStates.Add(state);

            // placeholders aren't part of the normal worker targets, so we don't increment the workerTargets dictionary here.
            // instead, just start them directly
            _ = WarmupPlaceholderAsync(new ControllerWorkerDefinition(context, state.WorkerId, environment), state);

            return true;
        }

        if (!_workerTargets.TryGetValue(context, out var environmentCount))
        {
            environmentCount = new(environment, 0);
            _workerTargets[context] = environmentCount;
        }

        environmentCount.TargetCount++;

        // Force an evaluation.
        _ = EvaluateStateAsync();

        return true;
    }

    public bool DecrementWorkerTarget(ApplicationContext context, RuntimeEnvironment environment)
    {
        if (!_workerTargets.TryGetValue(context, out var environmentCount))
        {
            return false; // No targets to decrement
        }

        if (environmentCount.TargetCount <= 0)
        {
            environmentCount.TargetCount = 0;
            return false;
        }

        environmentCount.TargetCount--;

        // Force an evaluation.
        _ = EvaluateStateAsync();
        return true;
    }

    public Task UpdateWorkerStateAsync(string applicationId, string workerId, ControllerWorkerStatus status)
    {
        if (!_workerStates.TryGetValue(applicationId, out var workerStates))
        {
            return Task.CompletedTask;
        }

        if (!workerStates.TryGetValue(workerId, out var currentState))
        {
            return Task.CompletedTask;
        }

        // now we know we've got something to update
        currentState.Status = status;

        _logger.LogInformation($"Application '{applicationId}' updated worker state '{currentState.Definition.WorkerId}' to '{currentState.Status}'");

        _ = EvaluateStateAsync();
        return Task.CompletedTask;
    }

    public string GetStatus()
    {
        var workerTargetsSerialize = new Dictionary<string, object>();

        foreach ((var appContext, var environmentCount) in _workerTargets)
        {
            workerTargetsSerialize[appContext.ToString()] = environmentCount.ToString();
        }

        var phState = _placeholders.ToDictionary(
            kvp => kvp.Key.ToString(),
            kvp => kvp.Value.Select(ph => new { ph.WorkerId, ph.IsWarm }).ToList()
        );

        var status = new
        {
            workerStates = _workerStates,
            workerTargets = workerTargetsSerialize,
            placeholderStates = phState
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());

        return JsonSerializer.Serialize(status, options);
    }
}

internal class PlaceholderState(string workerId, string targetUrl)
{
    public string WorkerId { get; } = workerId;

    public string TargetUrl { get; } = targetUrl;

    public bool WarmupSent { get; set; } = false;

    public bool IsWarm { get; set; } = false;
}

internal class EnvironmentCount(RuntimeEnvironment environment, int targetCount)
{
    public RuntimeEnvironment Environment { get; } = environment;

    public int TargetCount { get; set; } = targetCount;

    public override string ToString() => $"{Environment} ({TargetCount})";
}