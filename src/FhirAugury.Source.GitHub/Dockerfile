# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY fhir-augury.slnx .
COPY src/common.props src/common.props
COPY src/Directory.Build.props src/Directory.Build.props

# Copy project files for restore
COPY src/FhirAugury.Common/FhirAugury.Common.csproj src/FhirAugury.Common/
COPY src/FhirAugury.ServiceDefaults/FhirAugury.ServiceDefaults.csproj src/FhirAugury.ServiceDefaults/
COPY src/FhirAugury.Source.GitHub/FhirAugury.Source.GitHub.csproj src/FhirAugury.Source.GitHub/

RUN dotnet restore src/FhirAugury.Source.GitHub/FhirAugury.Source.GitHub.csproj

# Copy source and build
COPY src/FhirAugury.Common/ src/FhirAugury.Common/
COPY src/FhirAugury.ServiceDefaults/ src/FhirAugury.ServiceDefaults/
COPY src/FhirAugury.Source.GitHub/ src/FhirAugury.Source.GitHub/

RUN dotnet publish src/FhirAugury.Source.GitHub/FhirAugury.Source.GitHub.csproj \
    -c Release -o /app --no-restore

# Runtime stage — needs git for repository cloning
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

RUN adduser --disabled-password --no-create-home appuser \
    && mkdir -p /app/cache /app/data \
    && chown -R appuser:appuser /app

EXPOSE 5190

HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
    CMD wget --spider -q http://localhost:5190/health || exit 1

USER appuser
ENTRYPOINT ["dotnet", "FhirAugury.Source.GitHub.dll"]
