using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NapcatQUI.Core.Adapter;
using NapcatQUI.Core.Configuration;
using NapcatQUI.Core.Database;
using NapcatQUI.Core.Database.Repositories;
using NapcatQUI.Core.Events;
using NapcatQUI.Core.Services;

namespace NapcatQUI.Core.Host;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNapcatQUICore(this IServiceCollection services, string appDataDir)
    {
        var dbPath = Path.Combine(appDataDir, "napcatqui.db");

        services.AddSingleton<ConfigManager>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ConfigManager>>();
            return new ConfigManager(appDataDir, logger);
        });

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DatabaseManager>>();
            return new DatabaseManager(dbPath, logger);
        });

        services.AddSingleton<MessageRepository>();
        services.AddSingleton<ContactRepository>();
        services.AddSingleton<GroupRepository>();
        services.AddSingleton<AccountRepository>();

        services.AddSingleton<OneBotMessageParser>();

        services.AddSingleton<ContactSyncService>();
        services.AddSingleton<HistoryService>();

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ImageCacheService>>();
            return new ImageCacheService(appDataDir, logger);
        });

        services.AddSingleton<EventBus>();

        services.AddSingleton<AccountManager>();

        services.AddSingleton<NapcatQUIBackgroundService>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            sp => sp.GetRequiredService<NapcatQUIBackgroundService>());

        return services;
    }
}
