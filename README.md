# Album Wall

A Windows screensaver that turns your monitor into a slow-drifting wall of album covers.

Rows of covers glide sideways, alternating direction, at slightly different speeds. Point it at any folder of artwork and leave the room.

## Features

- Uses your own covers: any folder of `.jpg`, `.jpeg`, `.png` or `.bmp` images, searched including subfolders. It defaults to your Music folder.
- 2 to 10 rows, at slow, medium or fast speed.
- Works full screen, and in the small preview box in Windows' Screen Saver Settings.
- Any key or mouse movement closes it, like every screensaver.
- One small file, no installer, no extra software.

## Build

You need nothing beyond Windows. It compiles with the C# compiler that ships with the .NET Framework:

```bat
build.cmd
```

This produces `AlbumWall.scr`.

## Install

1. Right-click `AlbumWall.scr` and choose **Install**. You can also copy it to `C:\Windows\System32` and pick it in Screen Saver Settings.
2. Click **Settings…** to choose the folder of covers, the number of rows and the speed.

## How it works

A `.scr` file is an ordinary Windows program that the system starts with one of these arguments:

| Argument | What happens |
|---|---|
| `/s` | Runs full screen |
| `/p <hwnd>` | Draws inside the preview box in Screen Saver Settings |
| `/c` (or none) | Opens the settings window |
| `/snapshot <file.png>` | Renders one frame to an image, for testing |

Settings are stored in the registry under `HKEY_CURRENT_USER\Software\AlbumWall`.

Built with C#, WPF and Windows Forms on .NET Framework 4.
