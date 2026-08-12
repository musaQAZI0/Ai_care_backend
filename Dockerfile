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

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
CMD ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet AiCare.Api.dll"]
