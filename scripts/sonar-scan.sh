#!/usr/bin/env bash
#
# Runs a full SonarQube / SonarCloud analysis with test coverage.
#
# SonarAnalyzer already runs on every ordinary build (see Directory.Build.props), so this script
# is only needed to publish results to a server.
#
# Required environment:
#   SONAR_TOKEN      authentication token
#   SONAR_HOST_URL   e.g. https://sonarcloud.io or your internal SonarQube URL
# Optional:
#   SONAR_PROJECT_KEY  defaults to "Grove"
#   SONAR_ORGANIZATION required by SonarCloud, ignored by self-hosted SonarQube
#
set -euo pipefail

cd "$(dirname "$0")/.."

: "${SONAR_TOKEN:?set SONAR_TOKEN before running}"
: "${SONAR_HOST_URL:?set SONAR_HOST_URL before running}"
PROJECT_KEY="${SONAR_PROJECT_KEY:-Grove}"

# The scanner is installed as a local tool so the version is pinned in the repository.
if [ ! -f .config/dotnet-tools.json ]; then
  dotnet new tool-manifest
  dotnet tool install dotnet-sonarscanner
fi
dotnet tool restore

ORG_ARG=()
if [ -n "${SONAR_ORGANIZATION:-}" ]; then
  ORG_ARG=(/o:"${SONAR_ORGANIZATION}")
fi

dotnet sonarscanner begin \
  /k:"${PROJECT_KEY}" \
  "${ORG_ARG[@]}" \
  /d:sonar.host.url="${SONAR_HOST_URL}" \
  /d:sonar.token="${SONAR_TOKEN}" \
  /d:sonar.cs.opencover.reportsPaths="**/coverage.opencover.xml" \
  /d:sonar.scanner.scanAll=false

dotnet build --no-incremental

dotnet test \
  --no-build \
  /p:CollectCoverage=true \
  /p:CoverletOutputFormat=opencover \
  /p:CoverletOutput=./coverage.opencover.xml

dotnet sonarscanner end /d:sonar.token="${SONAR_TOKEN}"
