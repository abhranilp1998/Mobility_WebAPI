#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  printf 'Usage: %s /path/to/MobilityDashboardApi.zip\n' "$0" >&2
  exit 1
fi

zip_path="$1"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_directory="$(cd "$script_dir/../Mobility.DynamicDashboard.Api" && pwd)"

if [[ ! -f "$zip_path" ]]; then
  printf 'Package does not exist: %s\n' "$zip_path" >&2
  exit 1
fi

for required_command in unzip jq grep cmp; do
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
  "MobilityDashboardApi/appsettings.Development.json" \
  "MobilityDashboardApi/appsettings.Server.json"; do
  if ! grep -Fxq "$required_entry" <<<"$archive_entries"; then
    printf 'Required package entry is missing: %s\n' "$required_entry" >&2
    exit 1
  fi
done

for configuration_file in "$project_directory"/appsettings*.json; do
  configuration_name="${configuration_file##*/}"
  configuration_entry="MobilityDashboardApi/$configuration_name"
  if ! grep -Fxq "$configuration_entry" <<<"$archive_entries"; then
    printf 'Required package entry is missing: %s\n' "$configuration_entry" >&2
    exit 1
  fi
  unzip -p "$zip_path" "$configuration_entry" | jq empty
  if ! unzip -p "$zip_path" "$configuration_entry" | \
     cmp -s "$configuration_file" -; then
    printf 'Packaged settings differ from the current project file: %s\n' \
      "$configuration_name" >&2
    exit 1
  fi
done

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
printf '%s\n' 'All current appsettings files are preserved byte-for-byte, including appsettings.Server.json.'
