using EntraIDSecretRotator.Infrastructure;
using EntraIDSecretRotator.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Applications.Item.AddPassword;
using Microsoft.Graph.Applications.Item.RemovePassword;
using Microsoft.Extensions.Logging;

namespace EntraIDSecretRotator.Services;

/// <summary>
/// Implementation of IEntraIdService using Microsoft Graph SDK.
/// </summary>
public sealed class EntraIdService : IEntraIdService
{
    private readonly GraphServiceClient _graphClient;
    private readonly ILogger<EntraIdService> _logger;

    public EntraIdService(GraphServiceClient graphClient, ILogger<EntraIdService> logger)
    {
        _graphClient = graphClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AppRegistration>> ListAppRegistrationsAsync(
        string nameFilter,
        CancellationToken cancellationToken = default)
    {
        Assert.NotNullOrEmpty(nameFilter, nameof(nameFilter));

        _logger.LogInformation("Listing app registrations with filter: {Filter}", nameFilter);

        // Graph does not support server-side "contains" filtering on displayName, so every
        // application in the tenant has to be paged through. To keep memory bounded we filter
        // each page as it arrives instead of materialising the whole tenant first, and request
        // the largest page size Graph allows to minimise round trips.
        var filtered = new List<Application>();
        var totalCount = 0;

        try
        {
            var response = await _graphClient.Applications
                .GetAsync(requestConfiguration =>
                {
                    requestConfiguration.QueryParameters.Select = new[]
                    {
                        "id",
                        "appId",
                        "displayName",
                        "passwordCredentials"
                    };
                    requestConfiguration.QueryParameters.Top = GraphMaxPageSize;
                }, cancellationToken);

            Assert.NotNull(response, "response");

            var pageIterator = PageIterator<Application, ApplicationCollectionResponse>
                .CreatePageIterator(
                    _graphClient,
                    response,
                    app =>
                    {
                        totalCount++;
                        if (MatchesFilter(app, nameFilter))
                        {
                            filtered.Add(app);
                        }
                        return true;
                    });

            await pageIterator.IterateAsync(cancellationToken);

            _logger.LogInformation("Retrieved {Count} total app registrations", totalCount);
            _logger.LogInformation("Filtered to {Count} app registrations matching '{Filter}'",
                filtered.Count, nameFilter);

            // Map to our model
            var result = filtered
                .Select(MapToAppRegistration)
                .OrderBy(app => app.DisplayName)
                .ToList();

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list app registrations");
            throw;
        }
    }

    /// <summary>
    /// Maximum page size supported by Graph for the applications collection.
    /// </summary>
    internal const int GraphMaxPageSize = 999;

    /// <summary>
    /// Case-insensitive "contains" match on the application display name.
    /// </summary>
    internal static bool MatchesFilter(Application app, string nameFilter) =>
        app.DisplayName?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) == true;

    private static AppRegistration MapToAppRegistration(Application app)
    {
        Assert.NotNull(app, nameof(app));
        Assert.NotNullOrEmpty(app.Id, "app.Id");
        Assert.NotNullOrEmpty(app.AppId, "app.AppId");
        Assert.NotNullOrEmpty(app.DisplayName, "app.DisplayName");

        var secrets = app.PasswordCredentials?
            .Select(MapToSecretInfo)
            .OrderBy(s => s.EndDateTime)
            .ToList() ?? new List<SecretInfo>();

        return new AppRegistration
        {
            Id = app.Id,
            AppId = app.AppId,
            DisplayName = app.DisplayName,
            Secrets = secrets
        };
    }

    private static SecretInfo MapToSecretInfo(PasswordCredential credential)
    {
        Assert.NotNull(credential, nameof(credential));
        Assert.NotNullOrEmpty(credential.KeyId?.ToString(), "credential.KeyId");
        Assert.That(credential.EndDateTime.HasValue, "credential.EndDateTime must have a value");

        return new SecretInfo
        {
            KeyId = credential.KeyId.ToString()!,
            DisplayName = credential.DisplayName,
            StartDateTime = credential.StartDateTime,
            EndDateTime = credential.EndDateTime!.Value
        };
    }

    /// <inheritdoc />
    public async Task<CreatedSecretInfo> CreateSecretAsync(
        string objectId,
        string displayName,
        int validityDays,
        CancellationToken cancellationToken = default)
    {
        Assert.NotNullOrEmpty(objectId, nameof(objectId));
        Assert.NotNullOrEmpty(displayName, nameof(displayName));
        Assert.That(validityDays > 0, "validityDays must be positive");

        _logger.LogInformation(
            "Creating secret for app {ObjectId} with display name '{DisplayName}' and validity {ValidityDays} days",
            objectId, displayName, validityDays);

        try
        {
            var passwordCredential = new PasswordCredential
            {
                DisplayName = displayName,
                EndDateTime = DateTimeOffset.UtcNow.AddDays(validityDays)
            };

            var requestBody = new AddPasswordPostRequestBody
            {
                PasswordCredential = passwordCredential
            };

            var response = await GraphRetry.ExecuteAsync(
                ct => _graphClient.Applications[objectId]
                    .AddPassword
                    .PostAsync(requestBody, cancellationToken: ct),
                $"addPassword on app {objectId}",
                _logger,
                cancellationToken);

            Assert.NotNull(response, "response");
            Assert.NotNullOrEmpty(response.KeyId?.ToString(), "response.KeyId");
            Assert.NotNullOrEmpty(response.SecretText, "response.SecretText");
            Assert.That(response.StartDateTime.HasValue, "response.StartDateTime must have a value");
            Assert.That(response.EndDateTime.HasValue, "response.EndDateTime must have a value");

            _logger.LogInformation(
                "Successfully created secret {KeyId} for app {ObjectId}",
                response.KeyId, objectId);

            return new CreatedSecretInfo
            {
                KeyId = response.KeyId.ToString()!,
                SecretValue = response.SecretText,
                StartDateTime = response.StartDateTime!.Value,
                EndDateTime = response.EndDateTime!.Value,
                DisplayName = response.DisplayName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create secret for app {ObjectId}", objectId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DeleteSecretAsync(
        string objectId,
        string keyId,
        CancellationToken cancellationToken = default)
    {
        Assert.NotNullOrEmpty(objectId, nameof(objectId));
        Assert.NotNullOrEmpty(keyId, nameof(keyId));

        _logger.LogInformation("Deleting secret {KeyId} from app {ObjectId}", keyId, objectId);

        try
        {
            var requestBody = new RemovePasswordPostRequestBody
            {
                KeyId = Guid.Parse(keyId)
            };

            await GraphRetry.ExecuteAsync(
                ct => _graphClient.Applications[objectId]
                    .RemovePassword
                    .PostAsync(requestBody, cancellationToken: ct),
                $"removePassword {keyId} on app {objectId}",
                _logger,
                cancellationToken);

            _logger.LogInformation(
                "Successfully deleted secret {KeyId} from app {ObjectId}",
                keyId, objectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete secret {KeyId} from app {ObjectId}", keyId, objectId);
            throw;
        }
    }
}
