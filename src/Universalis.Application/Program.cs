using System;
using System.Threading;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Universalis.Application;

public static class Program
{
    public static void Main(string[] args)
    {
        // Increase the initial size of the thread pool
        var minThreadPoolSizeStr = Environment.GetEnvironmentVariable("UNIVERSALIS_MIN_THREADPOOL_SIZE");
        var minThreadPoolSize = int.TryParse(minThreadPoolSizeStr, out var minThreadPoolSizeParsed)
            ? minThreadPoolSizeParsed
            : 100;
        ThreadPool.SetMinThreads(minThreadPoolSize, minThreadPoolSize);

        var host = CreateHostBuilder(args).Build();

        // Run database migrations
        using (var scope = host.Services.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
        }

        host.Run();
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSimpleConsole();
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder
                    .UseStartup<Startup>()
                    .UseWebRoot(
#if DEBUG
                        "../Universalis.Mogboard.WebUI/wwwroot"
#else
                        "wwwroot/_content/Universalis.Mogboard.WebUI"
#endif
                    );
            });
}