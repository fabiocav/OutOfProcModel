using System.Collections.Concurrent;
using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Workers;

internal class DefaultInvocationHandlerManager : IInvocationHandlerManager, IDisposable
{
    // Dictionary mapping applicationId to a list of workers:
    private readonly ConcurrentDictionary<string, IList<IInvocationHandler>> _handlers = new();
    private IInvocationHandlerProvider _handlerProvider;

    public DefaultInvocationHandlerManager(IInvocationHandlerProvider handlerProvider)
    {
        _handlerProvider = handlerProvider;
    }

    public async ValueTask AddHandlerAsync(HandlerCreationContext handlerCreationContext)
    {
        // this is JobHost-scoped, so ensure that we own lifetime of handlers fully
        var handler = await _handlerProvider.Create(handlerCreationContext);
        var appHandlers = _handlers.GetOrAdd(handler.ApplicationId, []);
        appHandlers.Add(handler);
    }

    public bool RemoveHandler(IInvocationHandler worker)
    {
        var appHandlers = _handlers.GetOrAdd(worker.ApplicationId, new List<IInvocationHandler>());
        return appHandlers.Remove(worker);
    }

    public IReadOnlyCollection<IInvocationHandler> GetHandlers(string applicationId)
    {
        if (_handlers.TryGetValue(applicationId, out var handlers))
        {
            return handlers.AsReadOnly();
        }

        return [];
    }

    public void Dispose()
    {
        foreach (var handlers in _handlers.Values)
        {
            foreach (var handler in handlers)
            {
                // Dispose of the worker if it implements IDisposable
                if (handler is IDisposable disposableWorker)
                {
                    disposableWorker.Dispose();
                }
            }
        }
    }
}