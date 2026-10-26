# Ptty PC Test Monitor

A lightweight, portable Windows monitor for PC stress tests and game benchmarks.
Keep it open next to TestMem5, OCCT, Cinebench, 3DMark or a game to watch CPU, GPU, RAM, fans, FPS and network
speed, each with current / min / average / max values.

PC 스트레스 테스트와 게임 벤치마크용 가벼운 휴대용 모니터링 프로그램. TM5, OCCT, 시네벤치, 3DMark, 게임과 함께 실행해서
CPU·GPU·RAM·팬·FPS·네트워크 속도를 현재 / 최소 / 평균 / 최대 값으로 볼 수 있음.

## Features

- **Single exe, no install.** Uses the .NET Framework 4.8 that ships with Windows 10/11. Runs as administrator.
- **CPU:** usage, temperature, package power, clock
- **GPU (NVIDIA and AMD):** usage, temperature, hot spot, power, VRAM. Works with only the graphics driver installed.
- **RAM:** used / total, usage, DIMM temperature (modules with an SPD thermal sensor, mostly DDR5)
- **Fans:** GPU fans, motherboard fan headers, USB AIO coolers (e.g. NZXT Kraken)
- **FPS:** frame rate, 1% low and frame time of the foreground app, measured through ETW present events
  (the PresentMon method). Nothing is injected into the game, so it is safe with anti-cheat.
- **Network:** download / upload speed
- Usage, temperature and FPS graphs, per item below the values or faintly behind them, stacked / side by side /
  overlapped, with adjustable split, height and strength
- CSV logging, adjustable size / opacity / always-on-top, per-item show/hide
- **Free layout:** arrange the panels on a grid and set how many cells each spans (RTSS-style editor);
  fully empty columns and rows close automatically. Double-click a block to open its panel and drag the
  value columns (current/min/avg/max) and rows into the order you want
- **Compact view:** same layout, values shortened to `current ↓min ~avg ↑max`
- Right-click for quick view-mode and opacity-slider toggles

## Requirements

- Windows 10 or 11, x64
- Administrator rights (UAC prompt on start)
- For CPU temperature, RAM temperature and motherboard fans: the [PawnIO](https://pawnio.eu) driver.
  The app shows a banner when it's missing and can install it with `winget install namazso.PawnIO`.

## Usage

Download `PttyPcTestMonitor.exe` from Releases and run it. See [docs/사용법.txt](docs/사용법.txt) (Korean) for
all controls and menu items.

- Drag to move, drag the edges to resize (width zooms, height grows the graphs)
- Right-click → Options: view mode, items / graphs / value columns, layout editor, opacity, always on top,
  CSV logging, tools
- Exclusive-fullscreen games and benchmarks cover every overlay window. Use a second monitor, borderless/windowed
  mode, or turn on CSV logging and check the log afterwards.

Troubleshooting: Options → 기록·도구 → "센서 목록 저장" (save sensor list), or
`PttyPcTestMonitor.exe --dump out.txt`. `--render <folder>` saves PNG previews of every layout with sample data.

## Building

Requires the .NET SDK (8 or later).

```powershell
./build.ps1
```

The output is `dist/PttyPcTestMonitor.exe` (dependencies embedded) and a release zip.

## License

MIT, see [LICENSE](LICENSE). Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
