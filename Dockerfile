# syntax=docker/dockerfile:1

# ---- 1) Angular-SPA bauen ------------------------------------------------------------------------
FROM node:24 AS frontend
WORKDIR /src
COPY frontend/dataspace-drive/package*.json ./
RUN npm ci
COPY frontend/dataspace-drive/ ./
# DevExtreme-Lizenz zur Build-Zeit einsetzen (nie im Repo); leer = Testmodus.
ARG DEVEXTREME_LICENSE=""
RUN printf "export const DEVEXTREME_LICENSE_KEY = '%s';\n" "$DEVEXTREME_LICENSE" > src/license.ts
RUN npx ng build --configuration production

# ---- 2) .NET-Backend bauen -----------------------------------------------------------------------
# global.json wird bewusst nicht kopiert: das SDK-Image bringt seine eigene 10.0-Version mit.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY backend/Directory.Build.props backend/Directory.Packages.props ./
COPY backend/src/DataspaceDrive.Api/DataspaceDrive.Api.csproj src/DataspaceDrive.Api/
COPY backend/src/DataspaceDrive.Core/DataspaceDrive.Core.csproj src/DataspaceDrive.Core/
COPY backend/src/DataspaceDrive.Infrastructure/DataspaceDrive.Infrastructure.csproj src/DataspaceDrive.Infrastructure/
COPY backend/src/Swms.Edc.Client/Swms.Edc.Client.csproj src/Swms.Edc.Client/
RUN dotnet restore src/DataspaceDrive.Api/DataspaceDrive.Api.csproj
COPY backend/src/ src/
COPY --from=frontend /src/dist/dataspace-drive/browser/ src/DataspaceDrive.Api/wwwroot/
RUN dotnet publish src/DataspaceDrive.Api/DataspaceDrive.Api.csproj -c Release -o /app --no-restore

# ---- 3) Laufzeit ---------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=backend /app ./
# 8080: UI und API (Ingress), 8090: Content-Endpunkt nur für die EDC-Dataplane
EXPOSE 8080 8090
USER $APP_UID
ENTRYPOINT ["dotnet", "DataspaceDrive.Api.dll"]
