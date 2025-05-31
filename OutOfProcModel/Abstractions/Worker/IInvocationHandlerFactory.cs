namespace OutOfProcModel.Abstractions.Worker;

public interface IInvocationHandlerProvider
{
    ValueTask<IInvocationHandler> Create(HandlerCreationContext context);
}