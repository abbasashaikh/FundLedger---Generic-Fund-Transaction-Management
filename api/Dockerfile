# FundLedger API + Worker image (one image, two entrypoints).
#   API:     docker run <image>                      (default)
#   Worker:  docker run <image> worker
#   Migrate: docker run -e ConnectionStrings__Migrations=... <image> migrate
# Build context: ./api
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first for layer caching.
COPY global.json Directory.Build.props Directory.Packages.props FundLedger.slnx ./
COPY src/FundLedger.Domain/FundLedger.Domain.csproj src/FundLedger.Domain/
COPY src/FundLedger.Application/FundLedger.Application.csproj src/FundLedger.Application/
COPY src/FundLedger.Infrastructure/FundLedger.Infrastructure.csproj src/FundLedger.Infrastructure/
COPY src/FundLedger.Api/FundLedger.Api.csproj src/FundLedger.Api/
COPY src/FundLedger.Worker/FundLedger.Worker.csproj src/FundLedger.Worker/
RUN dotnet restore src/FundLedger.Api/FundLedger.Api.csproj \
 && dotnet restore src/FundLedger.Worker/FundLedger.Worker.csproj

COPY src/ src/
# OpenAPI generation is a dev/CI concern; skip it inside the image build.
RUN dotnet publish src/FundLedger.Api/FundLedger.Api.csproj -c Release -o /out/api --no-restore /p:OpenApiGenerateDocuments=false \
 && dotnet publish src/FundLedger.Worker/FundLedger.Worker.csproj -c Release -o /out/worker --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG APP_VERSION=0.0.0
ARG GIT_SHA=unknown
ENV APP_VERSION=${APP_VERSION} \
    GIT_SHA=${GIT_SHA} \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=0
LABEL org.opencontainers.image.source="https://github.com/abbasashaikh/FundLedger---Generic-Fund-Transaction-Management" \
      org.opencontainers.image.revision="${GIT_SHA}" \
      org.opencontainers.image.version="${APP_VERSION}"

WORKDIR /app
COPY --from=build /out/api ./api
COPY --from=build /out/worker ./worker
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod 0755 /usr/local/bin/docker-entrypoint.sh

# Non-root user provided by the .NET base image.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
