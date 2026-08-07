using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MISA.Observability;

/// <summary>
/// Registers baseline observability for all MISA modules.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
	/// <summary>
	/// Adds OpenTelemetry tracing with OTLP exporter.
	/// </summary>
	public static IServiceCollection AddMisaObservability(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		var serviceName = configuration["Observability:ServiceName"] ?? "MISA.Functions";

		services
			.AddOpenTelemetry()
			.ConfigureResource(resource => resource.AddService(serviceName))
			.WithMetrics(metrics =>
			{
				metrics
					.AddMeter("MISA.Application")
					.AddMeter("MISA.Orchestration.Akka")
					.AddMeter("MISA.Decisioning")
					.AddMeter("MISA.Knowledge")
					.AddOtlpExporter();
			})
			.WithTracing(tracing =>
			{
				tracing
					.AddSource("MISA.Application")
					.AddSource("MISA.Orchestration.Akka")
					.AddSource("MISA.Decisioning")
					.AddSource("MISA.Knowledge")
					.AddHttpClientInstrumentation()
					.AddAspNetCoreInstrumentation()
					.AddOtlpExporter();
			});

		return services;
	}
}
