# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY fhir-augury.slnx .
COPY src/common.props src/common.props
COPY src/Directory.Build.props src/Directory.Build.props

# Copy project files for restore
COPY src/FhirAugury.Common/FhirAugury.Common.csproj src/FhirAugury.Common/
COPY src/FhirAugury.Source.Confluence/FhirAugury.Source.Confluence.csproj src/FhirAugury.Source.Confluence/
COPY protos/ protos/

RUN dotnet restore src/FhirAugury.Source.Confluence/FhirAugury.Source.Confluence.csproj

# Copy source and build
COPY src/FhirAugury.Common/ src/FhirAugury.Common/
COPY src/FhirAugury.Source.Confluence/ src/FhirAugury.Source.Confluence/

RUN dotnet publish src/FhirAugury.Source.Confluence/FhirAugury.Source.Confluence.csproj \
    -c Release -o /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

RUN adduser --disabled-password --no-create-home appuser \
    && mkdir -p /app/cache /app/data \
    && chown -R appuser:appuser /app

EXPOSE 5180 5181

HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
    CMD wget --spider -q http://localhost:5180/health || exit 1

USER appuser
ENTRYPOINT ["dotnet", "FhirAugury.Source.Confluence.dll"]
