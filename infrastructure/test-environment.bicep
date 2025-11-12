// FeatherPod Test Environment Infrastructure
// This template creates a test App Service that shares the production storage account
// Uses separate containers (audio-test, metadata-test) for isolation

@description('Name of the existing storage account to use')
param storageAccountName string = 'featherpod'

@description('Azure region for resources')
param location string = 'swedencentral'

@description('Name of the test audio container')
param testAudioContainerName string = 'audio-test'

@description('Name of the test metadata container')
param testMetadataContainerName string = 'metadata-test'

@description('Name of the test App Service Plan')
param testAppServicePlanName string = 'featherpod-test-plan'

@description('Name of the test App Service (web app)')
param testAppServiceName string = 'featherpod-test'

@description('App Service Plan SKU')
param appServicePlanSku string = 'F1'

@description('.NET runtime version')
param dotnetVersion string = 'DOTNETCORE|9.0'

@description('API key for test environment (leave empty to set via GitHub secret)')
@secure()
param apiKey string = ''

@description('Podcast title for test environment')
param podcastTitle string = 'FeatherPod Test'

@description('Podcast description')
param podcastDescription string = 'Test environment for FeatherPod'

@description('Podcast author')
param podcastAuthor string = 'Test Author'

@description('Podcast email')
param podcastEmail string = 'test@example.com'

@description('Podcast language')
param podcastLanguage string = 'en-us'

@description('Podcast category')
param podcastCategory string = 'Technology'

// Note: Blob containers and role assignment must be created separately
// due to cross-resource-group scope limitations.
// See deployment instructions for manual setup steps.

// Test App Service Plan
resource testAppServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: testAppServicePlanName
  location: location
  sku: {
    name: appServicePlanSku
    tier: 'Free'
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

// Test App Service
resource testAppService 'Microsoft.Web/sites@2023-01-01' = {
  name: testAppServiceName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: testAppServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: dotnetVersion
      alwaysOn: false // F1 doesn't support AlwaysOn
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
    }
  }
}

// Test App Settings
resource testAppServiceSettings 'Microsoft.Web/sites/config@2023-01-01' = {
  parent: testAppService
  name: 'appsettings'
  properties: {
    Azure__AccountName: storageAccountName
    Azure__AudioContainerName: testAudioContainerName
    Azure__MetadataContainerName: testMetadataContainerName
    ApiKey: apiKey
    Podcast__Title: podcastTitle
    Podcast__Description: podcastDescription
    Podcast__Author: podcastAuthor
    Podcast__Email: podcastEmail
    Podcast__Language: podcastLanguage
    Podcast__Category: podcastCategory
    Podcast__BaseUrl: 'https://${testAppServiceName}.azurewebsites.net'
    Podcast__ImageUrl: 'https://${testAppServiceName}.azurewebsites.net/icon.png'
  }
}

// Note: Role assignment must be created manually (see deployment instructions)

// Outputs
output testAppServiceName string = testAppService.name
output testAppServiceUrl string = 'https://${testAppService.properties.defaultHostName}'
output testAppServicePrincipalId string = testAppService.identity.principalId
