DreamGlass cutting optimiser - playground (macOS)
=================================================

Nothing to install. This folder has everything, including .NET.
Pick the right download: "osx-arm64" for Apple silicon (M1, M2, M3, M4 ...), "osx-x64" for older
Intel Macs. (Apple menu > About This Mac shows "Chip: Apple ..." or "Processor: Intel ...".)

FIRST TIME ONLY (macOS blocks programs downloaded from the internet that are not from the App Store)
  1. Open Terminal (Applications > Utilities > Terminal).
  2. Type the following, with a space at the end, but do not press Enter yet:
         xattr -dr com.apple.quarantine
  3. Drag the DreamGlass-Playground folder from Finder into the Terminal window, then press Enter.
  You only need to do this once per download.

START
  Double-click "Start DreamGlass.command" in the DreamGlass-Playground folder.
  A Terminal window opens and your browser opens the playground (http://localhost:5080).
  Keep the Terminal window open while you use it. Close it (or press Ctrl+C) to stop the playground.

IF MACOS STILL SAYS IT CANNOT BE OPENED
  Open System Settings > Privacy & Security, scroll down and click "Open Anyway" next to the
  message about "Start DreamGlass.command" or "DreamGlass.Playground", then try again.

IF THE BROWSER DOES NOT OPEN
  Open your browser yourself and go to the address shown in the Terminal window
  (usually http://localhost:5080).

IF THE PAGE LOOKS OLD AFTER AN UPDATE
  Press Cmd+Shift+R in the browser once.

NOTES
  - The playground only runs on this computer. Other computers on the network cannot reach it.
  - It saves nothing. To keep a job, click "JSON" above the form, copy the text into a file, and
    paste it back later (then "Apply JSON").
  - If 5080 is busy (for example the playground is already open), it uses the next free port and
    shows that address instead.
