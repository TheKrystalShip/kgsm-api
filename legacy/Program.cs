using Microsoft.AspNetCore.SignalR;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Api.Configuration;
using TheKrystalShip.KGSM.Api.Services;
using TheKrystalShip.KGSM.Api.Hubs;
using TheKrystalShip.KGSM.Api.Constants;

namespace TheKrystalShip.KGSM.Api;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add configuration
        builder.Services.Configure<KgsmApiOptions>(
            builder.Configuration.GetSection(ApiConstants.ConfigurationSections.KgsmApi));

        // Add CORS policy for development and local network access
        builder.Services.AddCors(options =>
        {
            options.AddPolicy(ApiConstants.CorsPolicy.KgsmApiPolicy, policy =>
            {
                policy.WithOrigins(
                        ApiConstants.Defaults.LocalhostOrigin,
                        ApiConstants.Defaults.LocalhostIpOrigin
                    )
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
                    .SetIsOriginAllowedToAllowWildcardSubdomains();
            });
        });

        // Add controllers
        builder.Services.AddControllers();

        // Add OpenAPI/Swagger
        builder.Services.AddOpenApi();

        // Add SignalR for WebSocket functionality
        builder.Services.AddSignalR();

        // Add KGSM services
        builder.Services.AddKgsmServices(options =>
        {
            options.KgsmPath = builder.Configuration.GetValue<string>(ApiConstants.ConfigurationSections.KgsmPath) ?? ApiConstants.Defaults.KgsmPath;
            options.SocketPath = builder.Configuration.GetValue<string>(ApiConstants.ConfigurationSections.SocketPath) ?? ApiConstants.Defaults.SocketPath;
        });

        // Add application services
        builder.Services.AddSingleton<ISystemMetricsService, SystemMetricsService>();
        builder.Services.AddScoped<ILogStreamingService, LogStreamingService>();

        // Add background services
        builder.Services.AddHostedService<SystemMetricsBackgroundService>();
        builder.Services.AddHostedService<SystemMetricsBroadcastService>();

        // Add health checks
        builder.Services.AddHealthChecks();

        var app = builder.Build();

        // Configure the HTTP request pipeline
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint(ApiConstants.OpenApi.EndpointPath, ApiConstants.OpenApi.Title);
            });
        }

        // Enable CORS
        app.UseCors(ApiConstants.CorsPolicy.KgsmApiPolicy);

        // Enable static files
        app.UseStaticFiles();

        // Use routing
        app.UseRouting();

        // Use authorization (if needed in future)
        app.UseAuthorization();

        // Map controllers
        app.MapControllers();

        // Map SignalR hubs
        app.MapHub<LogStreamingHub>(SignalRConstants.Hubs.LogStreaming);
        app.MapHub<SystemMetricsHub>(SignalRConstants.Hubs.SystemMetrics);

        // Map health checks
        app.MapHealthChecks(ApiConstants.Routes.Health);

        app.Run();
    }
}
