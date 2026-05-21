FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# ── Restore layer (cached until .csproj changes) ──────────────────────────────
COPY ["backend/Excel.Provider.csproj", "backend/"]
COPY ["proto/", "proto/"]
RUN dotnet restore "backend/Excel.Provider.csproj"

# ── Build ─────────────────────────────────────────────────────────────────────
COPY backend/ backend/
RUN dotnet publish "backend/Excel.Provider.csproj" \
    -c Release -o /app/publish --no-restore

# ── Runtime image ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS="http://+:5600"

EXPOSE 5600

ENTRYPOINT ["dotnet", "ReportingPlatform.Excel.Provider.dll"]
