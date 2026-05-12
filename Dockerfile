# Stage 1: Build backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-backend
WORKDIR /src

COPY src/ ./
RUN dotnet restore Sonarr.sln --no-cache
RUN dotnet publish NzbDrone.Console/Sonarr.Console.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained false \
    --output /app/bin \
    /p:RunAnalyzers=false \
    /p:StyleCopEnabled=false

# Stage 2: Build frontend
FROM node:20-alpine AS build-frontend
WORKDIR /frontend

COPY frontend/ ./
RUN yarn install
RUN yarn build

# Stage 3: Runtime image
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

RUN apt-get update && apt-get install -y \
    curl \
    mediainfo \
    sqlite3 \
    && rm -rf /var/lib/apt/lists/*

ENV SONARR_BRANCH=main \
    TZ=UTC \
    PUID=1000 \
    PGID=1000

WORKDIR /app

COPY --from=build-backend /app/bin ./
COPY --from=build-frontend /frontend/build ./UI/

EXPOSE 8989

VOLUME ["/config", "/tv", "/downloads"]

ENTRYPOINT ["dotnet", "Sonarr.dll", "-nobrowser", "--data=/config"]
