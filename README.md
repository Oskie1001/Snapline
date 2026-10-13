# Snapline for Windows

A native Windows screenshot clothesline inspired by [Tendedero](https://github.com/alejandrobujan/tendedero), by Alejandro Buján. This independent implementation uses its own name and icon.

## Run

Download **Snapline-Windows.zip** from the [latest release](https://github.com/Oskie1001/Snapline/releases/latest), extract it, and run **Snapline.exe**. Tagged releases are built and tested by GitHub Actions. The executable is unsigned.

Double-click **dist\Snapline.exe**. Keep **Snapline.exe.config** beside the executable when moving or distributing it. It lives in the notification area; Windows may put the icon under the tray's overflow arrow. Requires Windows 10 or 11 with .NET Framework 4.8 or 4.8.1. No installer, administrator access, account, or network connection is needed.

The executable embeds Windows 10/11 compatibility and per-monitor V2 DPI declarations, runs with normal user permissions, and uses physical screen coordinates for the capture overlay and clothesline. The build uses AnyCPU; Windows 11 x64 is the locally tested platform. Windows on ARM and mixed-DPI monitor interactions have not been tested on separate hardware. Receiving apps control whether image drops and pastes are supported; run both apps at normal privilege levels for cross-app dragging.

Hover within the top five pixels of a monitor to bring the line down with a soft settling bounce. Move outside the upper strip to retract it; retraction starts after about 90 ms and slides upward quickly. Moving back reverses the animation smoothly. **Ctrl+Alt+T** toggles a pinned line; press it again to hide. Use the tray's Show / hide command if another app has registered that shortcut. Automatic reveal is suppressed when the foreground window fills the monitor. Capturing and dragging hide the line immediately.

Take screenshots directly with **Ctrl+Shift+D**, or choose **Take a snip** in the tray menu or the **Capture** button below the line. The tray menu reports whether the capture shortcut registered successfully. Drag a rectangle to capture; Escape or right-click cancels. Snapline saves the capture, copies it to the clipboard, and briefly shows the line. This works without Windows Snipping Tool. Snapline also registers **Win+Shift+S** when that shortcut is available; when Windows already owns it, the Windows snipping overlay handles that shortcut instead.

Clipboard image monitoring is enabled by default and can be disabled in the tray menu. New images saved in **Pictures\Screenshots** are also collected. Choose a different screenshot folder from the tray menu if Windows or OneDrive saves captures elsewhere. Existing files in the watched folder are not imported automatically; use **Add images…** to import them. Identical images already hanging are deduplicated.

| Action | Result |
| --- | --- |
| Click an image | Copy the image to the clipboard |
| Double-click | Open with the Windows default image viewer |
| Hold for at least half a second, then release | Open the drawing editor |
| Drag horizontally along the rope | Move a photo; drag past a neighbor to reorder |
| Pull a photo down off the rope, then drag into an app or Explorer | Drop an image or save a copy; the line hides to expose the target |
| Right-click an image | Copy, open, draw, save, or remove |
| Click the image's cross | Remove its private copy from the line |
| Scroll | Browse older images |
| Right-click empty space | Open the tray menu |

The annotation editor offers freehand drawing, a color picker, undo, reset, save, and cancel. **Start with Windows** is optional in the tray menu. Enable it after placing the executable in its permanent location; disable and re-enable it if you later move the executable.

Photos can be positioned horizontally, including when multiple images hang on the line. Other photos make room when you drag past them, and releasing keeps the frames spaced apart. Positions and order are saved across restarts and adapted to the current monitor width. Pull a photo vertically by about 52 pixels to switch from arranging to sharing. Scroll to arrange older images when there are more photos than fit on the screen.

Dragging offers PNG, bitmap, DIB, and file formats for image editors, document editors, chat attachments, browser upload areas, and Explorer. The receiving app decides which format to accept. Starting a drag also copies the image to the clipboard. If a target does not support image drops, focus its editor and press **Ctrl+V**. Snapline does not send keystrokes into other apps. Click-to-copy also provides PNG and bitmap formats. Images remain on the line after sharing.

## Storage and privacy

Images and preferences stay in `%LOCALAPPDATA%\Snapline`. Images survive restarts and quitting the app. Removing an image deletes only Snapline's private copy; files in your screenshot folder are preserved. Clipboard text, file lists, and images marked with the standard clipboard-monitor exclusion formats are skipped. The app makes no network requests. Quit before removing its storage folder or executable.

## Build and verify

The Windows .NET Framework compiler is used, so no separate SDK or package download is required:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
$process = Start-Process .\dist\Snapline.exe -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
$process.ExitCode
Get-Content .\dist\self-test.txt
```

The execution-policy override applies only to that build process. It does not change your machine's policy. The self-test checks image persistence, dimensions and pixels, file unlocking, supported extensions, duplicate fingerprints, and editor construction and rendering. It never reads your clipboard or starts folder monitoring.

For interactive verification, launch the executable, take a snip, hover at the top edge, click to copy, paste into Paint, drag to Explorer, annotate an image, and restart to confirm persistence. Verify reveal on each monitor and suppression over a fullscreen application. These desktop interactions require manual verification.

## Scope

This implements the original's core screenshot workflow using Windows APIs. Screen recording, video trimming, pressure-sensitive quick look, automatic removal after dragging to a folder, configurable shortcuts, and translations are not implemented. Dragging always sends a copy. Windows Snipping Tool's save location and thumbnail behavior are not changed. The line uses rounded neutral frames, silver clips, a curved rope, and spring motion for reveal and retraction.

## Credits and license

Inspired by Tendedero, copyright 2026 Alejandro Buján. No original source code or artwork is included. Snapline's source is MIT licensed; see LICENSE. Snapline is independent and is not endorsed by Tendedero's author.
