# CvMaker.Web — Blazor WebAssembly served by nginx, matching BlackHoleSim.Web.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/CvMaker.Shared/CvMaker.Shared.csproj src/CvMaker.Shared/
COPY src/CvMaker.Web/CvMaker.Web.csproj src/CvMaker.Web/
RUN dotnet restore src/CvMaker.Web/CvMaker.Web.csproj

COPY src/CvMaker.Shared/ src/CvMaker.Shared/
COPY src/CvMaker.Web/ src/CvMaker.Web/
RUN dotnet publish src/CvMaker.Web/CvMaker.Web.csproj -c Release -o /app/publish --no-restore


FROM nginx:1.27-alpine AS final
LABEL org.opencontainers.image.source=https://github.com/konradcinkusz/portfolio-maker

COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
COPY src/CvMaker.Web/nginx.conf /etc/nginx/conf.d/default.conf
COPY src/CvMaker.Web/docker-entrypoint.sh /docker-entrypoint-cvmaker.sh
RUN chmod +x /docker-entrypoint-cvmaker.sh

EXPOSE 8080
ENTRYPOINT ["/docker-entrypoint-cvmaker.sh"]
