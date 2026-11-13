# syntax=docker/dockerfile:1

# The fetcher, compiled to a native binary, so the image carries no .NET runtime.
FROM mcr.microsoft.com/dotnet/sdk:11.0-alpine AS build
RUN apk add --no-cache build-base clang zlib-dev
WORKDIR /source
COPY src/podnoms-fetcher.csproj src/
RUN dotnet restore src/podnoms-fetcher.csproj --use-current-runtime
COPY src src
RUN dotnet publish src/podnoms-fetcher.csproj --no-restore --use-current-runtime --configuration Release --output /app

# ffmpeg and ffprobe for yt-dlp, which only has them copy streams between
# containers (its fixups) and read HLS: every format, but no encoders, filters
# or devices, and none of the codec libraries Alpine's package brings (130MB).
# PodNoms does the conversion to MP3.
FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-alpine AS ffmpeg
ARG FFMPEG_VERSION=8.1.3
ARG FFMPEG_SHA256=7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3
RUN apk add --no-cache build-base curl nasm openssl-dev pkgconf xz zlib-dev
WORKDIR /build
RUN curl -fsSL -o ffmpeg.tar.xz "https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz" \
    && echo "${FFMPEG_SHA256}  ffmpeg.tar.xz" | sha256sum -c - \
    && tar -xJf ffmpeg.tar.xz --strip-components=1 \
    && ./configure --prefix=/usr/local \
        --disable-autodetect --enable-openssl --enable-version3 --enable-zlib \
        --enable-shared --disable-static --enable-small \
        --disable-doc --disable-debug --disable-ffplay \
        --disable-encoders --disable-filters --disable-devices --disable-hwaccels \
    && make -j"$(nproc)" \
    && make install DESTDIR=/out \
    && rm -rf /out/usr/local/include /out/usr/local/share /out/usr/local/lib/pkgconfig

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-alpine
WORKDIR /app

# yt-dlp's YouTube challenge solver needs a JavaScript runtime; it runs on Bun
# (its musl build, which needs libstdc++).
COPY --from=oven/bun:1.4.1-alpine /usr/local/bin/bun /usr/local/bin/bun
COPY --from=ffmpeg /out/usr/local /usr/local

# The Python environment is owned by the app's user so it can update yt-dlp,
# on start and every few hours. The rest is for the VPN: WireGuard's wg, ip and
# iptables, used only by fetcher-vpn, which the app's user may run as root
# (and nothing else) through doas.
RUN apk add --no-cache ca-certificates doas iproute2 iptables libstdc++ python3 wireguard-tools-wg \
    && echo 'permit nopass app as root cmd /usr/local/bin/fetcher-vpn' > /etc/doas.d/fetcher.conf \
    && python3 -m venv /opt/yt-dlp \
    && /opt/yt-dlp/bin/pip install --no-cache-dir 'yt-dlp[default]' \
    && printf '%s\n' '--js-runtimes bun' > /etc/yt-dlp.conf \
    && mkdir -p /app/work \
    && chown -R app:app /opt/yt-dlp /app/work

# The fetcher listens on PORT itself, so the base image's ASPNETCORE_HTTP_PORTS is cleared.
ENV PORT=8080 \
    ASPNETCORE_HTTP_PORTS= \
    WORK_DIR=/app/work \
    YTDLP_UPDATE_ON_START=true \
    YTDLP_PIP=/opt/yt-dlp/bin/pip \
    PATH="/opt/yt-dlp/bin:${PATH}"

COPY --from=build /app/podnoms-fetcher ./
COPY --chmod=755 docker/fetcher-vpn /usr/local/bin/fetcher-vpn

USER app
EXPOSE 8080
ENTRYPOINT ["/app/podnoms-fetcher"]
