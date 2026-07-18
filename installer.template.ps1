# ============================================================
#  190x4 PC Control — единый установщик v2
#  Внутри находится self-contained Windows-агент без NirCmd.
# ============================================================

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms | Out-Null
Add-Type -AssemblyName Microsoft.VisualBasic | Out-Null

function Show-Info($msg) { [System.Windows.Forms.MessageBox]::Show($msg, "190x4 PC Control", 'OK', 'Information') | Out-Null }
function Show-Err($msg) { [System.Windows.Forms.MessageBox]::Show($msg, "190x4 PC Control — ошибка", 'OK', 'Error') | Out-Null }

$userId = ""
while ($true) {
    $userId = [Microsoft.VisualBasic.Interaction]::InputBox(
        "Введи свой Telegram ID.`n`nУзнать: открой бота и отправь /myid.`nПосле запуска бот попросит подтвердить подключение этого ПК.",
        "190x4 PC Control — установка", "")
    if ($null -eq $userId -or $userId.Trim() -eq "") {
        Show-Info "Установка отменена."
        exit 0
    }
    $userId = $userId.Trim()
    if ($userId -match '^\d{5,15}$') { break }
    Show-Err "ID должен состоять только из цифр (5–15 знаков)."
}

try {
    $sharedDir = Join-Path ([Environment]::GetFolderPath("CommonDocuments")) "190x4\PCControl"
    New-Item -ItemType Directory -Path $sharedDir -Force | Out-Null
    icacls $sharedDir /inheritance:e /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Не удалось подготовить общий каталог конфигурации." }

    $installDir = Join-Path $env:LOCALAPPDATA "190x4\PCControl"
    $agentPath = Join-Path $installDir "PCControlAgent.exe"
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null

    $agentB64 = "{{AGENT_EXE_B64}}"
    $tempAgent = Join-Path $env:TEMP "PCControlAgent.new.exe"
    [System.IO.File]::WriteAllBytes($tempAgent, [System.Convert]::FromBase64String($agentB64))

    Get-Process PCControlAgent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Move-Item $tempAgent $agentPath -Force

    schtasks /delete /tn "PC Control Bot" /f 2>$null | Out-Null
    schtasks /delete /tn "190x4 PC Control" /f 2>$null | Out-Null
    $taskCommand = '"' + $agentPath + '" --user-id ' + $userId
    schtasks /create /tn "190x4 PC Control" /tr $taskCommand /sc ONLOGON /rl LIMITED /it /f | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Не удалось создать задачу автозапуска." }

    Start-Process $agentPath -ArgumentList "--user-id", $userId
    Show-Info "Готово! ✅`n`nАгент установлен и запущен.`n`nСейчас открой Telegram: бот пришлёт запрос подключения этого ПК. Сверь код и нажми «Подключить»."
}
catch {
    Show-Err "Установка не завершена:`n`n$($_.Exception.Message)"
    exit 1
}
