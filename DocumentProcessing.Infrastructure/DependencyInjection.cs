using DocumentProcessing.Core.Tools;
using DocumentProcessing.Infrastructure.Options;
using DocumentProcessing.Infrastructure.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentProcessing.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDocumentProcessingInfrastructure(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddOptions<DocDbOptions>()
            .Bind(config.GetSection(DocDbOptions.SectionName))
            .PostConfigure(opt =>
            {
                if (string.IsNullOrWhiteSpace(opt.ConnectionString))
                    opt.ConnectionString = config.GetConnectionString("DocDb") ?? string.Empty;
            });

        services.AddSingleton<ISqlTool, SqlTool>();
        services.AddSingleton<IOcrTool, OcrTool>();
        services.AddSingleton<IComplianceRulesTool, ComplianceRulesTool>();
        return services;
    }
}
