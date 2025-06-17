using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.Grpc.Abstractions;

namespace OutOfProcModel.FunctionsHost.Grpc;

public class GrpcFunctionMetadataFactory : IFunctionMetadataFactory, IMessageHandler
{
    private readonly string _applicationId;
    private readonly IWorkerChannelWriter _channelWriterProvider;

    private readonly TaskCompletionSource<IEnumerable<string>> _metadataTaskCompletionSource = new();
    private readonly Lazy<Task<IEnumerable<string>>> _metadataLazy;

    public GrpcFunctionMetadataFactory(string applicationId, IWorkerChannelWriterProvider channelWriterProvider)
    {
        _applicationId = applicationId;
        _channelWriterProvider = channelWriterProvider.GetWriter(applicationId);
        _metadataLazy = new(LoadMetadataAsync);
    }

    public Task<IEnumerable<string>> GetFunctionMetadataAsync()
    {
        return _metadataLazy.Value;
    }

    private Task<IEnumerable<string>> LoadMetadataAsync()
    {
        _channelWriterProvider.TryWrite(new MessageToWorker(_applicationId, FunctionsGrpcMessage.MetadataRequest, new Dictionary<string, string>()));
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
