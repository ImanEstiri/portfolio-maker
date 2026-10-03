// ============================================================================
// GENERATED-CV ARTIFACT STORAGE
// Azure Blob Storage for CvMaker.Api's AzureBlobArtifactStorageService
// (Services/Storage/AzureBlobArtifactStorageService.cs), which reads and
// writes generated CV/cover-letter PDFs.
// Container: "generated-cvs" (must match ContainerName in that class exactly).
// ============================================================================

@description('The location used for all deployed resources')
param location string = resourceGroup().location

@description('Tags applied to all resources')
param tags object = {}

@description('Principal ID of the CvMaker service principal (CvMaker.Api reads/writes generated PDFs)')
param principalId string = ''

var resourceToken = uniqueString(resourceGroup().id)
var storageAccountName = 'stcvmaker${take(resourceToken, 14)}'
var containerName = 'generated-cvs'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource generatedCvsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: containerName
  properties: {
    publicAccess: 'None'
  }
}

var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource apiRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(storageAccount.id, principalId, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
  }
}

output storageAccountName string = storageAccount.name
output containerName string = generatedCvsContainer.name
output storageAccountUrl string = 'https://${storageAccount.name}.blob.${environment().suffixes.storage}'
