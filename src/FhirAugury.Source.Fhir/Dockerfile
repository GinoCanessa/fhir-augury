# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY fhir-augury.slnx .
COPY src/common.props src/common.props
COPY src/Directory.Build.props src/Directory.Build.props

# Copy project files for restore
COPY src/FhirAugury.Common/FhirAugury.Common.csproj src/FhirAugury.Common/
COPY src/FhirAugury.Source.Fhir/FhirAugury.Source.Fhir.csproj src/FhirAugury.Source.Fhir/
RUN dotnet restore src/FhirAugury.Source.Fhir/FhirAugury.Source.Fhir.csproj

# Copy source and build
COPY src/FhirAugury.Common/ src/FhirAugury.Common/
COPY src/FhirAugury.Source.Fhir/ src/FhirAugury.Source.Fhir/

RUN dotnet publish src/FhirAugury.Source.Fhir/FhirAugury.Source.Fhir.csproj \
    -c Release -o /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

RUN adduser --disabled-password --no-create-home appuser \
    && mkdir -p /app/cache /app/data \
    && chown -R appuser:appuser /app

EXPOSE 5195

HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
    CMD wget --spider -q http://localhost:5195/health || exit 1

USER appuser
ENTRYPOINT ["dotnet", "FhirAugury.Source.Fhir.dll"]
