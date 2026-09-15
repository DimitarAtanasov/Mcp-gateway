# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Restore against the manifests alone so the layer caches across source edits.
COPY Directory.Build.props Directory.Packages.props McpGateway.sln ./
COPY src/McpGateway/McpGateway.csproj src/McpGateway/
COPY tests/McpGateway.Tests/McpGateway.Tests.csproj tests/McpGateway.Tests/
RUN dotnet restore

COPY . .
RUN dotnet publish src/McpGateway/McpGateway.csproj \
    --configuration Release \
    --no-restore \
    --output /app

# Run
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app ./

# The gateway is a network service fronted by APIM or Container Apps auth.
ENV GATEWAY_TRANSPORT=streamable-http \
    GATEWAY_PORT=8000 \
    GATEWAY_REGISTRY_PATH=/app/registry.yaml \
    DOTNET_gcServer=1

EXPOSE 8000

# $APP_UID is the non-root user the .NET base images ship with.
USER $APP_UID

ENTRYPOINT ["dotnet", "McpGateway.dll"]
