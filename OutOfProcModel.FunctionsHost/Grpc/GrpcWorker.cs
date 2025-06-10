using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Abstractions.Worker;
using System.Collections.Concurrent;

namespace OutOfProcModel.FunctionsHost.Grpc;

internal class GrpcWorker : IWorker, IDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<InvocationResult>> _executingInvocations = [];

    private readonly ILogger<GrpcWorker> _logger;
    private readonly IWorkerChannel _channel;

    public GrpcWorker(string applicationId, string workerId, string version, IWorkerChannel channel, ILogger<GrpcWorker> logger)
    {
        WorkerId = workerId;
        ApplicationId = applicationId;
        ApplicationVersion = version;
        _channel = channel;

        _logger = logger;

        _ = StartReadLoopAsync();
    }

    private async Task StartReadLoopAsync()
    {
        Status = WorkerStatus.Running;

        await foreach (var message in _channel.ReadAsync())
        {
            var invocationId = message.Properties["FunctionInvocationId"];
            if (_executingInvocations.TryRemove(invocationId, out TaskCompletionSource<InvocationResult>? tcs))
            {
                tcs.TrySetResult(new InvocationResult(invocationId, message.Properties["Result"]));
            }
        }
    }

    public string WorkerId { get; }

    public string ApplicationId { get; }

    public string ApplicationVersion { get; }

    public IEnumerable<string> Capabilities { get; } = [];

    public WorkerStatus Status { get; private set; } = WorkerStatus.Created;

    public void Dispose()
    {
        // do more...
    }

    public async ValueTask<InvocationResult> ProcessEvent(InvocationContext context)
    {
        TaskCompletionSource<InvocationResult> tcs = new();
        _executingInvocations.TryAdd(context.InvocationId, tcs);

        await _channel.SendAsync(context);
        return await tcs.Task;
    }

    public Task DrainAsync(TimeSpan timeout)
    {
        Status = WorkerStatus.Draining;

        var tasks = _executingInvocations.Select(p => p.Value.Task);
        return Task.WhenAll(tasks); // todo -- some timeout stuff
    }
}