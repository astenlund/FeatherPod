// FeatherPod Infrastructure - Phase 4: Storage + App Service + Managed Identity + App Settings
// This template creates the Azure Storage Account, blob containers, App Service with managed identity, RBAC, and app settings

@description('Name of the storage account (must be globally unique, 3-24 chars, lowercase alphanumeric)')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Azure region for resources')
param location string = 'swedencentral'

@description('Storage account SKU')
@allowed([
  'Standard_LRS'
  'Standard_GRS'
  'Standard_ZRS'
  'Premium_LRS'
])
param storageSku string = 'Standard_LRS'

@description('Name of the audio container')
param audioContainerName string = 'audio'

@description('Name of the metadata container')
param metadataContainerName string = 'metadata'

@description('Name of the App Service Plan')
param appServicePlanName string

@description('Name of the App Service (web app)')
param appServiceName string

@description('App Service Plan SKU')
@allowed([
  'F1'
  'B1'
  'B2'
  'B3'
  'S1'
  'S2'
  'S3'
  'P1v2'
  'P2v2'
  'P3v2'
])
param appServicePlanSku string = 'F1'

@description('.NET runtime version')
param dotnetVersion string = 'DOTNETCORE:10.0'

// Podcast Configuration
@description('Podcast title')
param podcastTitle string

@description('Podcast description')
param podcastDescription string

@description('Podcast author')
param podcastAuthor string

@description('Podcast email')
param podcastEmail string

@description('Podcast language (e.g., en-us)')
param podcastLanguage string = 'en-us'

@description('Podcast category')
param podcastCategory string

@description('API key for management endpoints (leave empty to set manually later)')
@secure()
param apiKey string = ''

// Storage Account
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: storageSku
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

// Blob Service
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

// Audio Container
resource audioContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: audioContainerName
  properties: {
    publicAccess: 'None'
  }
}

// Metadata Container
resource metadataContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: metadataContainerName
  properties: {
    publicAccess: 'None'
  }
}

// App Service Plan
resource appServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: appServicePlanName
  location: location
  sku: {
    name: appServicePlanSku
    tier: appServicePlanSku == 'F1' ? 'Free' : (appServicePlanSku == 'B1' || appServicePlanSku == 'B2' || appServicePlanSku == 'B3' ? 'Basic' : 'Standard')
  }
  kind: 'linux'
  properties: {
    reserved: true // Required for Linux
  }
}

// App Service (Web App)
resource appService 'Microsoft.Web/sites@2023-01-01' = {
  name: appServiceName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: dotnetVersion
      alwaysOn: appServicePlanSku != 'F1' // AlwaysOn not supported on F1
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
    }
  }
}

// App Settings
resource appServiceSettings 'Microsoft.Web/sites/config@2023-01-01' = {
  parent: appService
  name: 'appsettings'
  properties: {
    Azure__AccountName: storageAccountName
    Azure__AudioContainerName: audioContainerName
    Azure__MetadataContainerName: metadataContainerName
    ApiKey: apiKey
    Podcast__Title: podcastTitle
    Podcast__Description: podcastDescription
    Podcast__Author: podcastAuthor
    Podcast__Email: podcastEmail
    Podcast__Language: podcastLanguage
    Podcast__Category: podcastCategory
    Podcast__BaseUrl: 'https://${appServiceName}.azurewebsites.net'
    Podcast__ImageUrl: 'https://${appServiceName}.azurewebsites.net/icon.png'
  }
}

// Storage Blob Data Contributor role definition (well-known GUID)
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

// Role Assignment: Grant App Service managed identity access to Storage Account
resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, appService.id, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Outputs
output storageAccountId string = storageAccount.id
output storageAccountName string = storageAccount.name
output audioContainerName string = audioContainer.name
output metadataContainerName string = metadataContainer.name
output appServicePlanId string = appServicePlan.id
output appServicePlanName string = appServicePlan.name
output appServiceId string = appService.id
output appServiceName string = appService.name
output appServiceDefaultHostname string = appService.properties.defaultHostName
output appServicePrincipalId string = appService.identity.principalId
output roleAssignmentId string = roleAssignment.id
