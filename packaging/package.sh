#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 10 ]]; then
  echo "Usage: package.sh <repo> <publish> <key.pem> <key-id> <version> <os> <arch> <revision> <executable> <output>" >&2
  exit 2
fi

repository="$(cd "$1" && pwd)"
publish="$(cd "$2" && pwd)"
key="$(cd "$(dirname "$3")" && pwd)/$(basename "$3")"
key_id="$4"
version="$5"
target_os="$6"
target_arch="$7"
revision="$8"
executable="$9"
output="$(cd "$(dirname "${10}")" && pwd)/$(basename "${10}")"

[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]]
[[ "$target_os" =~ ^(linux|windows|macos)$ ]]
[[ "$target_arch" =~ ^(x64|arm64)$ ]]
[[ "$revision" =~ ^[0-9a-fA-F]{40}$|^[0-9a-fA-F]{64}$ ]]
[[ -f "$key" && -n "$key_id" ]]

stage="$(mktemp -d)"
trap 'rm -rf -- "$stage"' EXIT
mkdir -p "$stage/runtime/process" "$stage/manifest" "$stage/signature"
find "$publish" -maxdepth 1 -type f ! -name '*.pdb' -exec cp {} "$stage/runtime/process/" \;
cp -R "$repository/schemas" "$stage/schemas"
cp -R "$repository/sbom" "$stage/sbom"
cp -R "$repository/provenance" "$stage/provenance"
if [[ -d "$repository/client" ]]; then cp -R "$repository/client" "$stage/client"; fi

entrypoint="runtime/process/$executable"
[[ -f "$stage/$entrypoint" ]]
artifact_digest="sha256:$(sha256sum "$stage/$entrypoint" | cut -d' ' -f1)"
awk -v version="$version" -v target_os="$target_os" -v target_arch="$target_arch" -v entrypoint="$entrypoint" -v digest="$artifact_digest" '
  /^metadata:/ { metadata=1 }
  /^compatibility:/ { metadata=0 }
  metadata && /^  version:/ { print "  version: " version; next }
  /^      os:/ && !runtime_patched { print "      os: [" target_os "]"; next }
  /^      architectures:/ && !runtime_patched { print "      architectures: [" target_arch "]"; next }
  /^      entrypoint: runtime\/process\// && !runtime_patched { print "      entrypoint: " entrypoint; next }
  /^      digest:/ && !runtime_patched { print "      digest: " digest; runtime_patched=1; next }
  { print }
' "$repository/murchalka.module.yaml" > "$stage/manifest/murchalka.module.yaml"

module_id="$(awk '/^metadata:/{metadata=1;next} metadata && /^  id:/{print $2;exit}' "$stage/manifest/murchalka.module.yaml")"
capability_id="$(awk '/^  capabilities:/{capabilities=1;next} capabilities && /^    - id:/{print $3;exit}' "$stage/manifest/murchalka.module.yaml")"
capability_version="$(awk '/^  capabilities:/{capabilities=1;next} capabilities && /^      version:/{print $2;exit}' "$stage/manifest/murchalka.module.yaml")"
contract_path="$(awk '/^  capabilities:/{capabilities=1;next} capabilities && /^      contract:/{print $2;exit}' "$stage/manifest/murchalka.module.yaml")"
contract_digest="sha256:$(sha256sum "$stage/$contract_path" | cut -d' ' -f1)"
artifact_id="$(awk '/^  runtime:/{runtime=1;next} runtime && /^    - id:/{print $3;exit}' "$stage/manifest/murchalka.module.yaml")"

jq -c -n   --arg module "$module_id"   --arg version "$version"   --arg artifact "$artifact_id"   --arg artifactDigest "$artifact_digest"   --arg capability "$capability_id"   --arg capabilityVersion "$capability_version"   --arg contractDigest "$contract_digest"   '{schemaVersion:1,module:{id:$module,version:$version,bundleDigest:"sha256:0000000000000000000000000000000000000000000000000000000000000000"},resolvedAt:"2026-01-01T00:00:00.0000000+00:00",runtimeVersion:"0.1.0",dependencies:[],artifacts:[{target:"runtime",id:$artifact,digest:$artifactDigest}],contracts:[{id:$capability,version:$capabilityVersion,schemaDigest:$contractDigest}]}'   | sed 's/+00:00/\\u002B00:00/' | tr -d '\n' > "$stage/manifest/module.lock.json"

jq --arg version "$version" '.packages[0].versionInfo=$version | .name=(.packages[0].name+"-"+$version)'   "$stage/sbom/"*.spdx.json > "$stage/sbom/release.spdx.json"
find "$stage/sbom" -type f ! -name release.spdx.json -delete
lower_revision="$(printf '%s' "$revision" | tr '[:upper:]' '[:lower:]')"
jq --arg version "$version" --arg revision "$lower_revision" --arg target "$target_os-$target_arch"   '.version=$version | .sourceRevision=$revision | .target=$target'   "$stage/provenance/build.json" > "$stage/provenance/release.json"
mv "$stage/provenance/release.json" "$stage/provenance/build.json"

hashes="$(mktemp)"
find "$stage" -type f ! -path "$stage/manifest/file-hashes.json" ! -path "$stage/signature/*" -print0 |
  sort -z |
  while IFS= read -r -d '' file; do
    relative="${file#"$stage/"}"
    printf '%s\tsha256:%s\n' "$relative" "$(sha256sum "$file" | cut -d' ' -f1)"
  done > "$hashes"

canonical="$(mktemp)"
{
  printf 'murchalka-bundle-v1\n'
  while IFS=$'\t' read -r path digest; do printf '%s\n%s\n' "$path" "$digest"; done < "$hashes"
} > "$canonical"
bundle_digest="sha256:$(sha256sum "$canonical" | cut -d' ' -f1)"
jq -c --arg digest "$bundle_digest" '.module.bundleDigest=$digest' "$stage/manifest/module.lock.json" > "$stage/manifest/module.lock.release.json"
mv "$stage/manifest/module.lock.release.json" "$stage/manifest/module.lock.json"

jq -Rn '[inputs | split("\t") | {(.[0]): .[1]}] | add | {schemaVersion:1,algorithm:"sha256",files:.}'   < "$hashes" > "$stage/manifest/file-hashes.json"
signature="$(openssl dgst -sha256 -sign "$key" "$canonical" | base64 | tr -d '\n')"
jq -n --arg keyId "$key_id" --arg signature "$signature"   '{schemaVersion:1,publisher:"dev.murchalka",keyId:$keyId,algorithm:"ecdsa-p256-sha256",signature:$signature}'   > "$stage/signature/signature.json"

find "$stage" -exec touch -t 202601010000 {} +
rm -f -- "$output"
(cd "$stage" && find . -type f -print | LC_ALL=C sort | zip -X -q "$output" -@)
[[ -s "$output" ]]
echo "$bundle_digest"
