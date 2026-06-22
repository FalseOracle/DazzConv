$keepLanguages = @("ru", "en", "en-US", "en-GB")
$publishDir = ".\publish"

Write-Host "🧹 Удаляю лишние языки (оставляю: $($keepLanguages -join ', '))..." -ForegroundColor Yellow

$sizeBefore = (Get-ChildItem -Path $publishDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB

Get-ChildItem -Path $publishDir -Directory -Recurse | Where-Object {
    $_.Name -match '^[a-z]{2}(-[A-Z]{2})?$' -and
    $keepLanguages -notcontains $_.Name -and
    (Test-Path (Join-Path $_.FullName "*.dll"))
} | ForEach-Object {
    Write-Host "  Удаляю: $($_.Name)" -ForegroundColor Gray
    Remove-Item -Path $_.FullName -Recurse -Force
}

$sizeAfter = (Get-ChildItem -Path $publishDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
$saved = $sizeBefore - $sizeAfter

Write-Host "`n✅ Готово!" -ForegroundColor Green
Write-Host "📦 До: $([math]::Round($sizeBefore, 2)) MB → После: $([math]::Round($sizeAfter, 2)) MB" -ForegroundColor Cyan
Write-Host "💾 Сэкономлено: $([math]::Round($saved, 2)) MB" -ForegroundColor Yellow