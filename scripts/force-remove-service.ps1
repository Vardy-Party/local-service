<#
Force-remove a corrupted Windows service entry for VardyPartyLocalService.
Run this script in a TRUE elevated PowerShell (Run as Administrator).
It attempts multiple removal techniques in order:
  1. Stop service (Stop-Service)
  2. sc.exe delete
  3. WMI Delete (Win32_Service.Delete())
  4. Registry key removal (HKLM:\SYSTEM\CurrentControlSet\Services\...)
  5. Kill any leftover processes and remove install directory

Use with caution: registry deletion is destructive for the service entry.
#>

param(
	[string]$ServiceName = 'VardyPartyLocalService',
	[string]$InstallDirectory = 'C:\Program Files\VardyParty\LocalService'
)

function AbortIfNotElevated {
	$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
	if (-not $isAdmin) {
		Write-Host "ERROR: This script must be run from an elevated PowerShell session (Run as Administrator)." -ForegroundColor Red
		exit 1
	}
}

function ServiceExists {
	return (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -ne $null
}

AbortIfNotElevated

Write-Host "Force-remove service: $ServiceName" -ForegroundColor Cyan

# Step 1: Stop service if present
if (ServiceExists) {
	try {
		$svc = Get-Service -Name $ServiceName -ErrorAction Stop
		if ($svc.Status -ne 'Stopped') {
			Write-Host "Stopping service..." -ForegroundColor Yellow
			Stop-Service -Name $ServiceName -Force -ErrorAction Stop
			Start-Sleep -Seconds 2
		}
	}
	catch {
		Write-Host "Warning: Stop-Service failed or service not found: $_" -ForegroundColor Yellow
	}
}
else {
	Write-Host "Service not present via Get-Service." -ForegroundColor Gray
}

# Step 2: Try sc.exe delete
Write-Host "Trying sc.exe delete..." -ForegroundColor Yellow
try {
	$out = & sc.exe delete $ServiceName 2>&1
	Write-Host $out
}
catch {
	Write-Host "sc.exe delete failed: $_" -ForegroundColor Yellow
}
Start-Sleep -Seconds 2

if (-not (ServiceExists)) {
	Write-Host "Service removed by sc.exe." -ForegroundColor Green
	goto Cleanup
}

# Step 3: Try WMI Delete
Write-Host "Trying WMI Delete (Win32_Service.Delete)..." -ForegroundColor Yellow
try {
	$wmi = Get-WmiObject -Class Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
	if ($wmi -and $wmi.__PATH) {
		$result = $wmi.Delete()
		Write-Host "WMI Delete return value: $($result.ReturnValue)"
		if ($result.ReturnValue -eq 0) {
			Write-Host "WMI Delete succeeded." -ForegroundColor Green
			Start-Sleep -Seconds 1
			if (-not (ServiceExists)) { goto Cleanup }
		}
		else { Write-Host "WMI Delete returned non-zero. Continuing..." -ForegroundColor Yellow }
	}
	else {
		Write-Host "WMI: no live service object found (object may be deserialized)." -ForegroundColor Yellow
	}
}
catch {
	Write-Host "WMI Delete failed: $_" -ForegroundColor Yellow
}

# Step 4: Remove registry key
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
if (Test-Path $regPath) {
	try {
		Write-Host "Removing registry key: $regPath" -ForegroundColor Yellow
		Remove-Item -Path $regPath -Recurse -Force -ErrorAction Stop
		Write-Host "Registry key removed." -ForegroundColor Green
		Start-Sleep -Seconds 1
	}
	catch {
		Write-Host "Failed to remove registry key: $_" -ForegroundColor Red
	}
}
else {
	Write-Host "Registry key not found: $regPath" -ForegroundColor Gray
}

# Extra: Try removing service via Win32_Service class method from the class object (alternative)
try {
	$svcClass = [WMIClass]"\\.\root\cimv2:Win32_Service"
	$del = $svcClass.Delete($ServiceName)
	Write-Host "Win32_Service.Delete returned: $del" -ForegroundColor Yellow
}
catch {
	Write-Host "Win32_Service.Delete attempt failed or not supported: $_" -ForegroundColor Yellow
}

function RunAsSystemFallback {
	param(
		[string]$taskName = "RemoveVardyServiceTemp",
		[string]$svc = $ServiceName,
		[string]$installDir = $InstallDirectory
	)

	Write-Host "Attempting SYSTEM scheduled-task fallback..." -ForegroundColor Yellow
	$didSomething = $false
	try {
		$cmd = 'cmd.exe'
		$reg = "HKLM\\SYSTEM\\CurrentControlSet\\Services\\$svc"
		$args = "/c sc.exe delete $svc & reg delete `"$reg`" /f 2>nul & rmdir `"$installDir`" /s /q 2>nul"
		$action = New-ScheduledTaskAction -Execute $cmd -Argument $args
		$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(8)

		Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -RunLevel Highest -User "SYSTEM" -Force | Out-Null
		Start-ScheduledTask -TaskName $taskName
		Write-Host "Scheduled task started; waiting for completion..." -ForegroundColor Yellow
		Start-Sleep -Seconds 10
		Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
		Write-Host "SYSTEM scheduled-task fallback executed." -ForegroundColor Green
		$didSomething = $true
	}
	catch {
		Write-Host "SYSTEM scheduled-task fallback failed: $_" -ForegroundColor Yellow
	}

	# If still exists, try PsExec (if available)
	if (ServiceExists) {
		Write-Host "Service still exists after scheduled-task fallback. Attempting PsExec fallback..." -ForegroundColor Yellow
		# Find psexec in common locations or in PATH
		$possible = @()
		if (Test-Path "$env:USERPROFILE\Downloads\PSTools\psexec.exe") { $possible += "$env:USERPROFILE\Downloads\PSTools\psexec.exe" }
		if (Test-Path "C:\\Tools\\Sysinternals\\psexec.exe") { $possible += "C:\\Tools\\Sysinternals\\psexec.exe" }
		if (Test-Path "C:\\Sysinternals\\psexec.exe") { $possible += "C:\\Sysinternals\\psexec.exe" }
		$cmdInPath = Get-Command psexec.exe -ErrorAction SilentlyContinue
		if ($cmdInPath) { $possible += $cmdInPath.Source }

		$found = $possible | Where-Object { Test-Path $_ } | Select-Object -First 1
		if ($found) {
			Write-Host "Found PsExec at: $found" -ForegroundColor Green
			try {
				# Run PsExec as SYSTEM to sdshow, sdset (permissive) and delete service + registry
				$svcTemp = $svc
				$sddl = 'D:(A;;FA;;;SY)(A;;FA;;;BA)'
				# Build command string carefully to avoid parser issues
				$psCmd = 'sc.exe sdshow ' + $svcTemp + ' > %SystemRoot%\\Temp\\VardySvcSd.txt 2>nul'
				$psCmd += ' & sc.exe sdset ' + $svcTemp + ' "' + $sddl + '" 2>nul'
				$psCmd += ' & sc.exe delete ' + $svcTemp + ' 2>nul'
				$psCmd += ' & reg delete "HKLM\\SYSTEM\\CurrentControlSet\\Services\\' + $svcTemp + '" /f 2>nul'
				$psArgsArray = @(
					'-accepteula',
					'-s',
					'-i',
					'cmd.exe',
					'/c',
					$psCmd
				)
				Write-Host "Running PsExec to sdshow, sdset (permissive) and delete service..." -ForegroundColor Yellow
				Start-Process -FilePath $found -ArgumentList $psArgsArray -NoNewWindow -Wait
				$didSomething = $true
				Write-Host "PsExec sdset+delete fallback executed." -ForegroundColor Green

				# If PsExec ran but service still exists, open an interactive SYSTEM cmd for inspection
				Start-Sleep -Seconds 2
				if (ServiceExists) {
					Write-Host "Service still exists after PsExec attempt. Opening interactive SYSTEM command window for diagnostics..." -ForegroundColor Yellow
					try {
						Start-Process -FilePath $found -ArgumentList '-accepteula','-s','-i','cmd.exe' -NoNewWindow:$false
						Write-Host "Interactive SYSTEM cmd started. Run: sc.exe sdshow VardyPartyLocalService ; sc.exe sdshow VardyPartyLocalService > %SystemRoot%\\Temp\\VardySvcSd.txt ; sc.exe delete VardyPartyLocalService ; reg query \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\VardyPartyLocalService\"" -ForegroundColor Cyan
					}
					catch {
						Write-Host "Failed to launch interactive SYSTEM cmd: $_" -ForegroundColor Red
					}
				}
			}
			catch {
				Write-Host "PsExec execution failed: $_" -ForegroundColor Red
			}
		}
		else {
			Write-Host "PsExec not found on the system (looked in common locations and PATH)." -ForegroundColor Yellow
		}
	}

	if (-not $didSomething) {
		Write-Host "No SYSTEM-level removal method succeeded." -ForegroundColor Red
	}
}

function Cleanup {
	# Kill any running processes that look like the service exe
	try {
		$matches = Get-Process | Where-Object { $_.Path -and $_.Path -like "*VardyParty.LocalService*" } -ErrorAction SilentlyContinue
		if ($matches) {
			Write-Host "Killing service processes..." -ForegroundColor Yellow
			foreach ($p in $matches) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
		}
		else { Write-Host "No running service processes found." -ForegroundColor Gray }
	}
	catch {
		Write-Host "Process cleanup failed: $_" -ForegroundColor Yellow
	}

	# Remove installation directory
	if (Test-Path $InstallDirectory) {
		try {
			Write-Host "Removing install directory: $InstallDirectory" -ForegroundColor Yellow
			Remove-Item -Path $InstallDirectory -Recurse -Force -ErrorAction Stop
			Write-Host "Install directory removed." -ForegroundColor Green
		}
		catch {
			Write-Host "Failed to remove install directory: $_" -ForegroundColor Yellow
		}
	}
	else {
		Write-Host "Install directory not present: $InstallDirectory" -ForegroundColor Gray
	}

	# Final check
	if (-not (ServiceExists)) {
		Write-Host "Service '$ServiceName' is no longer present." -ForegroundColor Green
	}
	else {
		Write-Host "Service '$ServiceName' still exists. Attempting SYSTEM fallback..." -ForegroundColor Yellow
		RunAsSystemFallback
		Start-Sleep -Seconds 3
		if (-not (ServiceExists)) {
			Write-Host "Service removed by SYSTEM fallback." -ForegroundColor Green
		}
		else {
			Write-Host "Service still exists after SYSTEM fallback. A manual PsExec or Safe Mode removal may be required." -ForegroundColor Red
		}
	}

	Write-Host "Done." -ForegroundColor Cyan
}

# Invoke cleanup at the end of the script
Cleanup
