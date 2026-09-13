using Microsoft.Extensions.DependencyInjection;

namespace DocumentProcessing.Agents;

public static class DependencyInjection
{
    public static IServiceCollection AddDocumentProcessingAgents(this IServiceCollection services)
    {
        services.AddScoped<DocumentIntakeAgent>();
        services.AddScoped<OcrAgent>();
        services.AddScoped<ComplianceAgent>();
        services.AddScoped<CoordinatorAgent>();
        return services;
    }
}
