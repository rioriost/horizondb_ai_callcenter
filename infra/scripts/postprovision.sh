#!/usr/bin/env sh
set -eu

load_azd_env() {
  while IFS='=' read -r key value; do
    value=$(printf '%s' "$value" | sed 's/^"//' | sed 's/"$//')
    export "$key=$value"
  done <<EOF
$(azd env get-values)
EOF
}

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Required command '$1' was not found. Install it and run 'azd hooks run postprovision'." >&2
    exit 1
  fi
}

load_azd_env
require_command az
require_command psql

: "${AZURE_SUBSCRIPTION_ID:?AZURE_SUBSCRIPTION_ID is required}"
: "${AZURE_RESOURCE_GROUP:?AZURE_RESOURCE_GROUP is required}"
: "${AZURE_KEY_VAULT_NAME:?AZURE_KEY_VAULT_NAME is required}"
: "${POSTGRES_HOST:?POSTGRES_HOST is required}"
: "${POSTGRES_DATABASE:?POSTGRES_DATABASE is required}"
: "${POSTGRES_USERNAME:?POSTGRES_USERNAME is required}"
: "${HORIZONDB_CLUSTER_NAME:?HORIZONDB_CLUSTER_NAME is required}"
: "${HORIZONDB_POOL_NAME:=pool1}"
: "${AZURE_OPENAI_ENDPOINT:?AZURE_OPENAI_ENDPOINT is required}"
: "${AZURE_OPENAI_API_VERSION:?AZURE_OPENAI_API_VERSION is required}"
: "${AZURE_OPENAI_EMBED_DEPLOYMENT:?AZURE_OPENAI_EMBED_DEPLOYMENT is required}"
: "${AZURE_OPENAI_EMBED_MODEL:?AZURE_OPENAI_EMBED_MODEL is required}"
: "${AZURE_OPENAI_CHAT_DEPLOYMENT:?AZURE_OPENAI_CHAT_DEPLOYMENT is required}"
: "${AZURE_OPENAI_CHAT_MODEL:?AZURE_OPENAI_CHAT_MODEL is required}"

if [ -z "${HORIZONDB_PARAMETER_GROUP_NAME:-}" ]; then
  HORIZONDB_PARAMETER_GROUP_NAME=$(az resource list \
    -g "$AZURE_RESOURCE_GROUP" \
    --resource-type Microsoft.HorizonDb/parameterGroups \
    --query "[?starts_with(name, '${HORIZONDB_CLUSTER_NAME}-p-')].name | [-1]" \
    -o tsv)
fi

: "${HORIZONDB_PARAMETER_GROUP_NAME:?HORIZONDB_PARAMETER_GROUP_NAME is required}"

echo "Attaching HorizonDB parameter group: ${HORIZONDB_PARAMETER_GROUP_NAME}"
PARAM_GROUP_ID="/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${AZURE_RESOURCE_GROUP}/providers/Microsoft.HorizonDb/parameterGroups/${HORIZONDB_PARAMETER_GROUP_NAME}"
az rest --method PATCH \
  --url "https://management.azure.com/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${AZURE_RESOURCE_GROUP}/providers/Microsoft.HorizonDb/clusters/${HORIZONDB_CLUSTER_NAME}?api-version=2026-01-20-preview" \
  --body "{\"properties\":{\"parameterGroup\":{\"id\":\"${PARAM_GROUP_ID}\",\"applyImmediately\":true}}}" \
  --only-show-errors >/dev/null

echo "Adding HorizonDB firewall rules"
az rest --method PUT \
  --url "https://management.azure.com/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${AZURE_RESOURCE_GROUP}/providers/Microsoft.HorizonDb/clusters/${HORIZONDB_CLUSTER_NAME}/pools/${HORIZONDB_POOL_NAME}/firewallRules/AllowAzureServices?api-version=2026-01-20-preview" \
  --body '{"properties":{"startIpAddress":"0.0.0.0","endIpAddress":"0.0.0.0","description":"Allow Azure services"}}' \
  --only-show-errors >/dev/null

DEPLOYER_IP=$(curl -fsS https://api.ipify.org || true)
if [ -n "$DEPLOYER_IP" ]; then
  az rest --method PUT \
    --url "https://management.azure.com/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${AZURE_RESOURCE_GROUP}/providers/Microsoft.HorizonDb/clusters/${HORIZONDB_CLUSTER_NAME}/pools/${HORIZONDB_POOL_NAME}/firewallRules/AllowDeployerMachine?api-version=2026-01-20-preview" \
    --body "{\"properties\":{\"startIpAddress\":\"${DEPLOYER_IP}\",\"endIpAddress\":\"${DEPLOYER_IP}\",\"description\":\"azd postprovision deployer machine\"}}" \
    --only-show-errors >/dev/null
fi

export PGHOST="$POSTGRES_HOST"
export PGPORT="${POSTGRES_PORT:-5432}"
export PGUSER="$POSTGRES_USERNAME"
export PGPASSWORD
PGPASSWORD=$(az keyvault secret show --vault-name "$AZURE_KEY_VAULT_NAME" --name postgres-password --query value -o tsv)
export PGSSLMODE=require

echo "Syncing HorizonDB admin password from Key Vault"
az rest --method PATCH \
  --url "https://management.azure.com/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${AZURE_RESOURCE_GROUP}/providers/Microsoft.HorizonDb/clusters/${HORIZONDB_CLUSTER_NAME}?api-version=2026-01-20-preview" \
  --body "{\"properties\":{\"administratorLoginPassword\":\"${PGPASSWORD}\"}}" \
  --only-show-errors >/dev/null

echo "Waiting for HorizonDB connectivity"
for i in $(seq 1 60); do
  if psql -d postgres -c "SELECT 1" >/dev/null 2>&1; then
    break
  fi
  if [ "$i" -eq 60 ]; then
    echo "HorizonDB did not accept connections in time." >&2
    exit 1
  fi
  sleep 10
done

echo "Creating application database"
DB_EXISTS=$(psql -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='${POSTGRES_DATABASE}'")
if [ "$DB_EXISTS" != "1" ]; then
  psql -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE ${POSTGRES_DATABASE}"
fi

echo "Applying schema"
for i in $(seq 1 30); do
  EXTENSIONS=$(psql -d "$POSTGRES_DATABASE" -tAc "SHOW azure.extensions;" 2>/dev/null || true)
  case "$EXTENSIONS" in
    *vector*azure_ai*|*azure_ai*vector*) break ;;
  esac
  if [ "$i" -eq 30 ]; then
    echo "HorizonDB extension allow-list did not include vector and azure_ai. Current value: ${EXTENSIONS}" >&2
    exit 1
  fi
  sleep 10
done
psql -d "$POSTGRES_DATABASE" -v ON_ERROR_STOP=1 -f ./infra/scripts/schema.sql

echo "Registering HorizonDB AI model aliases"
psql -d "$POSTGRES_DATABASE" -c "SELECT model_registry.model_remove('app-embedding');" >/dev/null 2>&1 || true
psql -d "$POSTGRES_DATABASE" -c "SELECT model_registry.model_remove('app-reranker');" >/dev/null 2>&1 || true

psql -d "$POSTGRES_DATABASE" -v ON_ERROR_STOP=1 -c \
  "SELECT model_registry.model_add('app-embedding', '${AZURE_OPENAI_ENDPOINT}', '${AZURE_OPENAI_EMBED_DEPLOYMENT}', '${AZURE_OPENAI_EMBED_MODEL}', '${AZURE_OPENAI_API_VERSION}', 'managed-identity', NULL);"

psql -d "$POSTGRES_DATABASE" -v ON_ERROR_STOP=1 -c \
  "SELECT model_registry.model_add('app-reranker', '${AZURE_OPENAI_ENDPOINT}', '${AZURE_OPENAI_CHAT_DEPLOYMENT}', '${AZURE_OPENAI_CHAT_MODEL}', '${AZURE_OPENAI_API_VERSION}', 'managed-identity', NULL);"

echo "Seeding response master"
psql -d "$POSTGRES_DATABASE" -v ON_ERROR_STOP=1 -f ./infra/scripts/seed.sql

echo "Generating response master embeddings"
OPENAI_TOKEN=$(az account get-access-token --resource https://cognitiveservices.azure.com/ --query accessToken -o tsv)
export OPENAI_TOKEN
python3 - <<'PY' > /tmp/response-master-embeddings.sql
import json
import os
import urllib.request

endpoint = os.environ["AZURE_OPENAI_ENDPOINT"].rstrip("/")
deployment = os.environ["AZURE_OPENAI_EMBED_DEPLOYMENT"]
api_version = os.environ["AZURE_OPENAI_API_VERSION"]
token = os.environ["OPENAI_TOKEN"]

responses = [
    ("00000000-0000-0000-0000-000000000001", "お問い合わせありがとうございます。ご本人確認のため、お名前とご登録のお電話番号を教えてください。"),
    ("00000000-0000-0000-0000-000000000002", "ご不便をおかけして申し訳ありません。状況を確認しますので、発生している問題をもう少し詳しく教えてください。"),
    ("00000000-0000-0000-0000-000000000003", "料金や請求内容について確認します。対象の請求月または請求番号が分かれば教えてください。"),
    ("00000000-0000-0000-0000-000000000004", "契約内容の変更をご希望ですね。現在の契約内容を確認したうえで、変更可能な選択肢をご案内します。"),
    ("00000000-0000-0000-0000-000000000005", "解約についてのご相談ですね。手続き前に注意事項と代替プランをご案内します。"),
    ("00000000-0000-0000-0000-000000000006", "担当者への引き継ぎが必要な内容です。会話内容を記録したうえで、オペレーターにおつなぎします。"),
]

url = f"{endpoint}/openai/deployments/{deployment}/embeddings?api-version={api_version}"
for response_id, text in responses:
    payload = json.dumps({"input": text}).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=payload,
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
        },
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        body = json.loads(response.read().decode("utf-8"))
    vector = "[" + ",".join(str(value) for value in body["data"][0]["embedding"]) + "]"
    print(f"UPDATE response_master SET embedding = '{vector}'::vector, updated_at = now() WHERE id = '{response_id}'::uuid;")
PY
psql -d "$POSTGRES_DATABASE" -v ON_ERROR_STOP=1 -f /tmp/response-master-embeddings.sql

echo "Verifying model aliases"
psql -d "$POSTGRES_DATABASE" -v ON_ERROR_STOP=1 -c "SELECT * FROM model_registry.model_list_all();"
