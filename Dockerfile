# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy source and restore dependencies.
COPY . .
RUN dotnet restore src/MISA.McpInvokeHost/MISA.McpInvokeHost.csproj

# Publish the host for a lean runtime image.
RUN dotnet publish src/MISA.McpInvokeHost/MISA.McpInvokeHost.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV MCP_LISTEN_URL=http://0.0.0.0:19082
EXPOSE 19082

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MISA.McpInvokeHost.dll"]
