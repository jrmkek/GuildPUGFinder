# wcl-debug.ps1
# Direct WCL API testing from the terminal - no app rebuild needed.
#
# Usage:
#   .\wcl-debug.ps1 -Character Scrotul -ZoneId 1060
#   .\wcl-debug.ps1 -Character Scrotul                  # no zone filter (default/latest)
#   .\wcl-debug.ps1 -ListZones                            # dump the raw worldData.zones list

param(
    [string]$Character = "Scrotul",
    [string]$ServerSlug = "spineshatter",
    [string]$ServerRegion = "eu",
    [int]$ZoneId = 0,          # 0 = omit the argument entirely
    [int]$Partition = 0,       # 0 = omit the argument entirely
    [switch]$ListZones,
    [int]$ProbeFrom = 0,       # if set (with -ProbeTo), scan this range instead
    [int]$ProbeTo = 0
)

$ClientId = "01a09165-958a-7300-9160-956ad1b5f99d"
$ClientSecret = "xZUDY07fxHvJTGL5aUQi76Xd3xsTgia4Tt6Yxgpa"
$Site = "fresh.warcraftlogs.com"

# --- Get access token ---
$authBytes = [System.Text.Encoding]::ASCII.GetBytes("$($ClientId):$($ClientSecret)")
$authHeader = [Convert]::ToBase64String($authBytes)

try {
    $tokenResponse = Invoke-RestMethod -Uri "https://$Site/oauth/token" -Method Post `
        -Headers @{ Authorization = "Basic $authHeader" } `
        -Body @{ grant_type = "client_credentials" } -ErrorAction Stop
}
catch {
    Write-Host "TOKEN REQUEST FAILED - stopping here, not sending the actual query." -ForegroundColor Red
    Write-Host "This means ClientId/ClientSecret are wrong, revoked, or don't exist on $Site specifically." -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

$token = $tokenResponse.access_token
Write-Host "Got token." -ForegroundColor Green

$headers = @{ Authorization = "Bearer $token"; "Content-Type" = "application/json" }
$apiUrl = "https://$Site/api/v2/client"

if ($ProbeFrom -ne 0 -and $ProbeTo -ne 0) {
    Write-Host "Probing zoneID $ProbeFrom to $ProbeTo for character '$Character'...`n" -ForegroundColor Cyan
    for ($id = $ProbeFrom; $id -le $ProbeTo; $id++) {
        $query = @"
query GetCharacter(`$name: String!, `$serverSlug: String!, `$serverRegion: String!, `$zoneID: Int!) {
  characterData {
    character(name: `$name, serverSlug: `$serverSlug, serverRegion: `$serverRegion) {
      zoneRankings(zoneID: `$zoneID)
    }
  }
}
"@
        $body = @{
            query = $query
            variables = @{ name = $Character; serverSlug = $ServerSlug; serverRegion = $ServerRegion; zoneID = $id }
        } | ConvertTo-Json -Depth 5

        try {
            $result = Invoke-RestMethod -Uri $apiUrl -Method Post -Headers $headers -Body $body -ErrorAction Stop
            $zr = $result.data.characterData.character.zoneRankings
            if ($null -eq $zr) {
                Write-Host ("{0}: no data" -f $id) -ForegroundColor DarkGray
            }
            elseif ($zr.rankings -and $zr.rankings.Count -gt 0) {
                $firstEncounter = $zr.rankings[0].encounter.name
                $lastEncounter = $zr.rankings[$zr.rankings.Count - 1].encounter.name
                Write-Host ("{0}: bestAvg={1:N1}  encounters: '{2}' .. '{3}' ({4} total)" -f $id, $zr.bestPerformanceAverage, $firstEncounter, $lastEncounter, $zr.rankings.Count) -ForegroundColor Green
            }
            else {
                Write-Host ("{0}: zone exists but no rankings for this character" -f $id) -ForegroundColor Yellow
            }
        }
        catch {
            Write-Host ("{0}: request error - {1}" -f $id, $_.Exception.Message) -ForegroundColor Red
        }
    }
    exit
}

if ($ListZones) {
    $query = @"
query { worldData { zones { id name } } }
"@
    $body = @{ query = $query } | ConvertTo-Json
    $result = Invoke-RestMethod -Uri $apiUrl -Method Post -Headers $headers -Body $body
    $result.data.worldData.zones | Sort-Object id | Format-Table id, name -AutoSize
    exit
}

# --- Build zoneRankings args dynamically based on what was passed ---
$zrArgs = @()
$varDecls = @("`$name: String!", "`$serverSlug: String!", "`$serverRegion: String!")
$variables = @{ name = $Character; serverSlug = $ServerSlug; serverRegion = $ServerRegion }

if ($ZoneId -ne 0) {
    $zrArgs += "zoneID: `$zoneID"
    $varDecls += "`$zoneID: Int!"
    $variables["zoneID"] = $ZoneId
}
if ($Partition -ne 0) {
    $zrArgs += "partition: `$partition"
    $varDecls += "`$partition: Int!"
    $variables["partition"] = $Partition
}

$zrField = if ($zrArgs.Count -gt 0) { "zoneRankings($($zrArgs -join ', '))" } else { "zoneRankings" }
$varDeclStr = $varDecls -join ", "

$query = @"
query GetCharacter($varDeclStr) {
  characterData {
    character(name: `$name, serverSlug: `$serverSlug, serverRegion: `$serverRegion) {
      name
      classID
      $zrField
    }
  }
}
"@

$body = @{ query = $query; variables = $variables } | ConvertTo-Json -Depth 5

Write-Host "`nQuery:" -ForegroundColor Cyan
Write-Host $query
Write-Host "`nVariables:" -ForegroundColor Cyan
Write-Host ($variables | ConvertTo-Json)

Write-Host "`nSending request..." -ForegroundColor Cyan
try {
    $result = Invoke-RestMethod -Uri $apiUrl -Method Post -Headers $headers -Body $body
    Write-Host "`nResponse:" -ForegroundColor Green
    $result | ConvertTo-Json -Depth 10
}
catch {
    Write-Host "`nRequest failed:" -ForegroundColor Red
    $_.Exception.Response.GetResponseStream() | ForEach-Object {
        $reader = New-Object System.IO.StreamReader($_)
        $reader.ReadToEnd()
    }
}