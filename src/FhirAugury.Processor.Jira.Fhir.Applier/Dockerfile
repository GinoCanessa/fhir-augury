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
COPY src/FhirAugury.Processor.Jira.Fhir.Applier/FhirAugury.Processor.Jira.Fhir.Applier.csproj src/FhirAugury.Processor.Jira.Fhir.Applier/
COPY protos/ protos/

RUN dotnet restore src/FhirAugury.Processor.Jira.Fhir.Applier/FhirAugury.Processor.Jira.Fhir.Applier.csproj

# Copy source and publish
COPY src/FhirAugury.Common/ src/FhirAugury.Common/
COPY src/FhirAugury.Common.OpenApi/ src/FhirAugury.Common.OpenApi/
COPY src/FhirAugury.ServiceDefaults/ src/FhirAugury.ServiceDefaults/
COPY src/FhirAugury.Processing.Common/ src/FhirAugury.Processing.Common/
COPY src/FhirAugury.Processing.Jira.Common/ src/FhirAugury.Processing.Jira.Common/
COPY src/FhirAugury.Processor.Jira.Fhir.Applier/ src/FhirAugury.Processor.Jira.Fhir.Applier/

RUN dotnet publish src/FhirAugury.Processor.Jira.Fhir.Applier/FhirAugury.Processor.Jira.Fhir.Applier.csproj \
    -c Release -o /app --no-restore

# Runtime stage. Note: this image hosts the HTTP API and queue service. Real apply
# attempts also require an operator-supplied Copilot-compatible agent binary plus
# `git` on PATH; bundling those into this image is future work.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/* \
    && adduser --disabled-password --no-create-home appuser \
    && mkdir -p /app/data /app/out \
    && chown -R appuser:appuser /app

EXPOSE 5173

HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
    CMD wget --spider -q http://localhost:5173/health || exit 1

USER appuser
ENTRYPOINT ["dotnet", "FhirAugury.Processor.Jira.Fhir.Applier.dll"]
