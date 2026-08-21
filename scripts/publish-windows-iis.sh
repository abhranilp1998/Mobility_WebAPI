#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "$script_dir/.." && pwd)"
project_path="$repository_root/Mobility.DynamicDashboard.Api/Mobility.DynamicDashboard.Api.csproj"
timestamp="$(date +%Y%m%d-%H%M%S)"
artifact_root="${1:-$repository_root/artifacts/windows-iis-$timestamp}"
publish_directory="$artifact_root/MobilityDashboardApi"
zip_path="$artifact_root/MobilityDashboardApi.zip"

if [[ -e "$artifact_root" ]]; then
  printf 'Refusing to overwrite existing artifact path: %s\n' "$artifact_root" >&2
  exit 1
fi

for required_command in dotnet jq ditto; do
  if ! command -v "$required_command" >/dev/null 2>&1; then
    printf 'Required command is unavailable: %s\n' "$required_command" >&2
    exit 1
  fi
done

for configuration_file in \
  "$repository_root"/Mobility.DynamicDashboard.Api/appsettings*.json; do
  jq empty "$configuration_file"
done

mkdir -p "$publish_directory"

dotnet publish "$project_path" \
  --configuration Release \
  --runtime win-x64 \
  --self-contained false \
  --output "$publish_directory"

for configuration_file in "$publish_directory"/appsettings*.json; do
  jq empty "$configuration_file"
done

if [[ ! -f "$publish_directory/Mobility.DynamicDashboard.Api.exe" ]]; then
  printf '%s\n' 'Windows app host is missing from publish output.' >&2
  exit 1
fi

if ! grep -q 'ASPNETCORE_ENVIRONMENT' "$publish_directory/web.config" || \
   ! grep -q 'value="Development"' "$publish_directory/web.config"; then
  printf '%s\n' 'Published web.config lost the required Development environment.' >&2
  exit 1
fi

if jq -e '
    [(.ConnectionStrings // {})[] | select(type == "string" and length > 0)]
    | length > 0
  ' "$publish_directory/appsettings.json" >/dev/null; then
  printf '%s\n' 'Refusing to package plaintext connection strings from appsettings.json.' >&2
  exit 1
fi

ditto -c -k --sequesterRsrc --keepParent "$publish_directory" "$zip_path"

"$script_dir/verify-windows-iis-package.sh" "$zip_path"

printf 'Windows IIS folder: %s\n' "$publish_directory"
printf 'Windows IIS ZIP: %s\n' "$zip_path"
printf '%s\n' 'Server secret file is intentionally not included.'
