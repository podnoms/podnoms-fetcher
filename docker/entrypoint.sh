#!/bin/sh
set -e

# YouTube changes often, and an old yt-dlp is refused sooner. A failed update
# isn't fatal; the version in the image is used.
if [ "$YTDLP_UPDATE_ON_START" = "true" ]; then
  /opt/yt-dlp/bin/pip install --no-cache-dir --quiet --upgrade 'yt-dlp[default]' \
    || echo "Could not update yt-dlp; using $(yt-dlp --version)"
fi

exec "$@"
