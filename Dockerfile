FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY racetrace-api/racetrace-api.csproj racetrace-api/
RUN dotnet restore racetrace-api/racetrace-api.csproj
COPY racetrace-api/ racetrace-api/
RUN dotnet publish racetrace-api/racetrace-api.csproj -c Release --no-restore -o /app/publish

# Match Microsoft.Playwright in the project: includes Chromium and its Linux dependencies.
FROM mcr.microsoft.com/playwright/dotnet:v1.63.0-noble
WORKDIR /app
COPY --from=build /app/publish/ ./
COPY deploy/start.sh /usr/local/bin/racetrace-start
RUN sed -i 's/\r$//' /usr/local/bin/racetrace-start && chmod +x /usr/local/bin/racetrace-start
ENV ASPNETCORE_ENVIRONMENT=Production \
    Fantasy__DataDirectory=/data/fantasy
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/racetrace-start"]
