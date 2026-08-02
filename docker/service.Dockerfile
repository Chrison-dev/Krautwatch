# One Dockerfile for every Krautwatch service, parameterised by project.
#
# Why not .NET SDK container publishing, which needs no Dockerfile at all? Because every service needs
# libgssapi_krb5.so.2 — Npgsql loads it when opening a connection, and no stock .NET image ships it:
#
#     Error: libgssapi_krb5.so.2: cannot open shared object file: No such file or directory
#
# which surfaces as a bare "Connection refused" and reads like a networking or start-ordering problem.
# SDK publishing cannot install packages, and pointing it at a locally built base does not work either
# — it resolves ContainerBaseImage from a registry, so a local-only base fails with a manifest error.
#
# One parameterised file keeps the runtime dependencies declared in a single place, and every service
# provably identical apart from the payload.

ARG DOTNET_VERSION=10.0

# ── build ────────────────────────────────────────────────────────────────────
#
# Pinned to BUILDPLATFORM — the *builder's* architecture, not the target's. Nothing here sets a
# RuntimeIdentifier and every service publishes framework-dependent (UseAppHost=false), so the output
# is portable IL: byte-for-byte identical whether the image will run on amd64 or arm64. Emulating the
# SDK to produce it would cost minutes per image and change nothing.
#
# Only the runtime stage below varies per architecture, which is where it actually matters.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG PROJECT
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# The whole repo, because central package management (Directory.Packages.props) and
# Directory.Build.props apply repo-wide — a project-only copy does not restore.
COPY . .
RUN dotnet restore "${PROJECT}"
RUN dotnet publish "${PROJECT}" -c "${BUILD_CONFIGURATION}" -o /app/publish \
    --no-restore /p:UseAppHost=false

# ── runtime ──────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
ARG ASSEMBLY
ARG INSTALL_FFMPEG=false

# GHCR reads this label to link the package to its repository. Without it an org-owned package lands
# unattached — no source link on the package page, and repo-scoped permissions do not apply to it.
LABEL org.opencontainers.image.source="https://github.com/Chrison-dev/Krautwatch"

# libgssapi-krb5-2: required by Npgsql, see above — every service talks to Postgres.
# ffmpeg: only the Downloader remuxes HLS with `-c copy`; installing it everywhere would add ~380 MB
# per image for nothing.
RUN apt-get update \
 && apt-get install --no-install-recommends -y libgssapi-krb5-2 \
 && if [ "${INSTALL_FFMPEG}" = "true" ]; then apt-get install --no-install-recommends -y ffmpeg; fi \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Unprivileged: the Downloader writes to a bind mount owned by the host, and a compromised service
# should not be able to rewrite the media library as root.
USER $APP_UID

# ENTRYPOINT cannot expand a build arg, so bake the assembly name into an env var the shell form reads.
ENV KRAUTWATCH_ASSEMBLY=${ASSEMBLY}
ENTRYPOINT ["sh", "-c", "exec dotnet \"${KRAUTWATCH_ASSEMBLY}\""]
