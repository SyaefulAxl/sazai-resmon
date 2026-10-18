# =====================================================================
#  make-icons.ps1 - Deterministic Brand Asset Generator for Resource Monitoring
#  Generates: rm-icon-16..512.png, rm-avatar.png, preview.png
#  Palette extracted directly from Dashboard.cs
# =====================================================================
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$OutDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Exact palette from Dashboard.cs
$CBackMain   = [System.Drawing.Color]::FromArgb(255, 24, 24, 34)    # #181822 Form BackColor
$CBackInner  = [System.Drawing.Color]::FromArgb(255, 32, 34, 48)    # #202230 GraphPanel BackColor
$CCardFill   = [System.Drawing.Color]::FromArgb(255, 44, 47, 62)    # #2C2F3E CardPanel FillColor
$CCardBorder = [System.Drawing.Color]::FromArgb(255, 72, 78, 100)   # #484E64 CardPanel BorderColor
$CText       = [System.Drawing.Color]::FromArgb(255, 255, 255, 255) # #FFFFFF CText
$CSub        = [System.Drawing.Color]::FromArgb(255, 188, 194, 214) # #BCC2D6 CSub
$CDim        = [System.Drawing.Color]::FromArgb(255, 160, 166, 190) # #A0A6BE CDim
$CAccent     = [System.Drawing.Color]::FromArgb(255, 110, 190, 255) # #6EBEFF CAccent
$CGood       = [System.Drawing.Color]::FromArgb(255, 115, 222, 160) # #73DEA0 CGood
$CWarn       = [System.Drawing.Color]::FromArgb(255, 255, 176, 92)  # #FFB05C CWarn
$CBad        = [System.Drawing.Color]::FromArgb(255, 255, 118, 118) # #FF7676 CBad

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0.5) {
        $path.AddRectangle([System.Drawing.RectangleF]::new($x, $y, $w, $h))
        return $path
    }
    $d = [Math]::Min($r * 2.0, [Math]::Min($w, $h))
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-RmIcon([System.Drawing.Graphics]$g, [float]$ox, [float]$oy, [int]$size, [bool]$isAvatar) {
    $s = $size / 512.0
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    if ($isAvatar) {
        # Full-bleed dark square for GitHub repository avatar
        $bBg = New-Object System.Drawing.SolidBrush($CBackMain)
        $g.FillRectangle($bBg, $ox, $oy, $size, $size)
        $bBg.Dispose()

        $bPlate = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150, 44, 47, 62))
        $g.FillEllipse($bPlate, $ox + 68*$s, $oy + 60*$s, 376*$s, 376*$s)
        $bPlate.Dispose()
    } else {
        # Rounded-square app icon (~22% corner radius)
        $pad = if ($size -le 24) { 12.0 * $s } else { 16.0 * $s }
        $bw = $size - 2.0 * $pad
        $rad = $bw * 0.222

        $pOuter = New-RoundedRectPath ($ox + $pad) ($oy + $pad) $bw $bw $rad
        $bOuter = New-Object System.Drawing.SolidBrush($CBackMain)
        $g.FillPath($bOuter, $pOuter)
        $bOuter.Dispose()
        $pOuter.Dispose()

        if ($size -ge 32) {
            $inPad = 28.0 * $s
            $inW = $size - 2.0 * $inPad
            $inRad = $inW * 0.21
            $pInner = New-RoundedRectPath ($ox + $inPad) ($oy + $inPad) $inW $inW $inRad
            $bInner = New-Object System.Drawing.SolidBrush($CBackInner)
            $g.FillPath($bInner, $pInner)
            $penBorder = New-Object System.Drawing.Pen($CCardBorder, [Math]::Max(1.0, 6.0 * $s))
            $g.DrawPath($penBorder, $pInner)
            $penBorder.Dispose()
            $bInner.Dispose()
            $pInner.Dispose()
        } else {
            # Crisp 1px border at 16px
            $pBorder = New-RoundedRectPath ($ox + $pad + 0.5) ($oy + $pad + 0.5) ($bw - 1.0) ($bw - 1.0) $rad
            $penBorder = New-Object System.Drawing.Pen($CCardBorder, 1.0)
            $g.DrawPath($penBorder, $pBorder)
            $penBorder.Dispose()
            $pBorder.Dispose()
        }

        if ($size -ge 64) {
            $bDial = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(140, 44, 47, 62))
            $g.FillEllipse($bDial, $ox + 72*$s, $oy + 64*$s, 368*$s, 368*$s)
            $bDial.Dispose()
        }
    }

    # Center of gauge arc
    $cx = $ox + 256.0 * $s
    $cy = $oy + $(if ($size -le 24) { 252.0 } else { 248.0 }) * $s
    $arcR = $(if ($size -le 24) { 156.0 } elseif ($size -le 32) { 152.0 } else { 148.0 }) * $s
    $arcStroke = $(if ($size -le 16) { 56.0 } elseif ($size -le 32) { 44.0 } else { 36.0 }) * $s

    $arcRect = [System.Drawing.RectangleF]::new($cx - $arcR, $cy - $arcR, $arcR * 2.0, $arcR * 2.0)

    $penGood = New-Object System.Drawing.Pen($CGood, $arcStroke)
    $penAcc  = New-Object System.Drawing.Pen($CAccent, $arcStroke)
    $penWarn = New-Object System.Drawing.Pen($CWarn, $arcStroke)
    $penGood.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penGood.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $penAcc.StartCap  = [System.Drawing.Drawing2D.LineCap]::Round
    $penAcc.EndCap    = [System.Drawing.Drawing2D.LineCap]::Round
    $penWarn.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penWarn.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

    if ($size -le 24) {
        # Simplified bold 3-zone arc at 16px
        $g.DrawArc($penGood, $arcRect, 142, 70)
        $g.DrawArc($penAcc,  $arcRect, 236, 68)
        $g.DrawArc($penWarn, $arcRect, 328, 70)
    } else {
        $g.DrawArc($penGood, $arcRect, 138, 78)
        $g.DrawArc($penAcc,  $arcRect, 232, 76)
        $g.DrawArc($penWarn, $arcRect, 324, 78)
    }

    $penGood.Dispose()
    $penAcc.Dispose()
    $penWarn.Dispose()

    # Gauge needle pointing toward active upper-right zone (only >= 32px so 16px stays ultra-clean)
    if ($size -ge 32) {
        $needleW = $(if ($size -le 32) { 26.0 } else { 22.0 }) * $s
        $penNeedle = New-Object System.Drawing.Pen($CAccent, $needleW)
        $penNeedle.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $penNeedle.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($penNeedle, $cx, $oy + 242.0*$s, $ox + 336.0*$s, $oy + 170.0*$s)
        $penNeedle.Dispose()
    }

    # Thermometer degree ticks (>= 48px)
    if ($size -ge 48) {
        $penTick = New-Object System.Drawing.Pen($CSub, [Math]::Max(1.5, 10.0 * $s))
        $penTick.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $penTick.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($penTick, $ox + 194*$s, $oy + 204*$s, $ox + 212*$s, $oy + 204*$s)
        $g.DrawLine($penTick, $ox + 190*$s, $oy + 236*$s, $ox + 212*$s, $oy + 236*$s)
        $g.DrawLine($penTick, $ox + 194*$s, $oy + 268*$s, $ox + 212*$s, $oy + 268*$s)
        $penTick.Dispose()
    }

    # Central Thermometer
    $bWhite = New-Object System.Drawing.SolidBrush($CText)
    $bDark  = New-Object System.Drawing.SolidBrush($CBackMain)
    $bRed   = New-Object System.Drawing.SolidBrush($CBad)

    if ($size -le 32) {
        # Simplified high-contrast thermometer for 16px and 32px
        $stemW = $(if ($size -le 16) { 84.0 } else { 72.0 }) * $s
        $stemX = $cx - $stemW / 2.0
        $stemY = $oy + 150.0 * $s
        $stemH = 198.0 * $s
        $pStem = New-RoundedRectPath $stemX $stemY $stemW $stemH ($stemW / 2.0)
        $g.FillPath($bWhite, $pStem)
        $pStem.Dispose()

        $bulbR = $(if ($size -le 16) { 68.0 } else { 62.0 }) * $s
        $bulbY = $oy + 346.0 * $s
        $g.FillEllipse($bWhite, $cx - $bulbR, $bulbY - $bulbR, $bulbR * 2.0, $bulbR * 2.0)

        # Bold coral-red mercury core directly inside white shell at 16-32px
        $mW = $(if ($size -le 16) { 42.0 } else { 34.0 }) * $s
        $mX = $cx - $mW / 2.0
        $mY = $oy + 194.0 * $s
        $mH = 154.0 * $s
        $pMerc = New-RoundedRectPath $mX $mY $mW $mH ($mW / 2.0)
        $g.FillPath($bRed, $pMerc)
        $pMerc.Dispose()

        $mBulbR = $(if ($size -le 16) { 40.0 } else { 34.0 }) * $s
        $g.FillEllipse($bRed, $cx - $mBulbR, $bulbY - $mBulbR, $mBulbR * 2.0, $mBulbR * 2.0)
    } else {
        # Full crisp thermometer shell + dark recess + mercury column
        $stemW = 64.0 * $s
        $stemX = $cx - $stemW / 2.0
        $stemY = $oy + 152.0 * $s
        $stemH = 196.0 * $s
        $pStem = New-RoundedRectPath $stemX $stemY $stemW $stemH ($stemW / 2.0)
        $g.FillPath($bWhite, $pStem)
        $pStem.Dispose()

        $bulbR = 58.0 * $s
        $bulbCy = $oy + 348.0 * $s
        $g.FillEllipse($bWhite, $cx - $bulbR, $bulbCy - $bulbR, $bulbR * 2.0, $bulbR * 2.0)

        # Dark channel
        $chW = 36.0 * $s
        $chX = $cx - $chW / 2.0
        $chY = $oy + 168.0 * $s
        $chH = 180.0 * $s
        $pCh = New-RoundedRectPath $chX $chY $chW $chH ($chW / 2.0)
        $g.FillPath($bDark, $pCh)
        $pCh.Dispose()

        $chBulbR = 42.0 * $s
        $g.FillEllipse($bDark, $cx - $chBulbR, $bulbCy - $chBulbR, $chBulbR * 2.0, $chBulbR * 2.0)

        # Mercury column + bulb
        $mW = 22.0 * $s
        $mX = $cx - $mW / 2.0
        $mY = $oy + 198.0 * $s
        $mH = 150.0 * $s
        $pMerc = New-RoundedRectPath $mX $mY $mW $mH ($mW / 2.0)
        $g.FillPath($bRed, $pMerc)
        $pMerc.Dispose()

        $mBulbR = 30.0 * $s
        $g.FillEllipse($bRed, $cx - $mBulbR, $bulbCy - $mBulbR, $mBulbR * 2.0, $mBulbR * 2.0)

        if ($size -ge 64) {
            $bHi = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(170, 255, 255, 255))
            $hiR = 8.0 * $s
            $g.FillEllipse($bHi, $ox + 246*$s - $hiR, $oy + 338*$s - $hiR, $hiR * 2.0, $hiR * 2.0)
            $bHi.Dispose()
        }
    }

    $bWhite.Dispose()
    $bDark.Dispose()
    $bRed.Dispose()
}

# 1. Generate all icon PNG sizes: 16, 32, 48, 64, 128, 256, 512
$sizes = @(16, 32, 48, 64, 128, 256, 512)
foreach ($sz in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-RmIcon $g 0 0 $sz $false
    $g.Dispose()
    $outPath = Join-Path $OutDir ("rm-icon-{0}.png" -f $sz)
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# 2. Generate rm-avatar.png (512x512 full-bleed square)
$bmpAv = New-Object System.Drawing.Bitmap(512, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gAv = [System.Drawing.Graphics]::FromImage($bmpAv)
Draw-RmIcon $gAv 0 0 512 $true
$gAv.Dispose()
$bmpAv.Save((Join-Path $OutDir "rm-avatar.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$bmpAv.Dispose()

# 3. Generate preview.png (Review sheet: 16..256 on dark + light backgrounds, plus logo lockup)
$pw = 1040
$ph = 940
$bmpPrev = New-Object System.Drawing.Bitmap($pw, $ph, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gp = [System.Drawing.Graphics]::FromImage($bmpPrev)
$gp.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$gp.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$gp.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

$gp.Clear([System.Drawing.Color]::FromArgb(255, 18, 19, 27))

$fontTitle = New-Object System.Drawing.Font("Segoe UI", 14, [System.Drawing.FontStyle]::Bold)
$fontSub   = New-Object System.Drawing.Font("Segoe UI", 9.5, [System.Drawing.FontStyle]::Regular)
$bWhiteTxt = New-Object System.Drawing.SolidBrush($CText)
$bSubTxt   = New-Object System.Drawing.SolidBrush($CSub)
$bDarkTxt  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 40, 44, 60))

# Dark background showcase card
$pDarkCard = New-RoundedRectPath 24 24 992 330 16
$bDarkCard = New-Object System.Drawing.SolidBrush($CBackMain)
$penCard   = New-Object System.Drawing.Pen($CCardBorder, 1.5)
$gp.FillPath($bDarkCard, $pDarkCard)
$gp.DrawPath($penCard, $pDarkCard)
$gp.DrawString("DARK SURFACE (#181822) - ICON SCALE TEST (16px to 256px)", $fontSub, $bSubTxt, 44, 38)

# Light background showcase card
$pLightCard = New-RoundedRectPath 24 370 992 330 16
$bLightCard = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 241, 245, 249))
$gp.FillPath($bLightCard, $pLightCard)
$gp.DrawPath($penCard, $pLightCard)
$gp.DrawString("LIGHT SURFACE (#F1F5F9) - ICON SCALE TEST (16px to 256px)", $fontSub, $bDarkTxt, 44, 384)

$prevSizes = @(16, 32, 48, 64, 128, 256)
$curX = 56
foreach ($psz in $prevSizes) {
    $pngFile = Join-Path $OutDir ("rm-icon-{0}.png" -f $psz)
    $img = [System.Drawing.Image]::FromFile($pngFile)

    # Center vertically in dark row (center Y = 190) and light row (center Y = 536)
    $dyDark  = [int](190 - $psz / 2)
    $dyLight = [int](536 - $psz / 2)
    $rectDark  = New-Object System.Drawing.Rectangle($curX, $dyDark, $psz, $psz)
    $rectLight = New-Object System.Drawing.Rectangle($curX, $dyLight, $psz, $psz)
    $gp.DrawImage($img, $rectDark)
    $gp.DrawImage($img, $rectLight)

    $gp.DrawString(("{0}px" -f $psz), $fontSub, $bSubTxt, $curX, 324)
    $gp.DrawString(("{0}px" -f $psz), $fontSub, $bDarkTxt, $curX, 670)

    $img.Dispose()
    $curX += [Math]::Max($psz, 48) + 46
}

# Bottom horizontal logo lockup card
$pLogoCard = New-RoundedRectPath 24 718 992 196 20
$bLogoCard = New-Object System.Drawing.SolidBrush($CBackInner)
$gp.FillPath($bLogoCard, $pLogoCard)
$gp.DrawPath($penCard, $pLogoCard)

Draw-RmIcon $gp 56 740 152 $false

$fontLogo = New-Object System.Drawing.Font("Segoe UI", 30, [System.Drawing.FontStyle]::Bold)
$fontTag  = New-Object System.Drawing.Font("Segoe UI", 14, [System.Drawing.FontStyle]::Regular)
$gp.DrawString("RESOURCE MONITORING", $fontLogo, $bWhiteTxt, 232, 762)

# Palette pills
$bP1 = New-Object System.Drawing.SolidBrush($CAccent)
$bP2 = New-Object System.Drawing.SolidBrush($CGood)
$bP3 = New-Object System.Drawing.SolidBrush($CWarn)
$bP4 = New-Object System.Drawing.SolidBrush($CBad)
$gp.FillRectangle($bP1, 238, 824, 56, 5)
$gp.FillRectangle($bP2, 302, 824, 36, 5)
$gp.FillRectangle($bP3, 346, 824, 24, 5)
$gp.FillRectangle($bP4, 378, 824, 16, 5)
$bP1.Dispose(); $bP2.Dispose(); $bP3.Dispose(); $bP4.Dispose()

$dot = [char]0x00B7
$gp.DrawString(("laptop  {0}  battery  {0}  network  {0}  temperature" -f $dot), $fontTag, $bSubTxt, 235, 840)

$fontLogo.Dispose(); $fontTag.Dispose()
$fontTitle.Dispose(); $fontSub.Dispose()
$bWhiteTxt.Dispose(); $bSubTxt.Dispose(); $bDarkTxt.Dispose()
$bDarkCard.Dispose(); $bLightCard.Dispose(); $bLogoCard.Dispose(); $penCard.Dispose()
$pDarkCard.Dispose(); $pLightCard.Dispose(); $pLogoCard.Dispose()

$gp.Dispose()
$bmpPrev.Save((Join-Path $OutDir "preview.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$bmpPrev.Dispose()

Write-Host "Resource Monitoring icons and preview generated successfully."
