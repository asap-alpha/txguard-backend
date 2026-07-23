# syntax=docker/dockerfile:1
# Multi-stage build for the TxGuard API (which also hosts the in-process Temporal worker).
# Build context MUST be the `backend/` directory so all four projects + nuget.config are visible.

# ── Build ───────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, on just the project/solution metadata, so Docker layer caching skips
# a full restore whenever only source files change.
COPY nuget.config ./
COPY TxGuard.sln ./
COPY src/TxGuard.Domain/TxGuard.Domain.csproj           src/TxGuard.Domain/
COPY src/TxGuard.Workflows/TxGuard.Workflows.csproj     src/TxGuard.Workflows/
COPY src/TxGuard.Infrastructure/TxGuard.Infrastructure.csproj src/TxGuard.Infrastructure/
COPY src/TxGuard.Api/TxGuard.Api.csproj                 src/TxGuard.Api/
RUN dotnet restore src/TxGuard.Api/TxGuard.Api.csproj

# Now copy the rest and publish.
COPY src/ src/
RUN dotnet publish src/TxGuard.Api/TxGuard.Api.csproj -c Release -o /app/publish --no-restore

# ── Runtime ─────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

# Bind to the platform-assigned port; Program.cs reads PORT and overrides this locally.
# Render/Railway inject PORT at runtime — the app honors it regardless of this default.
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "TxGuard.Api.dll"]
