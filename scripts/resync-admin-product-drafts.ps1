param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$BearerToken,

    [Parameter(Mandatory = $true)]
    [string[]]$Skus
)

$normalizedBaseUrl = $BaseUrl.TrimEnd('/')
$headers = @{
    Authorization = "Bearer $BearerToken"
    "Content-Type" = "application/json"
}

foreach ($rawSku in $Skus) {
    $sku = ($rawSku ?? "").Trim().ToUpperInvariant()
    if ([string]::IsNullOrWhiteSpace($sku)) {
        continue
    }

    $uri = "$normalizedBaseUrl/api/v1/admin/products/$([uri]::EscapeDataString($sku))"
    $body = @{ reason = "batch_draft_sync" } | ConvertTo-Json

    try {
        $response = Invoke-RestMethod -Method Put -Uri $uri -Headers $headers -Body $body
        Write-Host "[OK] $sku synced" -ForegroundColor Green
    }
    catch {
        $message = $_.Exception.Message
        Write-Host "[FAIL] $sku -> $message" -ForegroundColor Red
    }
}

