#!/bin/sh
# Rewrites the SPA's runtime config from environment variables before nginx
# starts.
#
# Blazor WASM configuration is fetched by the browser, not read from the
# server's environment, so the published appsettings.json is baked into the
# image at build time. Without this the deployed UI would call whatever base
# URL the build machine happened to have — localhost — and fail silently in
# every environment except the one it was built on.
set -eu

CONFIG=/usr/share/nginx/html/appsettings.json

# DEV_USER goes into JSON and then into an HTTP header, so anything that could break either
# is refused here rather than surfacing as a client that will not boot.
case "${DEV_USER:-}" in
  *[!A-Za-z0-9._@-]*)
    echo "cv-maker web: DEV_USER may contain only letters, digits and . _ @ -" >&2
    exit 1 ;;
esac

cat > "$CONFIG" <<JSON
{
  "ApiBaseUrl": "${API_BASE_URL:-http://localhost:5100}",
  "AuthBaseUrl": "${AUTH_BASE_URL:-http://localhost:5200}",
  "DevUser": "${DEV_USER:-}"
}
JSON

# Security headers, including the CSP.
#
# Written here rather than baked into nginx.conf for the same reason as the config above:
# connect-src must name the exact API and auth origins, and those are environment-specific. A
# policy fixed at build time is either wrong everywhere but one environment, or so wide it
# stops being a policy.
#
# It is an included file rather than a conf.d drop-in because nginx's add_header does not
# inherit into any context that declares its own — the cache-control locations in nginx.conf
# would silently lose every one of these.
#
# Two directives worth explaining:
#   'wasm-unsafe-eval' — Blazor compiles the .NET runtime from WebAssembly, and without this
#     the app does not boot at all. It is narrower than 'unsafe-eval': WASM compilation is
#     allowed, eval() on strings is still forbidden.
#   'unsafe-inline' in style-src — Blazor injects component styles and its error UI as inline
#     <style> elements. Removing it needs a nonce the framework does not currently emit.
cat > /etc/nginx/cvmaker-security-headers.conf <<CONF
add_header X-Content-Type-Options "nosniff" always;
add_header Referrer-Policy "strict-origin-when-cross-origin" always;
add_header X-Frame-Options "DENY" always;
add_header Content-Security-Policy "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self' ${API_BASE_URL:-http://localhost:5100} ${AUTH_BASE_URL:-http://localhost:5200}; frame-ancestors 'none'; base-uri 'self'; form-action 'self'" always;
CONF

echo "cv-maker web: API=${API_BASE_URL:-http://localhost:5100} AUTH=${AUTH_BASE_URL:-http://localhost:5200}"

if [ -n "${DEV_USER:-}" ]; then
  echo "cv-maker web: DEV_USER=${DEV_USER} — sign-in is off and every call is made as that user." \
       "Only an API in Development with no token validation accepts it (src/CvMaker.Web/Services/DevUser.cs)."
fi

exec nginx -g 'daemon off;'
