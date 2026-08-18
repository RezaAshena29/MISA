using Azure.Monitor.OpenTelemetry.Exporter;
using MISA.Agents;
using MISA.Application;
using MISA.Decisioning;
using MISA.Infrastructure;
using MISA.Knowledge;
using MISA.Reasoning;
using MISA.Clarification;
using MISA.Observability;
using MISA.Orchestration.Akka;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

builder.Services
    .AddMisaObservability(builder.Configuration)
    .AddMisaApplication()
    .AddMisaInfrastructure(builder.Configuration)
    .AddMisaDecisioning()
    .AddMisaKnowledge()
    .AddMisaReasoning()
    .AddMisaClarification()
    .AddMisaAgents()
    .AddMisaOrchestrationAkka();

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

builder.Build().Run();
