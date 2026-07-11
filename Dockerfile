# Multi-stage build for the Wisper API — deploys as a container image behind an
# API gateway / load balancer. Pinned to .NET 8 (see global.json).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first (cached unless project/props change).
COPY global.json Directory.Build.props ./
COPY src/Wisper.Api/Wisper.Api.csproj src/Wisper.Api/
RUN dotnet restore src/Wisper.Api/Wisper.Api.csproj

# Build + publish.
COPY src/ src/
RUN dotnet publish src/Wisper.Api/Wisper.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Listen on 8080 in-container; the deploy maps this to the service's configured port.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_TieredPGO=1
EXPOSE 8080

# Run as a non-root user (the aspnet image ships one).
USER $APP_UID

ENTRYPOINT ["dotnet", "Wisper.Api.dll"]
