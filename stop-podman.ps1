$ErrorActionPreference = "Stop"

podman rm -f misa-functions 2>$null | Out-Null
podman rm -f misa-mcpinvokehost 2>$null | Out-Null
podman rm -f misa-mcp-host 2>$null | Out-Null
podman rm -f misa-azurite 2>$null | Out-Null
