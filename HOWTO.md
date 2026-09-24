# Automate secret rotation with EntraID-Secret-Rotator

## **tldr;**

This app is created to run as a CronJob in Kubernetes. It scans for expiring Entra ID _App registration_ secrets that are mapped to Azure Key Vaults.

Any Key Vault secret with the correct description matching a unique _App registration_ secret, will rotate the _App registration_ secret and subsequently update all matching Key Vault secrets in any connected Key Vaults. Provided it has the necessary access rights.

Any expiring _App registration_ secret *not* matching a Key Vault secret listed, but matching the filter, can be displayed in dashboards or alerts created from the metrics output.

## **How to link _App registration_ and Azure Key Vault secrets**

Three conditions must be met for the EntraID Secret Rotator to automatically rotate _App registration_ secrets:

* The _App registration_ must contain a case insensitive string, like `myfilter-`
* One of the _App registration_ owners must be the Managed Identity: `<your-mi-name-here>`
* The mapping Azure Key Vault secret(s) must have a *Content type (optional)* as follows: `<appRegName>:<appRegClientID>:<secretID>`

Then, regardless of the set expiry date (or lack thereof) on the Azure Key Vault secret, the EntraID-Secret-Rotator job will rotate the _App registration_ secret 30 days before its expiry, and update all corresponding Azure Key Vault secrets that match the same _Content type_ with the rotated secret value.

### Set the managed identity's Entra ID Enterprise Application permission

First, and this may take some time, you ***have*** to give the Enterprise Application of the Managed Identity the `Application.ReadWrite.OwnedBy` permission. This is a Microsoft Graph application permission, and it needs **Admin consent** - which you may not have the rights to grant. Reach out to someone who can grant this.

### **Set the managed identity as owner of Application Registration**

You need to be an owner of the _App registration_, and you need to find the `Object ID` of the same _App registration_. Then it should be as easy as:

```bash
appRegObjectID="<app-registration-object-id-goes-here>"
managedIdentityObjectId="<managed-identity-object-id-goes-here>"

# This Azure CLI command will set the owner
az ad app owner add --id "$appRegObjectId" --owner-object-id "$managedIdentityObjectId"
```

### **Link the secret(s):**

Identify which Azure Key Vault(s) and secrets are connected to the _App registration_. Find the *name* of the _App registration_ and the *Application (client) ID* from the Overview. Find the *Secret ID* you want to connect from the *Certificates & secrets* menu.

Find and enter the secret in Azure Key Vault. Go the **Current Version** of that secret (the one that matches the _App registration_ secret).

Enter the necessary information as follows in *Content type (optional)*, ensure that you use `:` as divider: `AppRegName:AppRegClientID:SecretID`.

As an example:

| **AppRegName** | myfilter-ballalaika-orchestra |
|------------|--------------------------|
| **AppRegClientID** | 01234567-0123-4567-8901-012345678912 |
| **SecretID** | ab123456-0fd6-4892-87df-987654321021 |

Will lead to a *Content type (optional)* like this:

`myfilter-ballalaika-orchestra:01234567-0123-4567-8901-012345678912:ab123456-0fd6-4892-87df-987654321021`

Press **Apply** to apply the changes

### Stakater Reloader

The final piece of the puzzle: [Stakater/Reloader](https://github.com/stakater/reloader).

This app can be rolled out using a Helm chart. Follow the Stakater/Reloader README.md file to enable automatic rollout restart when Kubernetes `Secret` object hash value change. Or `ConfigMap`, for that matter.

## **What’s next?**

Hopefully nothing. The secret should now be automatically rotated 30 days before expiry. All Key Vault secrets connected to the AppReg/SecretID should also be updated. Within two minutes, the `SecretProviderClass` in Kubernetes will rotate the secret(s) in any connected clusters, and once the Kubernetes `Secret` object has been changed, *Stakater Reloader* will restart all deployments connected to the rotated `Secret` object.

No hands needed.

### **But just in case ….**

Create a dashboard and alerts that will sound if any secret is about to expire.
