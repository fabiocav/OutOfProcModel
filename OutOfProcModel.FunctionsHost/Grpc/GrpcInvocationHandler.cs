using System.Collections.Concurrent;
using System.Threading.Channels;
using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.FunctionsHost.Grpc;

internal class GrpcInvocationHandler : IInvocationHandler, IDisposable
{
    private readonly ChannelWriter<FunctionsGrpcMessage> _outbound;
    private readonly ChannelReader<FunctionsGrpcMessage> _inbound;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<InvocationResult>> _executingInvocations = [];

    private readonly ILogger<GrpcInvocationHandler> _logger;

    public GrpcInvocationHandler(string applicationId, string workerId, string version,
        ChannelWriter<FunctionsGrpcMessage> outbound, ChannelReader<FunctionsGrpcMessage> inbound, ILogger<GrpcInvocationHandler> logger)
    {
        WorkerId = workerId;
        ApplicationId = applicationId;
        ApplicationVersion = version;

        _outbound = outbound;
        _inbound = inbound;
        _ = ProcessInbound(); // we can get things like logs, at any point from worker process        

        _logger = logger;
    }

    private async Task ProcessInbound()
    {
        while (await _inbound.WaitToReadAsync())
        {
            while (_inbound.TryRead(out var item))
            {
                switch (item.MessageType)
                {
                    case "InvocationResponse":
                        DispatchMessage(item);
                        break;
                    default:
                        break;
                }
            }
        }

        _outbound.TryComplete(); // signal that we are done processing
        await _inbound.Completion;
        _logger.LogInformation("Handler for {WorkerId} has finished processing messages.", WorkerId);
    }

    private void DispatchMessage(FunctionsGrpcMessage item)
    {
        ThreadPool.QueueUserWorkItem(state => ProcessItem((FunctionsGrpcMessage)state!), item);
    }

    private void ProcessItem(FunctionsGrpcMessage msg)
    {
        var id = msg.Properties[FunctionsGrpcMessage.FunctionInvocationId];
        if (_executingInvocations.TryRemove(id, out var tcs))
        {
            tcs.TrySetResult(new InvocationResult(id, msg.Properties["Result"]));
        }
        else
        {
            _logger.LogError($"No executing invocation found for ID: {msg.Id}");
        }
    }

    public string WorkerId { get; }

    public string ApplicationId { get; }

    public string ApplicationVersion { get; }

    public IEnumerable<string> Capabilities { get; } = [];

    public async ValueTask<InvocationResult> ProcessEvent(InvocationContext context)
    {
        // Do more real stuff here
        TaskCompletionSource<InvocationResult> tcs = new();
        _executingInvocations.TryAdd(context.InvocationId, tcs);

        await _outbound.WriteAsync(new FunctionsGrpcMessage
        {
            MessageType = FunctionsGrpcMessage.InvocationRequest,
            Id = context.InvocationId,
            Properties =
            {
                [FunctionsGrpcMessage.FunctionInvocationId] = context.InvocationId,
                ["Data"] = context.Data,
            },
        });

        return await tcs.Task;
    }

    public void Dispose()
    {
        // do more...
    }
}