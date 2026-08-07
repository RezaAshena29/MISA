using Azure.Monitor.OpenTelemetry.Exporter;
using MISA.Agents;
using MISA.Application;
using MISA.Decisioning;
using MISA.Infrastructure;
using MISA.Knowledge;
using MISA.Observability;
using MISA.Orchestration.Akka;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddMisaObservability(builder.Configuration)
    .AddMisaApplication()
    .AddMisaInfrastructure(builder.Configuration)
    .AddMisaDecisioning()
    .AddMisaKnowledge()
    .AddMisaAgents()
    .AddMisaOrchestrationAkka();

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

builder.Build().Run();
