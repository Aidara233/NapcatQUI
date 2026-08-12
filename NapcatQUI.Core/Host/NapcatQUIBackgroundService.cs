using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NapcatQUI.Core.Database;
using NapcatQUI.Core.Services;

namespace NapcatQUI.Core.Host;

public class NapcatQUIBackgroundService : BackgroundService
{
    private readonly AccountManager _accountManager;
    private readonly DatabaseManager _db;
    private readonly ILogger<NapcatQUIBackgroundService> _logger;

    public NapcatQUIBackgroundService(
        AccountManager accountManager,
        DatabaseManager db,
        ILogger<NapcatQUIBackgroundService> logger)
    {
        _accountManager = accountManager;
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NapcatQUI Core starting...");

        await _db.GetConnectionAsync();

        await _accountManager.InitializeAsync();
        await _accountManager.StartAllAsync(stoppingToken);

        _logger.LogInformation("NapcatQUI Core started, waiting for shutdown...");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("NapcatQUI Core stopping...");
        await _accountManager.DisposeAsync();
    }
}
