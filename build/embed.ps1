# Встраивает self-contained PCControlAgent.exe в installer.template.ps1.
# → пишет готовый installer.ps1 для ps2exe. Запускается в CI на windows-latest.

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

$agentPath = "$root\publish\PCControlAgent.exe"
if (-not (Test-Path $agentPath)) { throw "Сначала собери agent-dotnet: $agentPath не найден" }
$agentB64 = [System.Convert]::ToBase64String([System.IO.File]::ReadAllBytes($agentPath))

$tpl = Get-Content "$root\installer.template.ps1" -Raw -Encoding UTF8
$tpl = $tpl.Replace("{{AGENT_EXE_B64}}", $agentB64)

# UTF-8 с BOM — чтобы ps2exe корректно прочитал кириллицу в сообщениях
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText("$root\installer.ps1", $tpl, $utf8Bom)

Write-Host "installer.ps1 собран: agent=$($agentB64.Length)b64"
