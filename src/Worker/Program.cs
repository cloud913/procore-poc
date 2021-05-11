using Amazon.SQS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Json;

namespace Worker
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    var loggerConfig = new LoggerConfiguration()
#if DEBUG
                        .MinimumLevel.Debug()
#endif
                        .Enrich.FromLogContext()
                        .Enrich.WithProperty("Application", "Worker")
                        .WriteTo.Console(new JsonFormatter());


                    Log.Logger = loggerConfig.CreateLogger();
                    logging.AddSerilog();
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<Worker>();
                    services.AddLogging();

                    services.AddSingleton<IAmazonSQS, AmazonSQSClient>();
                    services.AddSingleton(new WorkerConfiguration
                    {
                        QueueUrl = hostContext.Configuration["QueueUrl"]
                    });
                });
    }
}
