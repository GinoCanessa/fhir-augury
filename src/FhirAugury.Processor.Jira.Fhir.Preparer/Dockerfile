# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY fhir-augury.slnx .
COPY src/common.props src/common.props
COPY src/Directory.Build.props src/Directory.Build.props

# Copy project files for restore
COPY src/FhirAugury.Common/FhirAugury.Common.csproj src/FhirAugury.Common/
COPY src/FhirAugury.Common.OpenApi/FhirAugury.Common.OpenApi.csproj src/FhirAugury.Common.OpenApi/
COPY src/FhirAugury.ServiceDefaults/FhirAugury.ServiceDefaults.csproj src/FhirAugury.ServiceDefaults/
COPY src/FhirAugury.Processing.Common/FhirAugury.Processing.Common.csproj src/FhirAugury.Processing.Common/
COPY src/FhirAugury.Processing.Jira.Common/FhirAugury.Processing.Jira.Common.csproj src/FhirAugury.Processing.Jira.Common/
COPY src/FhirAugury.Processor.Jira.Fhir.Preparer.Persistence/FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.csproj src/FhirAugury.Processor.Jira.Fhir.Preparer.Persistence/
COPY src/FhirAugury.Processor.Jira.Fhir.Preparer/FhirAugury.Processor.Jira.Fhir.Preparer.csproj src/FhirAugury.Processor.Jira.Fhir.Preparer/
COPY protos/ protos/

RUN dotnet restore src/FhirAugury.Processor.Jira.Fhir.Preparer/FhirAugury.Processor.Jira.Fhir.Preparer.csproj

# Copy source and publish
COPY src/FhirAugury.Common/ src/FhirAugury.Common/
COPY src/FhirAugury.Common.OpenApi/ src/FhirAugury.Common.OpenApi/
COPY src/FhirAugury.ServiceDefaults/ src/FhirAugury.ServiceDefaults/
COPY src/FhirAugury.Processing.Common/ src/FhirAugury.Processing.Common/
COPY src/FhirAugury.Processing.Jira.Common/ src/FhirAugury.Processing.Jira.Common/
COPY src/FhirAugury.Processor.Jira.Fhir.Preparer.Persistence/ src/FhirAugury.Processor.Jira.Fhir.Preparer.Persistence/
COPY src/FhirAugury.Processor.Jira.Fhir.Preparer/ src/FhirAugury.Processor.Jira.Fhir.Preparer/

RUN dotnet publish src/FhirAugury.Processor.Jira.Fhir.Preparer/FhirAugury.Processor.Jira.Fhir.Preparer.csproj \
    -c Release -o /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# This v1 image hosts the HTTP API and queue service. Actual processing requires
# an operator-supplied Copilot-compatible agent binary on PATH; bundling an
# agent runtime into this image is future work.
RUN adduser --disabled-password --no-create-home appuser \
    && mkdir -p /app/data \
    && chown -R appuser:appuser /app

EXPOSE 5171

HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
    CMD wget --spider -q http://localhost:5171/health || exit 1

USER appuser
ENTRYPOINT ["dotnet", "FhirAugury.Processor.Jira.Fhir.Preparer.dll"]
