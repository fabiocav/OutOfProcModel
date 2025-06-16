using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Abstractions.Mock;
using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.Grpc.Abstractions;
using System.Collections.Concurrent;

namespace OutOfProcModel.FunctionsHost.Grpc;

internal class GrpcWorker : IWorker, IDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<InvocationResult>> _executingInvocations = [];
    private readonly Task _readLoopTask;
    private readonly CancellationTokenSource _readLoopCancellationSource = new();

    public GrpcWorker(WorkerDefinition workerDefinition, IWorkerChannel channel)
    {
        Definition = workerDefinition ?? throw new ArgumentNullException(nameof(workerDefinition));

        _channel = channel;
        _readLoopTask = StartReadLoopAsync(_readLoopCancellationSource.Token);
    }

    private readonly IWorkerChannel _channel;

    public WorkerDefinition Definition { get; }

    public WorkerStatus Status { get; private set; } = WorkerStatus.Created;

    private async Task StartReadLoopAsync(CancellationToken readLoopToken)
    {
        Status = WorkerStatus.Running;
        try
        {
            await foreach (var message in _channel.ReadAsync(readLoopToken))
            {
                if (message.MessageType == FunctionsGrpcMessage.InvocationResponse)
                {
                    var invocationId = message.Properties["FunctionInvocationId"];
                    if (_executingInvocations.TryRemove(invocationId, out TaskCompletionSource<InvocationResult>? tcs))
                    {
                        tcs.TrySetResult(new InvocationResult(invocationId, message.Properties["Result"]));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        // We'll never read from this again.
        await _channel.DisconnectAsync();
    }

    public ValueTask<InvocationResult> ProcessEvent(InvocationContext context)
    {
        if (Status != WorkerStatus.Running)
        {
            throw new InvalidOperationException($"Worker {Definition.WorkerId} is not running. Current status: {Status}");
        }

        TaskCompletionSource<InvocationResult> tcs = new();
        _executingInvocations.TryAdd(context.InvocationId, tcs);

        var properties = new Dictionary<string, string>
        {
            { FunctionsGrpcMessage.FunctionInvocationId, context.InvocationId },
            { "Data", context.Data }
        };

        _channel.TryWrite(new MessageToWorker(Definition.ApplicationId, FunctionsGrpcMessage.InvocationRequest, properties));
        return new ValueTask<InvocationResult>(tcs.Task);
    }

    // Waits for invocations to complete
    public async Task DrainAsync(TimeSpan timeout)
    {
        // This worker is now removed from load-balancing and will not receive new invocations
        Status = WorkerStatus.Draining;

        var tasks = _executingInvocations.Select(p => p.Value.Task);
        await Task.WhenAll(tasks); // todo -- some timeout stuff

        _readLoopCancellationSource.Cancel();
        await _readLoopTask;

        Status = WorkerStatus.Drained;
    }

    // Do not wait for invocations to complete
    private async Task StopAsync()
    {
        Status = WorkerStatus.Stopping;

        _readLoopCancellationSource.Cancel();
        await _readLoopTask;

        Status = WorkerStatus.Stopped;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}