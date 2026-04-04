# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY fhir-augury.slnx .
COPY src/common.props src/common.props
COPY src/Directory.Build.props src/Directory.Build.props

# Copy project files for restore
COPY src/FhirAugury.Common/FhirAugury.Common.csproj src/FhirAugury.Common/
COPY src/FhirAugury.Orchestrator/FhirAugury.Orchestrator.csproj src/FhirAugury.Orchestrator/

RUN dotnet restore src/FhirAugury.Orchestrator/FhirAugury.Orchestrator.csproj

# Copy source and build
COPY src/FhirAugury.Common/ src/FhirAugury.Common/
COPY src/FhirAugury.Orchestrator/ src/FhirAugury.Orchestrator/

RUN dotnet publish src/FhirAugury.Orchestrator/FhirAugury.Orchestrator.csproj \
    -c Release -o /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

RUN adduser --disabled-password --no-create-home appuser \
    && mkdir -p /app/data \
    && chown -R appuser:appuser /app

EXPOSE 5150

HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
    CMD wget --spider -q http://localhost:5150/health || exit 1

USER appuser
ENTRYPOINT ["dotnet", "FhirAugury.Orchestrator.dll"]
