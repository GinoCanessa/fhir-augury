using FhirAugury.Processing.Common.Queue;
using Microsoft.Extensions.Hosting;

namespace FhirAugury.Processing.Common.Hosting;

public class ProcessingHostedService<TItem>(ProcessingQueueRunner<TItem> runner) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => runner.RunAsync(stoppingToken);
}
