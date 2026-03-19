using System.CommandLine;

namespace FhirAugury.Cli.Commands;

public static class ServiceCommand
{
    public static Command Create()
    {
        var command = new Command("service", "Interact with a running FHIR Augury service");

        var serviceOption = new Option<string>("--service", "-s")
        {
            Description = "Service base URL",
            DefaultValueFactory = _ => "http://localhost:5100",
        };
        command.Add(serviceOption);

        command.Add(CreateStatusCommand(serviceOption));
        command.Add(CreateTriggerCommand(serviceOption));
        command.Add(CreateScheduleCommand(serviceOption));
        command.Add(CreateSearchCommand(serviceOption));
        command.Add(CreateStatsCommand(serviceOption));

        return command;
    }

    private static Command CreateStatusCommand(Option<string> serviceOption)
    {
        var command = new Command("status", "Check service health and ingestion status");

        command.SetAction(async (parseResult, ct) =>
        {
            using var client = CreateClient(parseResult, serviceOption);

            try
            {
                var health = await client.GetHealthAsync(ct);
                Console.WriteLine("Service Health:");
                ServiceClient.PrintJson(health);
                Console.WriteLine();

                var status = await client.GetStatusAsync(ct);
                Console.WriteLine("Ingestion Status:");
                ServiceClient.PrintJson(status);
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Failed to connect to service: {ex.Message}");
            }
        });

        return command;
    }

    private static Command CreateTriggerCommand(Option<string> serviceOption)
    {
        var command = new Command("trigger", "Trigger an ingestion sync");

        var sourceOption = new Option<string?>("--source") { Description = "Source to sync (omit for all)" };
        var typeOption = new Option<string>("--type") { Description = "Ingestion type", DefaultValueFactory = _ => "Incremental" };
        command.Add(sourceOption);
        command.Add(typeOption);

        command.SetAction(async (parseResult, ct) =>
        {
            using var client = CreateClient(parseResult, serviceOption);
            var source = parseResult.GetValue(sourceOption);
            var type = parseResult.GetValue(typeOption)!;

            try
            {
                if (string.IsNullOrEmpty(source))
                {
                    var result = await client.TriggerSyncAllAsync(ct: ct);
                    Console.WriteLine("Sync triggered for all sources:");
                    ServiceClient.PrintJson(result);
                }
                else
                {
                    var result = await client.TriggerIngestionAsync(source, type, ct: ct);
                    Console.WriteLine($"Ingestion triggered for {source}:");
                    ServiceClient.PrintJson(result);
                }
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Failed: {ex.Message}");
            }
        });

        return command;
    }

    private static Command CreateScheduleCommand(Option<string> serviceOption)
    {
        var command = new Command("schedule", "View or update sync schedules");

        var sourceOption = new Option<string?>("--source") { Description = "Source to update (omit to view all)" };
        var intervalOption = new Option<string?>("--interval") { Description = "New interval (e.g., 00:30:00)" };
        command.Add(sourceOption);
        command.Add(intervalOption);

        command.SetAction(async (parseResult, ct) =>
        {
            using var client = CreateClient(parseResult, serviceOption);
            var source = parseResult.GetValue(sourceOption);
            var interval = parseResult.GetValue(intervalOption);

            try
            {
                if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(interval))
                {
                    var result = await client.UpdateScheduleAsync(source, interval, ct);
                    Console.WriteLine($"Schedule updated for {source}:");
                    ServiceClient.PrintJson(result);
                }
                else
                {
                    var result = await client.GetScheduleAsync(ct);
                    Console.WriteLine("Sync Schedules:");
                    ServiceClient.PrintJson(result);
                }
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Failed: {ex.Message}");
            }
        });

        return command;
    }

    private static Command CreateSearchCommand(Option<string> serviceOption)
    {
        var command = new Command("search", "Search via the service API");

        var queryOption = new Option<string>("--query", "-q") { Description = "Search query", Arity = ArgumentArity.ExactlyOne };
        var limitOption = new Option<int>("--limit", "-n") { Description = "Max results", DefaultValueFactory = _ => 20 };
        command.Add(queryOption);
        command.Add(limitOption);

        command.SetAction(async (parseResult, ct) =>
        {
            using var client = CreateClient(parseResult, serviceOption);
            var query = parseResult.GetValue(queryOption)!;
            var limit = parseResult.GetValue(limitOption);

            try
            {
                var result = await client.SearchAsync(query, limit: limit, ct: ct);
                ServiceClient.PrintJson(result);
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Failed: {ex.Message}");
            }
        });

        return command;
    }

    private static Command CreateStatsCommand(Option<string> serviceOption)
    {
        var command = new Command("stats", "Get database statistics via the service API");

        var sourceOption = new Option<string?>("--source") { Description = "Filter to source" };
        command.Add(sourceOption);

        command.SetAction(async (parseResult, ct) =>
        {
            using var client = CreateClient(parseResult, serviceOption);
            var source = parseResult.GetValue(sourceOption);

            try
            {
                var result = await client.GetStatsAsync(source, ct);
                ServiceClient.PrintJson(result);
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Failed: {ex.Message}");
            }
        });

        return command;
    }

    private static ServiceClient CreateClient(System.CommandLine.ParseResult parseResult, Option<string> serviceOption)
    {
        var baseUrl = parseResult.GetValue(serviceOption)!;
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return new ServiceClient(httpClient);
    }
}
