namespace OutOfProcModel.Abstractions.Worker;

public interface IWorkerChannelFactory
{
    IWorkerChannel CreateWorkerChannel();
}