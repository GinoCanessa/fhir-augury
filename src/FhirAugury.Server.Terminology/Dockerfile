# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY fhir-augury.slnx .
COPY src/common.props src/common.props

# Copy project files for restore
COPY src/FhirAugury.Common/FhirAugury.Common.csproj src/FhirAugury.Common/
COPY src/FhirAugury.Common.OpenApi/FhirAugury.Common.OpenApi.csproj src/FhirAugury.Common.OpenApi/
COPY src/FhirAugury.ServiceDefaults/FhirAugury.ServiceDefaults.csproj src/FhirAugury.ServiceDefaults/
COPY src/FhirAugury.Server.Terminology/FhirAugury.Server.Terminology.csproj src/FhirAugury.Server.Terminology/

RUN dotnet restore src/FhirAugury.Server.Terminology/FhirAugury.Server.Terminology.csproj

# Copy source and build
COPY src/FhirAugury.Common/ src/FhirAugury.Common/
COPY src/FhirAugury.Common.OpenApi/ src/FhirAugury.Common.OpenApi/
COPY src/FhirAugury.ServiceDefaults/ src/FhirAugury.ServiceDefaults/
COPY src/FhirAugury.Server.Terminology/ src/FhirAugury.Server.Terminology/

RUN dotnet publish src/FhirAugury.Server.Terminology/FhirAugury.Server.Terminology.csproj \
    -c Release -o /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

RUN adduser --disabled-password --no-create-home appuser \
    && mkdir -p /app/cache /app/data \
    && chown -R appuser:appuser /app

EXPOSE 5300

HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
    CMD wget --spider -q http://localhost:5300/health || exit 1

USER appuser
ENTRYPOINT ["dotnet", "FhirAugury.Server.Terminology.dll"]
