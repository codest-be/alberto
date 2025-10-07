using Microsoft.Extensions.DependencyInjection;

namespace Alberto.CQRS.Registration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCQRS(
        this IServiceCollection services,
        Action<CQRSBuilder> configureModule)
    {
        var builder = new CQRSBuilder(services);
        configureModule(builder);
        return builder.Build();
    }
}