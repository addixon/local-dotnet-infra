# ── Build stage ────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app --no-restore || dotnet publish -c Release -o /app

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Proxy listens on AMQP (5672), HTTP management (5300), and HTTPS admin (443)
EXPOSE 5672 5300 443

ENTRYPOINT ["dotnet", "ServiceBusProxy.dll"]
