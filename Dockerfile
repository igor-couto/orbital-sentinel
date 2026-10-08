FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props global.json ./
COPY src/OrbitalSentinel.Core/ src/OrbitalSentinel.Core/
COPY src/OrbitalSentinel.Propagation/ src/OrbitalSentinel.Propagation/
COPY src/OrbitalSentinel.Infrastructure/ src/OrbitalSentinel.Infrastructure/
COPY src/OrbitalSentinel.Runtime/ src/OrbitalSentinel.Runtime/
COPY src/OrbitalSentinel.Api/ src/OrbitalSentinel.Api/
COPY src/OrbitalSentinel.Workers/ src/OrbitalSentinel.Workers/
RUN dotnet restore src/OrbitalSentinel.Api --locked-mode \
    && dotnet restore src/OrbitalSentinel.Workers --locked-mode \
    && dotnet publish src/OrbitalSentinel.Api -c Release --no-restore -o /out/api \
    && dotnet publish src/OrbitalSentinel.Workers -c Release --no-restore -o /out/workers

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID

FROM runtime AS api
COPY --from=build /out/api .
ENTRYPOINT ["dotnet", "OrbitalSentinel.Api.dll"]

FROM runtime AS workers
COPY --from=build /out/workers .
ENTRYPOINT ["dotnet", "OrbitalSentinel.Workers.dll"]
