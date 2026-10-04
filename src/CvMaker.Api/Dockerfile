# CvMaker.Api — CRUD, job queue, worker.
#
# Small image on purpose (see docs/ARCHITECTURE.md, "Services and why they are
# separate"): the API scales on request volume, the renderer scales on CPU, and
# only the renderer needs TeX.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/CvMaker.Core/CvMaker.Core.csproj src/CvMaker.Core/
COPY src/CvMaker.Shared/CvMaker.Shared.csproj src/CvMaker.Shared/
COPY src/CvMaker.Api/CvMaker.Api.csproj src/CvMaker.Api/
RUN dotnet restore src/CvMaker.Api/CvMaker.Api.csproj

COPY src/CvMaker.Core/ src/CvMaker.Core/
COPY src/CvMaker.Shared/ src/CvMaker.Shared/
COPY src/CvMaker.Api/ src/CvMaker.Api/
RUN dotnet publish src/CvMaker.Api/CvMaker.Api.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
LABEL org.opencontainers.image.source=https://github.com/konradcinkusz/portfolio-maker

# The aspnet base image ships neither curl nor wget, so a container healthcheck
# has nothing to call without this.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# The catalogue is data, not code: templates are read once and shipped to the
# renderer per request, so the API never compiles anything itself.
COPY templates/ /app/templates/

RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin api
USER api

ENV ASPNETCORE_URLS=http://+:8080 \
    Templates__Root=/app/templates

EXPOSE 8080
ENTRYPOINT ["dotnet", "CvMaker.Api.dll"]
