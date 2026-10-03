# CvMaker.Agentic — Azure AI Foundry agents and the flow engine.
#
# Called only by the API and not exposed publicly. It needs Azure credentials to reach the
# model backend.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/CvMaker.Shared/CvMaker.Shared.csproj src/CvMaker.Shared/
COPY src/CvMaker.Agentic/CvMaker.Agentic.csproj src/CvMaker.Agentic/
RUN dotnet restore src/CvMaker.Agentic/CvMaker.Agentic.csproj

COPY src/CvMaker.Shared/ src/CvMaker.Shared/
COPY src/CvMaker.Agentic/ src/CvMaker.Agentic/
RUN dotnet publish src/CvMaker.Agentic/CvMaker.Agentic.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
LABEL org.opencontainers.image.source=https://github.com/konradcinkusz/portfolio-maker

RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin agentic
USER agentic

ENV ASPNETCORE_URLS=http://+:8080 \
    Agents__Root=/app/agents \
    Flows__Root=/app/flows

EXPOSE 8080
ENTRYPOINT ["dotnet", "CvMaker.Agentic.dll"]
