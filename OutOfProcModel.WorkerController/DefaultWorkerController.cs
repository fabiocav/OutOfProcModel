using System.Text.Json;
using System.Text.Json.Serialization;
using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.WorkerController;

namespace OutOfProcModel.Abstractions.ControlPlane;

public class DefaultWorkerController(MockWorkerFactory workerFactory, ILogger<DefaultWorkerController> logger) : IWorkerController
{
    private readonly ILogger<DefaultWorkerController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly MockWorkerFactory _workerFactory = workerFactory ?? throw new ArgumentNullException(nameof(workerFactory));

    // Map ApplicationContext to RuntimeEnvironments. Normal apps will only ever have one environment map here, but 
    // placeholders will all share an ApplicationContext for different Runtimes.
    private readonly Dictionary<ApplicationContext, Dictionary<RuntimeEnvironment, int>> _workerTargets = [];

    // For every ApplicationId, we track the current worker states by workerId.
    private readonly Dictionary<string, Dictionary<string, WorkerState>> _workerStates = [];

    private async Task EvaluateStateAsync()
    {
        // Only add one worker at a time; loop again if needed.
        bool evaluateAgain = false;

        foreach ((var context, var environments) in _workerTargets)
        {
            foreach ((var environment, int targetCount) in environments)
            {
                if (!_workerStates.TryGetValue(context.ApplicationId, out var workerStateMap))
                {
                    // We had no entries for this app -- create one
                    workerStateMap = [];
                    _workerStates[context.ApplicationId] = workerStateMap;
                }

                var currentCount = workerStateMap.Values.Count(s => s.RuntimeEnvironment == environment && s.Status <= WorkerStatus.Running);
                if (currentCount < targetCount)
                {
                    await CreateWorkerStatesAndStartWorkers(context, environment, workerStateMap);
                    evaluateAgain = true;
                }
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

        async Task CreateWorkerStatesAndStartWorkers(ApplicationContext context, RuntimeEnvironment environment, IDictionary<string, WorkerState> workerStateMap)
        {
            var workerId = $"w_{Guid.NewGuid().ToString()[..8]}";

            var workerState = new WorkerState(context.ApplicationId, context.ApplicationVersion, workerId, environment);
            workerStateMap.TryAdd(workerState.WorkerId, workerState);
            await StartNewWorkerAsync(workerState);
        }
    }

    private async Task StartNewWorkerAsync(WorkerState workerState)
    {
        await UpdateWorkerStateAsync(workerState.ApplicationId, workerState.WorkerId, WorkerStatus.Created);

        var newWorker = _workerFactory.CreateWorker(workerState);
        await newWorker.StartAsync();

        await UpdateWorkerStateAsync(workerState.ApplicationId, workerState.WorkerId, workerState.RuntimeEnvironment.IsPlaceholder ? WorkerStatus.RunningAsPlaceholder : WorkerStatus.Running);
    }

    public bool IncrementWorkerTarget(ApplicationContext context, RuntimeEnvironment environment)
    {
        if (!_workerTargets.TryGetValue(context, out var existingEnvironments))
        {
            existingEnvironments = new Dictionary<RuntimeEnvironment, int>() { { environment, 0 } };
            _workerTargets[context] = existingEnvironments;
        }

        if (!existingEnvironments.TryGetValue(environment, out var currentCount))
        {
            existingEnvironments[environment] = 1;
        }
        else
        {
            existingEnvironments[environment]++;
        }

        // TODO -- non-placeholders cannot have multiple environments

        // Force an evaluation.
        _ = EvaluateStateAsync();

        return true;
    }

    public void SpecializeWorker(ApplicationContext context, RuntimeEnvironment environment)
    {
        if (!_workerTargets.TryGetValue(context, out var existingEnvironments) || !existingEnvironments.ContainsKey(environment))
        {
            throw new InvalidOperationException($"No worker target found for context {context} and environment {environment}.");
        }

        // Update the environment to be specialized
        existingEnvironments[environment] = 1; // Reset to 1 since we are specializing

        _ = EvaluateStateAsync();
    }

    public bool DecrementWorkerTarget(ApplicationContext context, RuntimeEnvironment environment)
    {
        if (!_workerTargets.TryGetValue(context, out var existingEnvironments))
        {
            return false; // No targets to decrement
        }

        if (!existingEnvironments.TryGetValue(environment, out var currentCount) || currentCount <= 0)
        {
            return false; // No targets to decrement
        }

        if (currentCount == 1)
        {
            existingEnvironments.Remove(environment);
            if (existingEnvironments.Count == 0)
            {
                _workerTargets.Remove(context);
            }
        }
        else
        {
            existingEnvironments[environment] = currentCount - 1;
        }

        // Force an evaluation.
        _ = EvaluateStateAsync();
        return true;
    }

    public Task UpdateWorkerStateAsync(string applicationId, string workerId, WorkerStatus status)
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

        _logger.LogInformation($"Application '{applicationId}' updated worker state '{currentState.WorkerId}' to '{currentState.Status}'");

        _ = EvaluateStateAsync();
        return Task.CompletedTask;
    }

    public string GetStatus()
    {
        var workerTargetsSerialize = new Dictionary<string, object>();

        foreach ((var appContext, var states) in _workerTargets)
        {
            workerTargetsSerialize[appContext.ToString()] = states.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);
        }

        var status = new
        {
            workerStates = _workerStates,
            workerTargets = workerTargetsSerialize
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());

        return JsonSerializer.Serialize(status, options);
    }
}