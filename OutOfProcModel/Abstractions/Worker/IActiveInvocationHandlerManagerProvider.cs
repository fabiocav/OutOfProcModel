namespace OutOfProcModel.Abstractions.Worker;

public interface IActiveInvocationHandlerManagerProvider
{
    Task<IInvocationHandlerManager> GetActiveManagerAsync(string applicationId);
}