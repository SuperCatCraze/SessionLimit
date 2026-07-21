An always-on-top overlay for live Claude usage: session and weekly percentages, burn rate
with a projected time to your limit, per-model and per-project breakdown, and threshold
alerts before you run out.

## Install

Download **SessionLimit.exe** below ({{SIZE}} MB) and run it. Nothing else is needed — .NET
is bundled, and it never asks for administrator rights.

For Start Menu and desktop shortcuts plus launch-on-login, run `install.ps1` from the repo,
or just tick **Start with Windows** in the app's ⚙ settings.

It keeps itself up to date from here: the footer shows the version you are running and a ↻
that checks for new releases, and updating is one button.

## Verify the download

```powershell
(Get-FileHash SessionLimit.exe -Algorithm SHA256).Hash.ToLower()
```

should print `{{SHA}}`

## Requirements

Windows 10 or 11, 64-bit. Real plan percentages come from Claude Code, so they need it
installed; without it the bars fall back to your own token budget and say so.

---

{{CHANGES}}
