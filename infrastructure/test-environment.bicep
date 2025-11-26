// FeatherPod Test Environment Infrastructure - Multi-Feed Architecture
// This template creates a test App Service that shares the production storage account
// Uses separate container (featherpod-test) for isolation

@description('Name of the existing storage account to use')
param storageAccountName string = 'featherpod'

@description('Azure region for resources')
param location string = 'swedencentral'

@description('Name of the test container (single container for all test data)')
param testContainerName string = 'featherpod-test'

@description('Name of the test App Service Plan')
param testAppServicePlanName string = 'featherpod-test-plan'

@description('Name of the test App Service (web app)')
param testAppServiceName string = 'featherpod-test'

@description('App Service Plan SKU')
param appServicePlanSku string = 'F1'

@description('.NET runtime version')
param dotnetVersion string = 'DOTNETCORE|10.0'

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
      appCommandLine: 'dotnet FeatherPod.Server.dll'
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
    Azure__ContainerName: testContainerName
    Podcast__BaseUrl: 'https://${testAppServiceName}.azurewebsites.net'
  }
}

// Note: Role assignment must be created manually (see deployment instructions)

// Outputs
output testAppServiceName string = testAppService.name
output testAppServiceUrl string = 'https://${testAppService.properties.defaultHostName}'
output testAppServicePrincipalId string = testAppService.identity.principalId
