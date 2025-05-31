namespace OutOfProcModel.Abstractions.Worker;

public interface IHandlerResolver
{
    IInvocationHandler? ResolveHandler(string context);
}