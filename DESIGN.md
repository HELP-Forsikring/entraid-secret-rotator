# Design Document: EntraID-Secret-Rotator

## Overview

EntraIDSecretRotator is a .NET console application that automates the rotation of Azure Entra ID app registration secrets that are stored in Azure Key Vaults and consumed by Kubernetes workloads.

## Problem Statement

- Multiple app registrations have secrets that expire periodically (6 months, 1 year, 2 years)
- Secrets are stored in Key Vaults and consumed via SecretProviderClass in Kubernetes
- Manual rotation is error-prone and doesn't scale
- Need automated rotation with proper safeguards and alerting

## Architecture

### High-Level Flow

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         EntraIDSecretRotator                            │
├─────────────────────────────────────────────────────────────────────────┤
│  PHASE 1: FETCH          PHASE 2: PLAN           PHASE 3: EXECUTE       │
│  ┌──────────────┐        ┌──────────────┐        ┌──────────────┐       │
│  │ Entra ID     │───────▶│ Match &      │───────▶│ Rotate       │       │
│  │ App Regs     │        │ Validate     │        │ Secrets      │       │
│  └──────────────┘        │              │        └──────────────┘       │
│  ┌──────────────┐        │ Build        │        ┌──────────────┐       │
│  │ Key Vault    │───────▶│ Action       │───────▶│ Update       │       │
│  │ Secrets      │        │ Plan         │        │ Key Vaults   │       │
│  └──────────────┘        └──────────────┘        └──────────────┘       │
│                                                  ┌──────────────┐       │
│                                                  │ Emit         │       │
│                                                  │ Metrics      │       │
│                                                  └──────────────┘       │
└─────────────────────────────────────────────────────────────────────────┘
```

### Deployment

- **Platform:** Kubernetes CronJob
- **Authentication:** Azure Workload Identity (Managed Identity)
- **Schedule:** Daily at 7 (Europe/Oslo), easily re-defined in `deploy/cronjob.yaml`
- **Namespace:** `secretrotator`

### Required Permissions

| Resource | Permission |
|----------|------------|
| Entra ID | `Application.ReadWrite.OwnedBy` |
| Key Vaults | Key Vault Secrets Officer |

## Key Vault Content-Type Format

The content-type field on Key Vault secrets establishes the mapping to Entra ID secrets:

```
{app-reg-name}:{clientId}:{secretId}
```

| Component | Purpose |
|-----------|---------|
| `app-reg-name` | Human-readable reference (not used by code, can become stale) |
| `clientId` | App registration's Application (client) ID - **immutable, used for lookup** |
| `secretId` | The specific secret's Key ID - **used to identify which secret** |

**Example:**
```
myfilter-app-reg:12345678-abcd-1234-abcd-123456789abc:12345678-aa11-bb22-cc33-a1b2c3d4e5f6
```

After rotation, the content-type is updated with the new secretId:
```
myfilter-app-reg:12345678-abcd-1234-abcd-123456789abc:NEW-SECRET-ID-HERE
```

## Rotation Logic

### Configuration

Environment variables use the `Rotator__` prefix (standard .NET configuration binding):

| Setting | Default | Description |
|---------|---------|-------------|
| `Rotator__Days` | 30 | Rotate secrets expiring within this many days |
| `Rotator__AppFilter` | "myfilter" | Filter app registrations by name |
| `Rotator__DryRun` | false | If true, show actions without executing |
| `Rotator__Output` | "table" | Output format: table, json, yaml |
| `Rotator__ValidityDays` | 180 | Validity period for newly created secrets (6 months) |
| `Rotator__KeyVaults` | (required) | Comma-separated list of Key Vault names |

### Phase 1: Fetch Data (API Calls)

```
1.1 Fetch all app registrations matching filter
    → Build map: clientId → {name, secrets: [{secretId, expiry, description}]}

1.2 Fetch all secrets from all configured Key Vaults
    → Build map: vaultName/secretName → {value, contentType, expiry, versions[]}

1.3 Parse content-types, extract valid mappings
    → Build map: clientId:secretId → [{vault, secretName, previousVersions}]
```

### Phase 2: Build Action Plan (No API Calls)

All matching and decision logic operates on in-memory data only.

```
2.1 Validate mappings
    - Match KV content-types to app registration secrets
    - Identify duplicates (same clientId:secretId in multiple KVs)

2.2 Categorize each mapped secret:
    - EXPIRED: Secret has already expired
    - EXPIRING: Secret expires within Rotator__Days
    - HEALTHY: Secret expires after Rotator__Days

2.3 Build action lists:
    - uniqueRotations: [{clientId, secretId, kvRef}]
    - duplicateRotations: [{clientId, secretId, kvRefs: [...]}]
    - deletions: [{clientId, secretId, safeToDelete: bool}]
    - orphanAlerts: [{clientId, secretId, daysUntilExpiry}]
```

### Phase 3: Execute Action Plan (API Calls)

#### 3.1 Process Unique Rotations First

For each secret needing rotation (single KV reference):

```
a. Check: Does app already have a secret with expiry > rotation_days?
   → If YES: Already rotated, just update KV content-type if needed
   → If NO: Continue with rotation

b. Create new secret in Entra ID
   - Description: "Rotated by EntraIDSecretRotator, {date}"
   - Validity: Rotator__ValidityDays (default 180 days / 6 months)

c. Update Key Vault secret:
   - Value: New secret value
   - Expiry: Match Entra ID secret expiry
   - Content-Type: {app-reg-name}:{clientId}:{NEW-secretId}

d. If Key Vault update FAILS:
   - Delete newly created Entra ID secret (rollback)
   - Emit "rotation_failed" metric
   - Continue to next secret

e. If SUCCESS:
   - Emit "rotation_success" metric
```

#### 3.2 Process Duplicate Rotations Second

For secrets mapped in multiple Key Vaults:

```
a. Create new secret in Entra ID (once)

b. Update ALL referencing Key Vaults:
   - Track which updates succeed

c. If ANY Key Vault update fails:
   - Rollback: Revert successful KV updates to previous version
   - Rollback: Delete new Entra ID secret
   - Emit "rotation_failed_duplicate" metric

d. Only mark success when ALL Key Vaults updated
```

#### 3.3 Process Safe Deletions

For expired secrets:

```
a. Determine if safe to delete:
   - KV has previous version with DIFFERENT secretId in content-type
   - This proves we previously rotated this secret

b. If SAFE:
   - Delete expired secret from Entra ID
   - Emit "secret_deleted" metric

c. If NOT SAFE (no proof of previous rotation):
   - Do NOT delete
   - Emit "manual_intervention_required" metric
```

#### 3.4 Emit Orphan Metrics

For secrets NOT mapped in any Key Vault:

```
If expiring within rotation_days OR already expired:
   → Emit "orphan_expiring_secret" metric with labels:
     - app_name
     - client_id
     - secret_id
     - days_until_expiry
```

### Phase 4: Report

- Summary of all actions taken
- Metrics emitted for Prometheus
- Structured logs for debugging

## Safety Guarantees

| Scenario | Protection Mechanism |
|----------|---------------------|
| Delete wrong secret | Only delete if KV version history proves we rotated it |
| Key Vault update fails | Rollback: delete newly created Entra ID secret |
| Partial failure (duplicates) | Rollback: revert ALL changes, restore consistent state |
| App still using old secret | 30-day rotation window provides grace period |
| Unknown secret expiring | Don't touch, emit orphan metric for human review |
| Duplicate KV mappings | Detected in planning phase, handled atomically |

## Metrics

### Success/Failure Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `entraid_rotator_secrets_rotated_total` | Counter | status=success\|failed | Total rotation attempts |
| `entraid_rotator_secrets_deleted_total` | Counter | | Expired secrets deleted |
| `entraid_rotator_job_duration_seconds` | Histogram | | Job execution time |

### Alert Metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `entraid_rotator_orphan_secrets` | Gauge | app_name, client_id, days_until_expiry, pending_deletion | Unmapped secrets needing attention. `pending_deletion=true` means a Key Vault secret's previous version still maps to this secret, so the rotator replaced it and deletes it at expiry. `false` means nothing proves it was replaced (manual app, partial onboarding, or a developer secret): notify only |
| `entraid_rotator_manual_intervention_required` | Gauge | app_name, client_id, secret_id | Secrets that couldn't be auto-handled |
| `entraid_rotator_rotation_failed` | Gauge | app_name, client_id, reason | Failed rotations |
| `entraid_rotator_duplicate_mappings` | Gauge | client_id, secret_id, vault_count | Detected duplicate mappings |

## CLI Commands

| Command | Description |
|---------|-------------|
| `list` | List all app registrations with secrets |
| `expiring` | List secrets expiring within N days |
| `expired` | List already expired secrets |
| `keyvaults` | List all Key Vault secrets with mapping info |
| `orphans` | Find unmapped expiring secrets |
| `rotate` | Execute rotation (respects --dry-run) |
| `cleanup` | Remove expired secrets (with safety checks) |

## Migration: Existing Secrets

Existing Key Vault secrets must be manually tagged with the content-type format before they can be auto-rotated:

1. Identify the app registration the secret belongs to
2. Find the specific secretId in Entra ID
3. Update Key Vault secret content-type: `{app-reg-name}:{clientId}:{secretId}`

Secrets without valid content-type will be reported as "orphans" but not modified.

## Error Handling

### Transient Failures

- Graph write rejections (HTTP 429/503/504, or "Error due to concurrent requests being made to the tenant"):
  retried by `Services/GraphRetry.cs` with exponential backoff (1s, 2s, 4s plus jitter, capped at 30s),
  up to 4 attempts, honouring `Retry-After`. Applies to `addPassword` and `removePassword`.
  Graph rejects back-to-back writes on the same tenant, which may happen when two secrets on the same
  app are rotated 300 ms apart.
- Only explicit rejections are retried. A rejected write was never applied, so a retry cannot create a
  secret twice. Network-level failures on writes are not retried for that reason
- Rate limiting on reads: handled by the Graph SDK's built-in retry handler
- Partial page results: Use PageIterator for complete data

### Permanent Failures

- Permission denied: Log error, emit metric, continue with other secrets
- Invalid content-type format: Skip, emit orphan metric
- App registration not found: Log warning, skip

### Rollback Scenarios

| Failure Point | Rollback Action |
|---------------|-----------------|
| KV update fails after Entra ID create | Delete new Entra ID secret |
| Second KV fails in duplicate scenario | Revert first KV, delete Entra ID secret |
| Deletion fails | Log error, secret remains (safe) |

## Testing Strategy

### Unit Tests

- Model validation (SecretInfo, AppRegistration)
- Configuration priority (env > appsettings > CLI)
- Content-type parsing
- Action plan building logic

### Integration Tests

- Test app registration: `test-secret-rotator`
- Test Key Vault secret with content-type mapping
- Run with `--dry-run` first, then execute
- Verify rotation, KV update, old secret description
