using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration.Json;
using QualityAviationCodeRepositoryConsoleClient.Console.Services;

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

var serviceProvider = new ServiceCollection()
    .AddSingleton<IConfiguration>(config)
    .AddScoped<IApiService, ApiService>()
    .AddScoped<IFileService, FileService>()
    .AddScoped<IConsoleUI, ConsoleUI>()
    .AddScoped<IAuthService, AuthService>()
    .BuildServiceProvider();

var consoleUI = serviceProvider.GetRequiredService<IConsoleUI>();
await consoleUI.Run();