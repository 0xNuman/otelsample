# Multi-stage Dockerfile for OtelSample.Api
# Builds the entire solution from the repo root, then runs only the API project.
# Context must be the repository root (docker-compose.yml sets build.context: .)

# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and all project files first for layer caching on restore
COPY OtelSample.slnx ./
COPY src/OtelSample.Api/OtelSample.Api.csproj                       src/OtelSample.Api/
COPY src/OtelSample.Application/OtelSample.Application.csproj       src/OtelSample.Application/
COPY src/OtelSample.Infrastructure/OtelSample.Infrastructure.csproj src/OtelSample.Infrastructure/
COPY src/OtelSample.Observability/OtelSample.Observability.csproj   src/OtelSample.Observability/

RUN dotnet restore OtelSample.slnx

# Copy everything else and publish
COPY . .
RUN dotnet publish src/OtelSample.Api/OtelSample.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# ASP.NET Core 8+ defaults to port 8080 in containers
EXPOSE 8080

ENTRYPOINT ["dotnet", "OtelSample.Api.dll"]
