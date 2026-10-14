namespace OrbitalSentinel.Workers;

public static class WorkerServices
{
    public static IServiceCollection AddOrbitalWorkers(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHostedService<IngestionWorker>();
        services.AddHostedService<CatalogMetadataWorker>();
        services.AddHostedService<OutboxPublisher>();
        if (configuration.GetValue<bool>("Prediction:PrecomputeTrajectories"))
            services.AddHostedService<PropagationWorker>();
        services.AddHostedService<PredictionRunCoordinator>();
        services.AddHostedService<PredictionRunConsumer>();
        services.AddHostedService<ConjunctionWorker>();
        return services;
    }
}
