FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and common props
COPY fhir-augury.slnx .
COPY src/common.props src/common.props
COPY src/Directory.Build.props src/Directory.Build.props

# Copy project files for restore
COPY src/FhirAugury.Models/FhirAugury.Models.csproj src/FhirAugury.Models/
COPY src/FhirAugury.Database/FhirAugury.Database.csproj src/FhirAugury.Database/
COPY src/FhirAugury.Sources.Jira/FhirAugury.Sources.Jira.csproj src/FhirAugury.Sources.Jira/
COPY src/FhirAugury.Sources.Zulip/FhirAugury.Sources.Zulip.csproj src/FhirAugury.Sources.Zulip/
COPY src/FhirAugury.Sources.Confluence/FhirAugury.Sources.Confluence.csproj src/FhirAugury.Sources.Confluence/
COPY src/FhirAugury.Sources.GitHub/FhirAugury.Sources.GitHub.csproj src/FhirAugury.Sources.GitHub/
COPY src/FhirAugury.Indexing/FhirAugury.Indexing.csproj src/FhirAugury.Indexing/
COPY src/FhirAugury.Service/FhirAugury.Service.csproj src/FhirAugury.Service/
COPY src/FhirAugury.Cli/FhirAugury.Cli.csproj src/FhirAugury.Cli/
COPY src/FhirAugury.Mcp/FhirAugury.Mcp.csproj src/FhirAugury.Mcp/

RUN dotnet restore fhir-augury.slnx

# Copy everything and build
COPY src/ src/
RUN dotnet publish src/FhirAugury.Service/FhirAugury.Service.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=build /app .

# Persistent volumes
VOLUME ["/data/cache"]
VOLUME ["/data/db"]

# Create non-root user and set ownership
RUN adduser --disabled-password --gecos "" appuser \
    && mkdir -p /data/cache /data/db \
    && chown -R appuser:appuser /app /data

# Default configuration via environment variables
ENV FHIR_AUGURY_Cache__RootPath=/data/cache
ENV FHIR_AUGURY_DatabasePath=/data/db/fhir-augury.db

EXPOSE 5100

USER appuser

ENTRYPOINT ["dotnet", "FhirAugury.Service.dll"]
