@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

@description('Principal ID of the CvMaker service principal (Fly.io DefaultAzureCredential) to grant OpenAI roles.')
param principalId string

@description('Restore the Azure OpenAI account if a soft-deleted resource with the same name already exists.')
param restore bool = false

@description('Custom subdomain name for the Azure OpenAI account. When empty, the module computes the default value from the resource group id.')
param customSubDomainName string = ''

var effectiveCustomSubDomainName = empty(customSubDomainName)
  ? take('cvmaker-openai-${uniqueString(resourceGroup().id)}', 64)
  : customSubDomainName

// ============================================================================
// AZURE OPENAI SERVICE
// Backs the Azure AI Foundry hub/project with the single gpt-4.1 deployment
// every CvMaker.Agentic agent definition (src/CvMaker.Agentic/agents/*/definition.json)
// is written against.
// ============================================================================

resource openai 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: take('openai-${uniqueString(resourceGroup().id)}', 64)
  location: location
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: union({
    customSubDomainName: effectiveCustomSubDomainName
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
    }
  }, restore ? {
    restore: true
  } : {})
}

resource gpt41Deployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openai
  name: 'gpt-4.1'
  sku: {
    name: 'GlobalStandard'
    capacity: 50 // 50K tokens per minute
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4.1'
      version: '2025-04-14'
    }
  }
}

// Grant "Cognitive Services OpenAI User" — chat completion / inference data-plane.
// Role ID: 5e0bd9bd-7b93-4f28-af87-19fc36ad61bd
resource openaiUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openai.id, principalId, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  scope: openai
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
    )
  }
}

// Grant "Cognitive Services OpenAI Contributor" — Agents API data-plane actions
// (create/run/delete agents, threads) performed by FoundryAgentRuntime.
// Role ID: a001fd3d-188f-4b5d-821b-7da978bf7442
resource openaiContributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openai.id, principalId, 'a001fd3d-188f-4b5d-821b-7da978bf7442')
  scope: openai
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'a001fd3d-188f-4b5d-821b-7da978bf7442'
    )
  }
}

@description('The endpoint URL of the Azure OpenAI resource.')
output endpoint string = openai.properties.endpoint

@description('The name of the Azure OpenAI resource.')
output name string = openai.name

@description('The resource ID of the Azure OpenAI resource.')
output id string = openai.id

@description('The deployment name of the gpt-4.1 model.')
output deploymentName string = gpt41Deployment.name
