using Microsoft.Extensions.DependencyInjection;

namespace Trexon.Data;

/// <summary>Registers the data source and request-scoped sessions in Microsoft dependency injection.</summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>Registers one singleton data source and one session per service scope. Call UpdateAsync explicitly at startup.</summary>
    public static IServiceCollection AddTrexonData(this IServiceCollection services, Action<DataConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddSingleton(_ => Data.Create(configure));
        services.AddScoped(provider => provider.GetRequiredService<Data>().Open());
        return services;
    }
}
