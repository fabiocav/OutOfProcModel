using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.FunctionsHost.Grpc;

public interface IMessageHandler
{
    ValueTask<bool> HandleMessage(MessageFromWorker message);
}
