using pdf_extractor.Configuration;
using pdf_order_service;


IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "PDF Order Service";
    })
    .ConfigureAppConfiguration((hostingContext, config) =>
    {
        // Load appsettings.json
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    })
    .ConfigureLogging(logging =>
    {
        logging.AddEventLog(settings => settings.SourceName = "PDFOrderService");
    })
    .ConfigureServices((context, services) =>
    {
        // Bind AppCredentials from configuration
        services.Configure<AppCredentialsOptions>(
            context.Configuration.GetSection("AppCredentials"));

        // Register Worker
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();