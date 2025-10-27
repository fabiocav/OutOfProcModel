using Microsoft.Extensions.DependencyInjection;

namespace OutOfProcModel.Mock;

public class JobHostScopedServiceProviderFactory : IServiceProviderFactory<IServiceCollection>
{
    private readonly IServiceProvider _rootProvider;
    private readonly IServiceCollection _rootServices;

    public JobHostScopedServiceProviderFactory(IServiceProvider rootProvider, IServiceCollection rootServices)
    {
        _rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
        _rootServices = rootServices ?? throw new ArgumentNullException(nameof(rootServices));
    }

    public IServiceCollection CreateBuilder(IServiceCollection services)
    {
        return services;
    }

    /// <summary>
    /// This creates the service provider *and the end* of spinning up the JobHost.
    /// When we build the ScriptHost (<see cref="DefaultScriptHostBuilder.BuildHost(bool, bool)"/>),
    /// all services are fed in here (<paramref name="services"/>), and using that list we build
    /// a provider that has all of the base level services that we want to copy, then adds all of the
    /// SciptHost level services on top. It is not a proxying provider, we are copying the services
    /// references into that (rarely - e.g. startup and specialization) created ScriptHost layer scope.
    /// </summary>
    /// <param name="services">The ScriptHost services to add on top of the copied root services.</param>
    /// <returns>A provider containing the superset of base (application) level services and ScriptHost servics.</returns>
    /// <exception cref="HostInitializationException">If service validation fails (e.g. user touches something they shouldn't have.</exception>
    public IServiceProvider CreateServiceProvider(IServiceCollection services)
    {
        // Start from the root (web app level) as a base
        var jobHostServices = _rootProvider.CreateChildContainer(_rootServices);

        // ...and then add all the child services to this container
        foreach (var service in services)
        {
            jobHostServices.Add(service);
        }

        return jobHostServices.BuildServiceProvider();
    }
}
