$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    podman network exists misa-shared
    if ($LASTEXITCODE -ne 0) {
        podman network create misa-shared | Out-Null
    }

    podman build -t misa-agentic-mcp-host:local -f Dockerfile .
    podman build -t misa-agentic-functions:local -f Dockerfile.functions .

    # Clean up legacy container names from earlier manual runs.
    podman rm -f misa-mcp-host 2>$null | Out-Null

    podman rm -f misa-mcpinvokehost 2>$null | Out-Null
    podman rm -f misa-functions 2>$null | Out-Null
    podman rm -f misa-azurite 2>$null | Out-Null

    podman run -d --name misa-azurite --network misa-shared -e AZURITE_ACCOUNTS=misaaccount:MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY= -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite azurite --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0

    podman run -d --name misa-mcpinvokehost --network misa-shared -p 19082:19082 -e MCP_LISTEN_URL=http://0.0.0.0:19082 misa-agentic-mcp-host:local

    podman run -d --name misa-functions --network misa-shared -p 7071:80 -e FUNCTIONS_WORKER_RUNTIME=dotnet-isolated -e AzureWebJobsStorage="DefaultEndpointsProtocol=http;AccountName=misaaccount;AccountKey=MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=;BlobEndpoint=http://misa-azurite:10000/misaaccount;QueueEndpoint=http://misa-azurite:10001/misaaccount;TableEndpoint=http://misa-azurite:10002/misaaccount;" -e Misa__Mcp__BaseUrl=http://misa-mcpinvokehost:19082/mcp -e Misa__Mcp__Enabled=true -e Misa__Mcp__Knowledge__Enabled=true -e Misa__Mcp__Decisioning__Enabled=true -e Misa__Mcp__Reasoning__Enabled=true -e Misa__Mcp__Clarification__Enabled=true misa-agentic-functions:local
}
finally {
    Pop-Location
}

$vmIp = ""
try {
    $machines = podman machine list --format json | ConvertFrom-Json
    $activeMachine = $machines | Where-Object { $_.Running -eq $true } | Select-Object -First 1
    if (-not $activeMachine) {
        $activeMachine = $machines | Select-Object -First 1
    }

    $ipOutput = podman machine ssh $activeMachine.Name "ip -4 addr show eth0"
    $match = [regex]::Match(($ipOutput | Out-String), 'inet\s+([0-9.]+)/')
    if ($match.Success) {
        $vmIp = $match.Groups[1].Value
    }
}
catch {
    $vmIp = ""
}

if ([string]::IsNullOrWhiteSpace($vmIp)) {
    Write-Host "MISA_Agentic stack started. Query VM IP with: podman machine ssh \"ip -4 addr show eth0\""
}
else {
    Write-Host "MCP Host URL: http://${vmIp}:19082/mcp/health"
    Write-Host "Functions URL: http://${vmIp}:7071/api/irt/health"
}
