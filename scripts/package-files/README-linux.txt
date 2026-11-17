DreamGlass cutting optimiser - playground (Linux x64)
=====================================================

Nothing to install. This folder has everything, including .NET.

START
  In a terminal, in the DreamGlass-Playground folder:
      ./start.sh
  Your browser opens the playground (http://localhost:5080). If it does not, open the address
  shown in the terminal. Press Ctrl+C in the terminal to stop the playground.

  Options (after ./start.sh):
      --no-browser                 do not open a browser
      --urls http://0.0.0.0:5080   also allow other computers to reach it (it has no login, so only
                                   do this on a trusted network)

IF THE PAGE LOOKS OLD AFTER AN UPDATE
  Press Ctrl+F5 in the browser once.

NOTES
  - It saves nothing. To keep a job, click "JSON" above the form, copy the text into a file, and
    paste it back later (then "Apply JSON").
  - If 5080 is busy (for example the playground is already open), it uses the next free port and
    shows that address instead.
