# syntax=docker/dockerfile:1

FROM oven/bun:1.4.1
WORKDIR /app

# ffmpeg for yt-dlp's fixups. The Python environment includes yt-dlp's YouTube
# challenge solver, which runs on the Bun already in this image. It's owned
# by the app's user so it can update yt-dlp, on start and every few hours.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates ffmpeg python3 python3-venv \
    && python3 -m venv /opt/yt-dlp \
    && /opt/yt-dlp/bin/pip install --no-cache-dir 'yt-dlp[default]' \
    && printf '%s\n' '--js-runtimes bun' > /etc/yt-dlp.conf \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/work \
    && chown -R bun:bun /opt/yt-dlp /app/work

ENV NODE_ENV=production \
    PORT=8080 \
    WORK_DIR=/app/work \
    YTDLP_UPDATE_ON_START=true \
    YTDLP_PIP=/opt/yt-dlp/bin/pip \
    PATH="/opt/yt-dlp/bin:${PATH}"

COPY package.json bun.lock ./
RUN bun install --frozen-lockfile --production
COPY src ./src
COPY --chmod=755 docker/entrypoint.sh /usr/local/bin/fetcher-entrypoint

USER bun
EXPOSE 8080
ENTRYPOINT ["fetcher-entrypoint"]
CMD ["bun", "src/server.ts"]
