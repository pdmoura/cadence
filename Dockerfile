# Multi-stage build: SDK image compiles, the slim ASP.NET runtime image runs.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Cadence.sln ./
COPY src/Cadence.Web/Cadence.Web.csproj src/Cadence.Web/
RUN dotnet restore src/Cadence.Web/Cadence.Web.csproj
COPY src/ src/
COPY db/ db/
RUN dotnet publish src/Cadence.Web/Cadence.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Database__Backend=sqlite \
    Database__Path=/data/cadence.db
COPY --from=build /app/publish ./
COPY --from=build /src/db/migrations ./db/migrations
RUN mkdir -p /data && chown -R app:app /data /app
USER app
VOLUME ["/data"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s CMD ["/bin/sh", "-c", "wget -qO- http://127.0.0.1:8080/healthz || exit 1"]
ENTRYPOINT ["dotnet", "Cadence.Web.dll"]
