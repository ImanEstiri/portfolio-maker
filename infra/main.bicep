// ============================================================================
// CVMAKER AZURE RESOURCES
//
// Provisions the Azure resources CvMaker's Fly.io services consume at runtime:
//   1. Azure OpenAI          → CvMaker.Agentic (gpt-4.1, via Azure AI Foundry)
//   2. Azure AI Foundry      → CvMaker.Agentic (Persistent Agents API)
//   3. Generated-CV Storage  → CvMaker.Api (PDF artifacts)
//
// Authentication model: Fly.io has no Managed Identity. Both services
// authenticate via DefaultAzureCredential using AZURE_TENANT_ID + AZURE_CLIENT_ID
// + AZURE_CLIENT_SECRET environment variables (flyio/agentic.fly.toml,
// flyio/api.fly.toml). The service principal identified by principalId
// receives all required RBAC roles on the provisioned resources.
//
// Deployed by: .github/workflows/flyio.yml (provision-azure job), on every
// v* tag push. Idempotent — re-running the workflow updates resources in place.
// ============================================================================

targetScope = 'subscription'

@description('Short label used in the resource group name: rg-<environmentName>.')
param environmentName string = 'cvmaker-flyio-dev'

@description('Azure region for all resources, e.g. "eastus2" or "westeurope".')
param location string

@description('''
Object ID of the Azure Service Principal cv-maker's Fly.io services authenticate
as via DefaultAzureCredential.
  CvMaker.Agentic → Cognitive Services OpenAI User + Contributor, Azure AI Developer
  CvMaker.Api     → Storage Blob Data Contributor on the generated-cvs container
Run: az ad sp show --id <AZURE_CLIENT_ID> --query id -o tsv
''')
param principalId string

@description('Restore the Azure OpenAI account when a soft-deleted resource with the same name already exists.')
param restoreOpenAi bool = false

@description('Existing Azure OpenAI custom subdomain name to preserve when redeploying an existing resource.')
param openAiCustomSubDomainName string = ''

var tags = {
  environment: environmentName
  'managed-by': 'flyio-cicd'
}

resource rg 'Microsoft.Resources/resourceGroups@2022-09-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

// ── 1. Azure OpenAI ──────────────────────────────────────────────────────────
module openai 'openai/openai.module.bicep' = {
  name: 'openai'
  scope: rg
  params: {
    location: location
    principalId: principalId
    restore: restoreOpenAi
    customSubDomainName: openAiCustomSubDomainName
  }
}

// ── 2. Azure AI Foundry Hub + Project ────────────────────────────────────────
// Reuses the OpenAI resource above (no duplicated model deployment).
module azureAiFoundry 'azure-ai-foundry/azure-ai-foundry.module.bicep' = {
  name: 'azure-ai-foundry'
  scope: rg
  params: {
    location: location
    principalId: principalId
    openAiEndpoint: openai.outputs.endpoint
    openAiResourceId: openai.outputs.id
  }
}

// ── 3. Generated-CV Artifact Storage ─────────────────────────────────────────
module artifactsStorage 'artifacts-storage/artifacts-storage.module.bicep' = {
  name: 'artifacts-storage'
  scope: rg
  params: {
    location: location
    tags: tags
    principalId: principalId
  }
}

output RESOURCE_GROUP_NAME string = rg.name

// Azure OpenAI (used by CvMaker.Agentic through the Foundry connection)
output AZURE_OPENAI_ENDPOINT string = openai.outputs.endpoint
output AZURE_OPENAI_NAME string = openai.outputs.name
output AZURE_OPENAI_DEPLOYMENT_NAME string = openai.outputs.deploymentName

// Azure AI Foundry — set as AzureAIFoundry:Endpoint (flyio/agentic.fly.toml)
output AZURE_AI_FOUNDRY_ENDPOINT string = azureAiFoundry.outputs.projectEndpoint
output AZURE_AI_FOUNDRY_PROJECT_NAME string = azureAiFoundry.outputs.projectName

// Generated-CV storage — set as ArtifactsStorage:StorageAccountUrl (flyio/api.fly.toml)
output ARTIFACTS_STORAGE_ACCOUNT_URL string = artifactsStorage.outputs.storageAccountUrl
output ARTIFACTS_STORAGE_ACCOUNT_NAME string = artifactsStorage.outputs.storageAccountName
