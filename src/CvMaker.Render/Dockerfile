# CvMaker.Render — ASP.NET + TeX Live from Debian packages.
#
# Sizing: this is the heavy image. altacv-modern needs the -extra, fonts-extra
# and pictures sets (see "TeX Live" below), so the installation runs to several
# gigabytes — slow to pull and expensive to keep warm on Fly. A scheme-basic
# base with a tlmgr install of only the packages the templates reference is the
# known fix and is not done. Adding a template means adding its packages here;
# the end-to-end CI job compiles every shipped template, which is what catches
# a missing one.
#
# Run this container with the sandbox the compiler assumes:
#   --read-only --cap-drop ALL --user 10001:10001 \
#   --tmpfs /tmp/cvmaker-render:rw,noexec,nosuid,size=256m
#
# On networking: "no network" would be imprecise — this is an HTTP service, so
# it must keep an interface to receive requests. What has to be denied is
# *egress*, so a payload that survives escaping still cannot reach out.
# Enforce that where the deployment can: an internal-only Docker network with no
# default route locally, and on Fly an app with no public IP that is only
# reachable over the private 6PN network. Denying the TeX process itself a
# network namespace (unshare -n around the engine) is the stronger version and is
# not done; it needs CAP_SYS_ADMIN or userns-remap, which is why it is not the
# default here.
#
# The in-process controls in LatexCompiler.cs are the first layer, not the only
# one.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/CvMaker.Render/CvMaker.Render.csproj src/CvMaker.Render/
RUN dotnet restore src/CvMaker.Render/CvMaker.Render.csproj

COPY src/CvMaker.Render/ src/CvMaker.Render/
RUN dotnet publish src/CvMaker.Render/CvMaker.Render.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
LABEL org.opencontainers.image.source=https://github.com/konradcinkusz/portfolio-maker

# ── TeX Live ─────────────────────────────────────────────────────────────────
# latexmk drives the build; the -recommended sets cover the base packages the
# ats-plain template uses (article, geometry, enumitem, titlesec, parskip,
# hyperref, lmodern). altacv.cls additionally requires academicons, simpleicons,
# fontawesome5, tikz, tcolorbox, trimclip, dashrule, multirow, tabularx, pdfx
# and extsizes, which is what pulls in latex-extra, fonts-extra and pictures.
#
# Accepted for now so the two-column template works; the follow-up is a tlmgr
# based image installing only the packages actually referenced, which is what
# would keep Fly cold starts tolerable.
#
# altacv-modern's cv.tex.scriban also does `\setmainfont{Roboto Slab}` /
# `\setsansfont{Lato}` under `\iftutex` — fontspec resolves those through the
# *system* font catalogue (fontconfig), not a TeX Live package, so the fonts
# have to be installed as fonts and `fc-cache` run, or LuaLaTeX fails with
# "Package fontspec Error: The font ... cannot be found."
#
# `fontconfig` is in the list for `fc-cache` itself. The aspnet base image does
# not ship it, and neither font package pulls it in under
# --no-install-recommends, so the layer failed with exit 127 and took every
# downstream CI job with it.
RUN apt-get update && apt-get install -y --no-install-recommends \
        texlive-latex-base \
        texlive-latex-recommended \
        texlive-latex-extra \
        texlive-fonts-recommended \
        texlive-fonts-extra \
        texlive-pictures \
        texlive-luatex \
        texlive-xetex \
        latexmk \
        fonts-lato \
        fonts-roboto-slab \
        fontconfig \
        curl \
    && fc-cache -f \
    && rm -rf /var/lib/apt/lists/*

# Non-root. The compiler writes only under its work root, which should be a
# tmpfs mounted at this path.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin renderer \
    && mkdir -p /tmp/cvmaker-render \
    && chown renderer:renderer /tmp/cvmaker-render

WORKDIR /app
COPY --from=build /app/publish .

USER renderer

ENV ASPNETCORE_URLS=http://+:8080 \
    Render__WorkRoot=/tmp/cvmaker-render \
    Render__TimeoutSeconds=30

EXPOSE 8080
ENTRYPOINT ["dotnet", "CvMaker.Render.dll"]
