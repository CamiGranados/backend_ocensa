#!/usr/bin/env bash
# Build a clean IIS package from a committed snapshot; never start the API or migrate data.
# Usage: ./scripts/publish-backend.sh /existing/parent/new-output-directory [git-ref]
set -euo pipefail
umask 077

fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
[[ $# -ge 1 && $# -le 2 ]] || fail "Usage: $0 NEW_OUTPUT_DIRECTORY [GIT_REF]"
for tool in git dotnet tar find cmp mktemp cp; do
  command -v "$tool" >/dev/null 2>&1 || fail "Required command not found: $tool"
done

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
repo_root=$(git -C "$script_dir" rev-parse --show-toplevel)
repo_root=$(cd -- "$repo_root" && pwd -P)
git_ref=${2:-HEAD}
[[ "$git_ref" != -* ]] || fail "GIT_REF must not begin with '-'."
source_sha=$(git -C "$repo_root" rev-parse --verify "${git_ref}^{commit}")
output_name=$(basename -- "$1")
[[ -n "$output_name" && "$output_name" != . && "$output_name" != .. && "$output_name" != / ]] || fail "Specify a new directory name."
output_parent=$(cd -- "$(dirname -- "$1")" && pwd -P) || fail "The output parent directory must already exist."
output_dir="$output_parent/$output_name"
[[ ! -e "$output_dir" && ! -L "$output_dir" ]] || fail "Output already exists; nothing will be overwritten."
case "$output_dir/" in
  "$repo_root/"*) fail "Choose an output directory outside this Git checkout." ;;
esac
sdk_version=$(dotnet --version)
[[ "$sdk_version" == 8.* ]] || fail "Use a .NET 8 SDK for this backend."

staging=$(mktemp -d "$output_parent/.backend-publish.XXXXXX")
cleanup() { rm -rf -- "$staging"; }
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
snapshot="$staging/source"
package="$staging/package"
mkdir -- "$snapshot" "$package"
git -C "$repo_root" archive --format=tar "$source_sha" backend/DashboardApi | tar -xf - -C "$snapshot"
# Delete generated directories only inside the newly created snapshot.
find "$snapshot" -type d \( -name bin -o -name obj \) -prune -exec rm -rf -- {} +

project_dir="$snapshot/backend/DashboardApi"
project="$project_dir/DashboardApi.csproj"
[[ -f "$project" ]] || fail "The selected commit does not contain the expected backend project."
for config in appsettings.json validacion-config.json; do
  [[ -f "$project_dir/$config" ]] || fail "Required source file is missing: $config"
done
printf 'Publishing committed snapshot %s (Release, net8.0, framework-dependent).\n' "$source_sha"
dotnet publish "$project" --configuration Release --framework net8.0 \
  --self-contained false -p:UseAppHost=false --output "$package" --nologo

for required in DashboardApi.dll DashboardApi.deps.json DashboardApi.runtimeconfig.json web.config; do
  [[ -s "$package/$required" ]] || fail "Publish output is missing or empty: $required"
done
for config in appsettings.json validacion-config.json; do
  cmp -s "$project_dir/$config" "$package/$config" || fail "Published $config does not match the committed source."
done
cat > "$package/BUILD-MANIFEST.txt" <<EOF
source_commit=$source_sha
source_project=backend/DashboardApi/DashboardApi.csproj
configuration=Release
target_framework=net8.0
self_contained=false
dotnet_sdk=$sdk_version
appsettings_matches_committed_source=true
validation_config_matches_committed_source=true
tracked_bin_and_obj_excluded=true
api_started=false
database_accessed=false
production_deployment_validated=false
EOF

# Atomically reserve the new destination; never copy into an existing directory.
mkdir -- "$output_dir" || fail "Could not reserve the new output directory; nothing was copied."
if ! cp -Rp "$package/." "$output_dir/"; then
  fail "Final copy failed. The new, possibly incomplete output was retained for inspection: $output_dir"
fi
printf 'Validated package created: %s\n' "$output_dir"
printf 'Packaging only: no API start, database operation, or production deployment was performed.\n'
