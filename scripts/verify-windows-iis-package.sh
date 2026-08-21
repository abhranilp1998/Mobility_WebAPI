#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  printf 'Usage: %s /path/to/MobilityDashboardApi.zip\n' "$0" >&2
  exit 1
fi

zip_path="$1"
expected_legacy_base_url="${EXPECTED_LEGACY_BASE_URL:-http://192.168.192.196:8087}"

if [[ ! -f "$zip_path" ]]; then
  printf 'Package does not exist: %s\n' "$zip_path" >&2
  exit 1
fi

for required_command in unzip jq grep; do
  if ! command -v "$required_command" >/dev/null 2>&1; then
    printf 'Required command is unavailable: %s\n' "$required_command" >&2
    exit 1
  fi
done

archive_entries="$(unzip -Z1 "$zip_path")"
for required_entry in \
  "MobilityDashboardApi/Mobility.DynamicDashboard.Api.exe" \
  "MobilityDashboardApi/Mobility.DynamicDashboard.Api.dll" \
  "MobilityDashboardApi/web.config" \
  "MobilityDashboardApi/appsettings.json" \
  "MobilityDashboardApi/appsettings.Development.json"; do
  if ! grep -Fxq "$required_entry" <<<"$archive_entries"; then
    printf 'Required package entry is missing: %s\n' "$required_entry" >&2
    exit 1
  fi
done

for configuration_entry in \
  "MobilityDashboardApi/appsettings.json" \
  "MobilityDashboardApi/appsettings.Development.json"; do
  unzip -p "$zip_path" "$configuration_entry" | jq empty
done

development_configuration="$(
  unzip -p "$zip_path" \
    "MobilityDashboardApi/appsettings.Development.json"
)"

if ! jq -e --arg expected "$expected_legacy_base_url" '
    .DashboardApi.CurrentOpp.Legacy.BaseUrl == $expected and
    .DashboardApi.TaskStatus.Legacy.BaseUrl == $expected and
    .DashboardApi.WorkDone.Legacy.BaseUrl == $expected
  ' <<<"$development_configuration" >/dev/null; then
  printf 'Development legacy BaseUrl must be %s for all live dashboards.\n' \
    "$expected_legacy_base_url" >&2
  exit 1
fi

web_configuration="$(
  unzip -p "$zip_path" "MobilityDashboardApi/web.config"
)"

if ! grep -q 'ASPNETCORE_ENVIRONMENT' <<<"$web_configuration" || \
   ! grep -q 'value="Development"' <<<"$web_configuration"; then
  printf '%s\n' 'Package web.config does not preserve Development.' >&2
  exit 1
fi

if unzip -p "$zip_path" "MobilityDashboardApi/appsettings.json" | jq -e '
    [(.ConnectionStrings // {})[] | select(type == "string" and length > 0)]
    | length > 0
  ' >/dev/null; then
  printf '%s\n' 'Package contains plaintext connection strings.' >&2
  exit 1
fi

printf 'Verified Windows IIS package: %s\n' "$zip_path"
