# =============================================================================
# EntraIDSecretRotator - AOT Multi-arch Dockerfile
# =============================================================================
#
# LOCAL BUILD (single arch - recommended):
#   docker build -t entraid-secret-rotator .
#
# MULTI-ARCH BUILD (requires native runners or CI/CD):
#   docker buildx build --platform linux/amd64,linux/arm64 --push -t registry/entraid-secret-rotator .
#
# NOTE: AOT cross-compilation via QEMU emulation is unreliable (ILCompiler crashes).
# For multi-arch builds, use CI/CD with native runners (GitHub Actions, etc.)
# =============================================================================

# -----------------------------------------------------------------------------
# Stage 1: Build environment
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

# Install native AOT dependencies
RUN apk add --no-cache build-base clang lld musl-dev zlib-dev

# Determine RID based on current architecture
RUN ARCH=$(uname -m) && \
  echo "Building on architecture: $ARCH" && \
  if [ "$ARCH" = "x86_64" ]; then \
  echo "linux-musl-x64" > /tmp/rid; \
  elif [ "$ARCH" = "aarch64" ]; then \
  echo "linux-musl-arm64" > /tmp/rid; \
  else \
  echo "Unsupported architecture: $ARCH" && exit 1; \
  fi

WORKDIR /src

# Copy csproj first for layer caching
COPY EntraIDSecretRotator/EntraIDSecretRotator.csproj EntraIDSecretRotator/
RUN dotnet restore EntraIDSecretRotator/EntraIDSecretRotator.csproj \
  -r $(cat /tmp/rid) \
  /p:PublishAot=true

# Copy source and build
COPY EntraIDSecretRotator/ EntraIDSecretRotator/

RUN dotnet publish EntraIDSecretRotator/EntraIDSecretRotator.csproj \
  -c Release \
  -r $(cat /tmp/rid) \
  -o /app/publish \
  /p:PublishAot=true \
  /p:StripSymbols=true

# -----------------------------------------------------------------------------
# Stage 2: Assemble final filesystem
# -----------------------------------------------------------------------------
FROM alpine:3.23 AS runtime-deps
RUN apk add --no-cache ca-certificates libcrypto3 libssl3

# Build the complete filesystem tree in /rootfs
RUN mkdir -p /rootfs/lib /rootfs/usr/lib /rootfs/etc/ssl/certs /rootfs/etc \
  /rootfs/tmp/log/entraid-rotator /rootfs/tmp/system-commandline-sentinel-files && \
  cp /lib/ld-musl-* /lib/libc.musl-* /rootfs/lib/ && \
  cp /usr/lib/libssl.so.* /usr/lib/libcrypto.so.* /rootfs/usr/lib/ && \
  cp /etc/ssl/certs/ca-certificates.crt /rootfs/etc/ssl/certs/ && \
  echo "nobody:*:65534:65534:nobody:/_nonexistent:/bin/false" > /rootfs/etc/passwd && \
  chmod 1777 /rootfs/tmp

# Copy the published app into rootfs
COPY --from=build /app/publish/EntraIDSecretRotator /rootfs/app/EntraIDSecretRotator
COPY --from=build /app/publish/appsettings.json /rootfs/app/appsettings.json

# -----------------------------------------------------------------------------
# Stage 3: Final minimal image (scratch)
# -----------------------------------------------------------------------------
FROM scratch AS final

# Labels for container metadata
LABEL org.opencontainers.image.title="EntraIDSecretRotator"
LABEL org.opencontainers.image.description="Automated rotation of Azure Entra ID app registration secrets"

# Copy the entire assembled filesystem in one layer
COPY --from=runtime-deps /rootfs /

# Set working directory
WORKDIR /app

# Set environment variables
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
ENV SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt

# Set user
USER nobody

# Run as the application
ENTRYPOINT ["/app/EntraIDSecretRotator"]

# Default command (can be overridden)
CMD ["rotate", "--dry-run"]
