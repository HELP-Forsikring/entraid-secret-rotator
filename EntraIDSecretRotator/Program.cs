using System.CommandLine;
using Azure.Identity;
using EntraIDSecretRotator.Commands;
using EntraIDSecretRotator.Infrastructure;
using EntraIDSecretRotator.Services;
using EntraIDSecretRotator.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Serilog;
using Serilog.Events;
using Serilog.Settings.Configuration;
using Serilog.Sinks.OpenTelemetry;
using Serilog.Templates;

namespace EntraIDSecretRotator;

/// <summary>
/// Main entry point for the EntraIDSecretRotator application.
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Setup configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var options = new RotatorOptions();
        configuration.GetSection(RotatorOptions.SectionName).Bind(options);

        // Setup Serilog with triple sinks
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var logPath = Environment.GetEnvironmentVariable("LOG_PATH") ?? "/tmp/log/entraid-rotator";

        // Ensure log directory exists
        Directory.CreateDirectory(logPath);

        // AOT-compatible configuration reader options - explicitly specify assemblies
        // These assemblies contain the Serilog sinks and enrichers used by this application
        var configReaderOptions = new ConfigurationReaderOptions(
            typeof(Serilog.LoggerConfiguration).Assembly,                // Serilog core
            typeof(Serilog.ConsoleLoggerConfigurationExtensions).Assembly, // Serilog.Sinks.Console
            typeof(Serilog.FileLoggerConfigurationExtensions).Assembly,   // Serilog.Sinks.File
            typeof(OtlpProtocol).Assembly,                                // Serilog.Sinks.OpenTelemetry
            typeof(ExpressionTemplate).Assembly);                         // Serilog.Expressions (templates)

        var loggerConfig = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration, configReaderOptions)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("service.name", "EntraIDSecretRotator")
            .Enrich.WithProperty("service.namespace", "entraid-secret-rotator");

        // Primary: OTLP sink if endpoint is configured
        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            loggerConfig.WriteTo.OpenTelemetry(opts =>
            {
                opts.Endpoint = otlpEndpoint;
                opts.Protocol = OtlpProtocol.Grpc;
                opts.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = "EntraIDSecretRotator",
                    ["service.namespace"] = "entraid-secret-rotator",
                    ["deployment.environment"] = Environment.GetEnvironmentVariable("ENVIRONMENT") ?? "development"
                };
            });
        }

        // JSON formatter that always includes level (required for Loki)
        var jsonTemplate = new ExpressionTemplate(
            "{ {Timestamp: @t, Level: @l, Exception: @x, MessageTemplate: @mt, Message: @m, Properties: @p} }\n");

        // Secondary: JSON console sink to stderr
        loggerConfig.WriteTo.Console(
            formatter: jsonTemplate,
            standardErrorFromLevel: LogEventLevel.Verbose);

        // Fallback: File sink for persistence
        loggerConfig.WriteTo.File(
            formatter: jsonTemplate,
            path: Path.Combine(logPath, "rotator-.json"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            fileSizeLimitBytes: 50_000_000,
            rollOnFileSizeLimit: true,
            flushToDiskInterval: TimeSpan.FromSeconds(1));

        Log.Logger = loggerConfig.CreateLogger();

        // Create logger factory for DI using Serilog
        using var loggerFactory = new LoggerFactory().AddSerilog(Log.Logger);

        // Setup OpenTelemetry
        using var meterProvider = TelemetrySetup.ConfigureMetrics();

        // Create Azure credential
        // Exclude credentials not available in minimal containers
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeEnvironmentCredential = false,          // Service Principal via env vars
            ExcludeWorkloadIdentityCredential = false,     // Kubernetes Workload Identity
            ExcludeManagedIdentityCredential = false,      // Azure Managed Identity
            ExcludeSharedTokenCacheCredential = true,      // Requires libsecret
            ExcludeVisualStudioCredential = true,          // Requires VS
            ExcludeAzureCliCredential = true,              // Requires az CLI binary
            ExcludeAzurePowerShellCredential = true,       // Requires PowerShell
            ExcludeAzureDeveloperCliCredential = true,     // Requires azd CLI binary
            ExcludeInteractiveBrowserCredential = true     // Requires browser
        });

        // Create Graph client
        var graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

        // Create services
        var metrics = new Metrics();
        var entraIdService = new EntraIdService(graphClient, loggerFactory.CreateLogger<EntraIdService>());
        var keyVaultNames = options.GetKeyVaults();
        if (keyVaultNames.Count == 0)
        {
            Console.Error.WriteLine("ERROR: No Key Vaults configured.");
            Console.Error.WriteLine("Set Rotator__KeyVaults environment variable (comma-separated)");
            Console.Error.WriteLine("Or add KeyVaults array to appsettings.json under Rotator section.");
            return 1;
        }
        var keyVaultService = new KeyVaultService(credential, keyVaultNames, loggerFactory.CreateLogger<KeyVaultService>());

        // Create commands
        var listCommand = new ListCommand(
            entraIdService,
            metrics,
            options,
            loggerFactory.CreateLogger<ListCommand>());

        var expiringCommand = new ExpiringCommand(
            entraIdService,
            metrics,
            options,
            loggerFactory.CreateLogger<ExpiringCommand>());

        var expiredCommand = new ExpiredCommand(
            entraIdService,
            metrics,
            options,
            loggerFactory.CreateLogger<ExpiredCommand>());

        var keyVaultsCommand = new KeyVaultsCommand(
            keyVaultService,
            options,
            loggerFactory.CreateLogger<KeyVaultsCommand>());

        var orphansCommand = new OrphansCommand(
            entraIdService,
            keyVaultService,
            metrics,
            options,
            loggerFactory.CreateLogger<OrphansCommand>());

        // Create the rotation plan builder
        var planBuilder = new RotationPlanBuilder();

        // Create the rotate command
        var rotateCommand = new RotateCommand(
            entraIdService,
            keyVaultService,
            planBuilder,
            metrics,
            options,
            loggerFactory.CreateLogger<RotateCommand>());

        // Create the cleanup command
        var cleanupCommand = new CleanupCommand(
            entraIdService,
            keyVaultService,
            planBuilder,
            metrics,
            options,
            loggerFactory.CreateLogger<CleanupCommand>());

        // Build root command
        var rootCommand = new RootCommand("EntraIDSecretRotator - Manage Entra ID app registration secrets")
        {
            listCommand.BuildCommand(),
            expiringCommand.BuildCommand(),
            expiredCommand.BuildCommand(),
            keyVaultsCommand.BuildCommand(),
            orphansCommand.BuildCommand(),
            rotateCommand.BuildCommand(),
            cleanupCommand.BuildCommand()
        };

        // Execute
        try
        {
            return await rootCommand.Parse(args).InvokeAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
