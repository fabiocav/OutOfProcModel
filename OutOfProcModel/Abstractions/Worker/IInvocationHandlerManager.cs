namespace OutOfProcModel.Abstractions.Worker;

// JobHost-scoped. 
// Retrieved by WebHost-level Grpc server when a new connection is made.
public interface IInvocationHandlerManager
{
    ValueTask AddHandlerAsync(HandlerCreationContext handler);

    bool RemoveHandler(IInvocationHandler handler);

    IReadOnlyCollection<IInvocationHandler> GetHandlers(string applicationId);
}