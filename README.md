# EntraIDSecretRotator

A .NET 10 console application that rotates Azure Entra ID app registration client secrets and writes the new values to Azure Key Vault, so that Kubernetes workloads pick them up without human involvement. It is built to run daily as a Kubernetes CronJob and doubles as a CLI for inspection and dry runs.

For the rotation logic, safety guarantees and metric definitions, see [DESIGN.md](/DESIGN.md).

## What it does

Every run:

1. Lists all app registrations whose display name contains the configured filter (default `myfilter`) and reads their secret metadata. Secret values are never readable from Entra ID.
2. Lists all secrets in the configured Key Vaults and parses each secret's content-type as `{app-reg-name}:{clientId}:{secretId}`. That content-type is the only mapping between a Key Vault secret and the Entra ID secret it holds.
3. Builds a plan in memory: which mapped secrets expire within the threshold (default 30 days), which expired secrets are safe to delete, which secrets are orphans (expiring but not mapped in any vault), and which are mapped in more than one vault.
4. Executes the plan: creates a new secret in Entra ID (default validity 180 days), writes value, expiry and updated content-type to every mapped Key Vault, rolls back the Entra ID secret if a Key Vault write fails, and deletes expired secrets only when Key Vault version history proves the rotator created their replacement.
5. Emits OpenTelemetry metrics and structured logs for dashboards and alerting.

Workloads receive the new value through the Secrets Store CSI driver (polls Key Vault every 2 minutes by default) and Stakater Reloader, which restarts deployments when the synced Kubernetes Secret changes.

## Prerequisites

- .NET 10 SDK (`dotnet --version` should print 10.x)
- An Azure identity with:
  - Microsoft Graph application permission `Application.ReadWrite.OwnedBy` (admin consent required). It can list every app registration in the tenant but can only create, update and delete secrets on app registrations it **owns**.
  - Key Vault Secrets Officer on every configured Key Vault.
- The identity added as an owner of each app registration that should be rotated. The portal only offers users as owners, so use the CLI:

```bash
az ad app owner add --id <app-client-id> --owner-object-id <identity-service-principal-object-id>
```

## Authentication

The application uses `DefaultAzureCredential` with only three credential sources enabled, because the container image has no CLI tooling:

1. Environment variables (`AZURE_TENANT_ID`, `AZURE_CLIENT_ID` plus `AZURE_CLIENT_SECRET` or `AZURE_CLIENT_CERTIFICATE_PATH`)
2. Workload Identity (Kubernetes, via the `azure.workload.identity/use` label and a federated service account)
3. Managed Identity

`az login`, Visual Studio, PowerShell, `azd` and interactive browser credentials are excluded in `Program.cs`. For local runs, export the environment variables of a service principal that has the permissions above, or run with a federated token file (`AZURE_FEDERATED_TOKEN_FILE`).

## Configuration

Precedence, highest first: **CLI option > environment variable > appsettings.json > built-in default**.

Environment variables use the `Rotator__` prefix (standard .NET configuration binding):

| Variable | Default | Purpose |
|---|---|---|
| `Rotator__KeyVaults` | none, **required** | Comma-separated Key Vault names. The program exits with code 1 if none are configured |
| `Rotator__AppFilter` | `myfilter` | Case-insensitive "contains" filter on app registration display name |
| `Rotator__Days` | `30` | Rotate or report secrets expiring within this many days |
| `Rotator__ValidityDays` | `180` | Validity of newly created secrets |
| `Rotator__NotExpired` | `false` | Exclude already expired secrets from reports and rotation |
| `Rotator__DryRun` | `false` | Plan and log, but change nothing |
| `Rotator__Output` | `table` | `table`, `json` or `yaml` |

Other environment variables:

| Variable | Default | Purpose |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | When set, metrics and logs are exported over OTLP gRPC to this endpoint |
| `OTEL_SERVICE_NAME` | unset | Read by the OpenTelemetry SDK as the resource service name on exported metrics |
| `LOG_PATH` | `/tmp/log/entraid-rotator` | Directory for rolling JSON log files (daily, 7 files kept, 50 MB each) |
| `ENVIRONMENT` | `development` | Reported as `deployment.environment` on exported logs |

`appsettings.json` ships with the defaults above, an empty `KeyVaults` array, and the Serilog minimum levels (Information, with Microsoft, Azure and System at Warning).

## CLI usage

```bash
dotnet run --project EntraIDSecretRotator -- <command> [options]
```

Read-only commands:

| Command | Purpose | Options |
|---|---|---|
| `list` | All app registrations with their secrets and expiry | `--app-filter/-f`, `--output/-o`, `--dry-run/-n` |
| `expiring` | Secrets expiring within N days | `--days/-d`, `--app-filter/-f`, `--output/-o`, `--not-expired`, `--dry-run/-n` |
| `expired` | Secrets that have already expired | `--app-filter/-f`, `--output/-o`, `--dry-run/-n` |
| `keyvaults` | All Key Vault secrets with mapping status | `--vault/-v`, `--status/-s` (`mapped`, `unmapped`, `invalid`), `--output/-o` |
| `orphans` | Expiring secrets that are not mapped in any Key Vault | `--filter/-f`, `--days/-d`, `--not-expired/-n`, `--output/-o` |

Commands that change things:

| Command | Purpose | Options |
|---|---|---|
| `rotate` | Rotate mapped secrets expiring within N days, then delete expired secrets that are proven replaced | `--filter/-f`, `--days/-d`, `--not-expired/-n`, `--validity-days`, `--output/-o`, `--dry-run` |
| `cleanup` | Delete expired secrets only, with the same proof requirement | `--filter/-f`, `--output/-o`, `--dry-run` |

Note the short flags: `-n` means `--dry-run` on `list`, `expiring` and `expired`, but `--not-expired` on `rotate` and `orphans`. Spell out `--dry-run` on `rotate` and `cleanup`.

Examples:

```bash
# What would the daily run do? Nothing is changed.
Rotator__AppFilter=myfilter Rotator__KeyVaults=key-vault-0 dotnet run --project EntraIDSecretRotator -- rotate --dry-run

# Secrets expiring within a week, as JSON
Rotator__AppFilter=myfilter Rotator__KeyVaults=key-vault-0 dotnet run --project EntraIDSecretRotator -- expiring --days 7 --not-expired -o json

# Key Vault secrets whose content-type is missing or malformed
Rotator__AppFilter=myfilter Rotator__KeyVaults=key-vault-0 dotnet run --project EntraIDSecretRotator -- keyvaults --status unmapped
Rotator__AppFilter=myfilter Rotator__KeyVaults=key-vault-0 dotnet run --project EntraIDSecretRotator -- keyvaults --status invalid
```

## Onboarding a secret for rotation

1. Make the rotator identity an owner of the app registration (command above).
2. Set the Key Vault secret's content-type to `{app-reg-name}:{clientId}:{secretId}`, where `secretId` is the key id of the Entra ID secret whose value the vault currently holds. Example: `myfilter-app-reg:12345678-abcd-1234-abcd-123456789abc:12345678-aa11-bb22-cc33-a1b2c3d4e5f6`.
3. Run `keyvaults --status mapped` or `rotate --dry-run` to confirm the mapping is recognised.

Secrets without a valid content-type are reported as orphans and never touched.

## Resilience

- Key Vault write fails: the new Entra ID secret is deleted again (rollback).
- Secret mapped in several vaults: all vaults are updated or all are reverted.
- Expired secret without proof of replacement: not deleted, reported as `manual_intervention_required`.
- Graph rejects a write transiently (HTTP 429, 503, 504 or "concurrent requests being made to the tenant"): `Services/GraphRetry.cs` retries up to 4 attempts with exponential backoff (1s, 2s, 4s plus jitter, capped at 30s) and honours `Retry-After`. Only explicit rejections are retried, so a retry can never create a secret twice.
- Authorization errors such as "Insufficient privileges" fail immediately and surface as `rotation_failed{reason="create_secret_failed"}`. The usual cause is a missing owner assignment.

## Observability

Metrics are emitted through the `EntraIDSecretRotator` meter and exported over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `entraid_rotator_app_registrations_total` | gauge | | App registrations matching the filter |
| `entraid_rotator_secrets_expiring` | gauge | `expiry_bucket` = `expired`, `30d`, `60d`, `90d`, `90d+` | Secret count per bucket |
| `entraid_rotator_secret_expiry_info` | gauge | `app_name`, `client_id`, `expiry_bucket`, `days_until_expiry` | One series per secret, including replaced secrets waiting to expire |
| `entraid_rotator_orphan_secrets` | gauge | `app_name`, `client_id`, `days_until_expiry`, `pending_deletion` | Expiring or expired secrets not mapped in any vault. `pending_deletion="true"` means the rotator replaced it and deletes it at expiry; `"false"` means a human must act |
| `entraid_rotator_rotation_failed` | gauge | `app_name`, `client_id`, `reason` | Failed rotations in this run |
| `entraid_rotator_manual_intervention_required` | gauge | `app_name`, `client_id`, `secret_id` | Expired secrets that could not be deleted safely |
| `entraid_rotator_duplicate_mappings` | gauge | `client_id`, `secret_id` | Secrets mapped in more than one vault (value is the vault count) |
| `entraid_rotator_secrets_rotated_total` | counter | `status` = `success` or `failed`, `app_name` | Rotation attempts |
| `entraid_rotator_secrets_deleted_total` | counter | | Expired secrets deleted |
| `entraid_rotator_job_duration_seconds` | histogram | | Run duration |

The job runs for a short time once a day, so the gauges only exist while the pod runs. Dashboards and alert rules should wrap them in `last_over_time(...[25h])`. `days_until_expiry` is a label, so numeric thresholds are regexes, for example `days_until_expiry=~"^(-[0-9]+|[0-9])$"` for "10 days or less, or already expired".

Logs go to three sinks via Serilog: OTLP (when the endpoint is set), JSON on stderr, and rolling JSON files under `LOG_PATH`. `deploy/prometheusrules.yaml` has five example alert rules: rotation failed, secret expiring within 10 days with no replacement, manual intervention required, rotator silent for 26 hours, and CronJob failed. No dashboard is included.

## Build and test

```bash
dotnet restore
dotnet build
dotnet test
```

Tests use xUnit, AwesomeAssertions and Moq. Internal invariants are checked with the fail-fast assertions in `Infrastructure/Assert.cs`; those are for programmer errors, not for user input.

## Container image

The `Dockerfile` publishes a native AOT binary on Alpine and copies it into a `scratch` image that runs as `nobody`. The image's default command is `rotate --dry-run`, so an unconfigured container changes nothing.

```bash
docker build -t entraid-secret-rotator .
docker run --rm -e Rotator__AppFilter=myfilter -e Rotator__KeyVaults=key-vault-0 -e AZURE_TENANT_ID=... -e AZURE_CLIENT_ID=... -e AZURE_CLIENT_SECRET=... entraid-secret-rotator rotate --dry-run
```

## Deployment

`deploy/` holds reference manifests for Kustomize:

- `namespace.yaml` and `serviceaccount.yaml`: namespace `secretrotator` and service account `secretrotator-sa` with Workload Identity
- `cronjob.yaml`: schedule `0 7 * * *` in `Europe/Oslo`, `concurrencyPolicy: Forbid`, 10-minute deadline, one retry, 128 MiB memory, argument `rotate`
- `prometheusrules.yaml`: the five example alert rules (requires the Prometheus Operator CRDs)

Replace the placeholders before applying:

| File | Placeholder | Set to |
|---|---|---|
| `kustomization.yaml` | `your.registry.io/your-repo`, `x.y.z` | Your image and tag |
| `serviceaccount.yaml` | `<your-managed-identity-client-id>` | Client ID of the managed identity federated with `secretrotator-sa` |
| `cronjob.yaml` | `myfilter`, `key-vault-0,key-vault-1,...`, `http://your-otel-collector` | Your app filter, Key Vault names and OTLP endpoint |

```bash
kubectl apply -k deploy/
```

Check the deployed image and the latest run:

```bash
kubectl get cronjob entraid-secret-rotator -n secretrotator -o jsonpath='{.spec.jobTemplate.spec.template.spec.containers[0].image}'
kubectl logs job/$(kubectl get jobs -n secretrotator --sort-by=.metadata.creationTimestamp -o jsonpath='{.items[-1].metadata.name}') -n secretrotator
```

## Repository layout

```
entraid-secret-rotator/
├── README.md                    # This file
├── DESIGN.md                    # Rotation logic, safety guarantees, metrics
├── HOWTO.md                     # Onboarding an app registration, step by step
├── Dockerfile                   # Native AOT multi-stage build
├── deploy/                      # Reference manifests: namespace, service account, CronJob, alert rules
├── EntraIDSecretRotator/
│   ├── Commands/                # list, expiring, expired, keyvaults, orphans, rotate, cleanup
│   ├── Services/                # EntraIdService, KeyVaultService, RotationPlanBuilder, GraphRetry
│   ├── Models/                  # AppRegistration, SecretInfo, CreatedSecretInfo, KeyVaultSecretInfo, RotationPlan
│   ├── Infrastructure/          # Assert, RotatorOptions, OutputFormat, DateTimeOffsetConverter
│   ├── Telemetry/               # Metrics, TelemetrySetup
│   ├── appsettings.json
│   └── Program.cs
└── EntraIDSecretRotator.Tests/  # xUnit tests mirroring the folders above
```

## License

See [LICENSE](./LICENSE).
