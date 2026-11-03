FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY web/PanelHaven.Web.csproj web/
RUN dotnet restore web/PanelHaven.Web.csproj
COPY web/*.cs web/
COPY web/wwwroot/ web/wwwroot/
COPY scripts/backup.py scripts/backup.py
RUN dotnet publish web/PanelHaven.Web.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_URLS=http://127.0.0.1:5178 \
    DataPath=/var/lib/panelhaven/data \
    ServerSettingsPath=/var/lib/panelhaven/settings.json \
    Libraries__0=/library
USER $APP_UID
ENTRYPOINT ["dotnet", "PanelHaven.Web.dll"]
