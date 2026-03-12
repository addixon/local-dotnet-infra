# ── Build stage ────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app --no-restore || dotnet publish -c Release -o /app

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app .

# Proxy listens on AMQP (5672) and HTTP management (5300)
EXPOSE 5672 5300

ENTRYPOINT ["dotnet", "ServiceBusProxy.dll"]
