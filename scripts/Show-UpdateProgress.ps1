param(
    [Parameter(Mandatory = $true)]
    [string]$StatusPath,

    [Parameter(Mandatory = $true)]
    [int]$ParentProcessId,

    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms

$form = [System.Windows.Forms.Form]::new()
$form.Text = "Updating Glasswork"
$form.Width = 360
$form.Height = 145
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.ControlBox = $false
$form.TopMost = $true

$label = [System.Windows.Forms.Label]::new()
$label.Text = "Preparing Glasswork $Version..."
$label.AutoSize = $true
$label.Left = 16
$label.Top = 24
$form.Controls.Add($label)

$progress = [System.Windows.Forms.ProgressBar]::new()
$progress.Left = 16
$progress.Top = 58
$progress.Width = 312
$progress.Height = 18
$progress.Style = "Marquee"
$progress.MarqueeAnimationSpeed = 30
$form.Controls.Add($progress)

$timer = [System.Windows.Forms.Timer]::new()
$timer.Interval = 100
$timer.Add_Tick({
    if (Test-Path $StatusPath) {
        try {
            $status = (Get-Content $StatusPath -Raw).Trim()
            if ($status -eq "__complete__") {
                $form.Close()
                return
            }
            if (-not [string]::IsNullOrWhiteSpace($status)) {
                $label.Text = $status
            }
        }
        catch [System.IO.IOException] {
            # The updater may be replacing the status file during this tick.
        }
    }

    if (-not (Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue)) {
        $form.Close()
    }
})

$form.Add_Shown({ $timer.Start() })
try {
    [System.Windows.Forms.Application]::Run($form)
}
finally {
    $timer.Stop()
    $timer.Dispose()
    $form.Dispose()
}
