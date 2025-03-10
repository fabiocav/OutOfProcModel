namespace OutOfProcModel.Abstractions.Core;

public interface IEventProcessor
{
    ValueTask<T> ProcessEvent<T>(string context);
}