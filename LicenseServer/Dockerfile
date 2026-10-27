FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY LicenseServer/Pulse.LicensingServer.csproj LicenseServer/
COPY LicensingCore/OfflineLicenseCodec.cs LicensingCore/
RUN dotnet restore LicenseServer/Pulse.LicensingServer.csproj

COPY LicenseServer/ LicenseServer/
COPY LicensingCore/OfflineLicenseCodec.cs LicensingCore/
RUN dotnet publish LicenseServer/Pulse.LicensingServer.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
COPY LicensingCore/pulse-license-public-key.pem /app/pulse-license-public-key.pem

ENV ASPNETCORE_URLS=http://+:10000
ENV PULSE_LICENSE_DATA_DIR=/var/data
ENV PULSE_LICENSE_PUBLIC_KEY_PATH=/app/pulse-license-public-key.pem
EXPOSE 10000
ENTRYPOINT ["dotnet", "Pulse.LicensingServer.dll"]
