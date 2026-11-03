#!/usr/bin/env bash
set -euo pipefail
for name in APPLE_BUILD_CERTIFICATE_BASE64 APPLE_CERTIFICATE_PASSWORD APPLE_ID APPLE_APP_SPECIFIC_PASSWORD APPLE_TEAM_ID APPLE_SIGNING_KEYCHAIN_PASSWORD APPLE_SIGN_APP_IDENTITY; do
  test -n "${!name}" || { echo "Missing Apple signing setting: $name" >&2; exit 1; }
done
certificate="$RUNNER_TEMP/apple-build-certificate.p12"
keychain="$RUNNER_TEMP/sunder-app-signing.keychain-db"
trap 'rm -f "$certificate"' EXIT
printf '%s' "$APPLE_BUILD_CERTIFICATE_BASE64" | base64 -D > "$certificate"
security create-keychain -p "$APPLE_SIGNING_KEYCHAIN_PASSWORD" "$keychain"
# Make cleanup possible even if a subsequent credential import fails.
echo "keychain_path=$keychain" >> "$GITHUB_OUTPUT"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$APPLE_SIGNING_KEYCHAIN_PASSWORD" "$keychain"
security default-keychain -s "$keychain"
security list-keychains -d user -s "$keychain"
security import "$certificate" -P "$APPLE_CERTIFICATE_PASSWORD" -A -t cert -f pkcs12 -k "$keychain"
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$APPLE_SIGNING_KEYCHAIN_PASSWORD" "$keychain" >/dev/null
xcrun notarytool store-credentials sunder-app-notary --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_SPECIFIC_PASSWORD" --keychain "$keychain"
echo 'notary_profile=sunder-app-notary' >> "$GITHUB_OUTPUT"
