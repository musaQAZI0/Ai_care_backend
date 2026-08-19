FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY AiCare.Backend.sln ./
COPY src/AiCare.Api/AiCare.Api.csproj src/AiCare.Api/
COPY src/AiCare.Application/AiCare.Application.csproj src/AiCare.Application/
COPY src/AiCare.Domain/AiCare.Domain.csproj src/AiCare.Domain/
COPY src/AiCare.Infrastructure/AiCare.Infrastructure.csproj src/AiCare.Infrastructure/
COPY tests/AiCare.Tests/AiCare.Tests.csproj tests/AiCare.Tests/
RUN dotnet restore AiCare.Backend.sln

COPY . .
RUN dotnet publish src/AiCare.Api/AiCare.Api.csproj -c Release -o /app/publish --no-restore

FROM build AS migrations
RUN dotnet tool install --global dotnet-ef --version 8.0.0
ENV PATH="${PATH}:/root/.dotnet/tools"
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "ef", "database", "update", "--project", "src/AiCare.Infrastructure/AiCare.Infrastructure.csproj", "--startup-project", "src/AiCare.Api/AiCare.Api.csproj", "--no-build"]

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
ENV DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=5 CMD curl --fail --silent http://127.0.0.1:${PORT:-8080}/health/live >/dev/null || exit 1
CMD ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet AiCare.Api.dll"]
