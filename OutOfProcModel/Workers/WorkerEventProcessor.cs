using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Workers;

public class WorkerEventProcessor(IHandlerResolver handlerResolver) : IEventProcessor
{
    private readonly IHandlerResolver _handlerResolver = handlerResolver;

    public async ValueTask<EventResult> ProcessEvent(EventContext context)
    {
        var worker = _handlerResolver.ResolveHandler(context.ApplicationId);

        if (worker == null)
        {
            throw new InvalidOperationException("No worker available");
        }

        // what is T going to be here? 
        // Should it be some kind of "Result" object? What if we go to process it, but it's suddenly 
        // now draining... (a race between resolving and processing) should we resolve another worker and try again?
        var result = await worker.ProcessEvent(context.InvocationContext);

        return new EventResult(result, worker.WorkerId);
    }
}