using OutOfProcModel.Abstractions.Worker;
using System.Threading.Channels;

namespace OutOfProcModel.FunctionsHost.Grpc;

public class GrpcFunctionMetadataFactory(string workerId, ChannelWriter<GrpcToWorker> writeToWorker) : IFunctionMetadataFactory, IMessageHandler
{
    private readonly string _workerId = workerId ?? throw new ArgumentNullException(nameof(workerId));
    private readonly ChannelWriter<GrpcToWorker> _writeToWorker = writeToWorker ?? throw new ArgumentNullException(nameof(writeToWorker));

    private readonly TaskCompletionSource<IEnumerable<string>> _metadataTaskCompletionSource = new();

    public Task<IEnumerable<string>> GetFunctionMetadataAsync()
    {
        _writeToWorker.TryWrite(new GrpcToWorker { Id = _workerId, MessageType = FunctionsGrpcMessage.MetadataRequest });
        return _metadataTaskCompletionSource.Task;
    }

    public ValueTask<bool> HandleMessage(MessageFromWorker message)
    {
        if (message.MessageType == FunctionsGrpcMessage.MetadataResponse)
        {
            _metadataTaskCompletionSource.TrySetResult(message.Properties.Keys); // mocking this
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(false);
    }
}
